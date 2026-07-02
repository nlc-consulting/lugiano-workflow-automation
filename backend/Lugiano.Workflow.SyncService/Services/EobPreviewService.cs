using ClosedXML.Excel;
using Dapper;
using Lugiano.Workflow.SyncService.ChiroTouch;

namespace Lugiano.Workflow.SyncService.Services;

// Dry-run EOB posting preview. Reads an uploaded EOB Line Items workbook
// (15-column schema — see memory/reference_eob_samples.md), matches each line
// against a PSChiro Transactions charge row, and returns what an UPDATE would
// look like. NEVER writes back. The "Apply" path is a follow-up that needs
// broader lugiano_rw permissions and a separate write service.
public sealed class EobPreviewService
{
    private readonly ISourceDbConnectionFactory _sourceDb;
    private readonly ILogger<EobPreviewService> _logger;

    public EobPreviewService(ISourceDbConnectionFactory sourceDb, ILogger<EobPreviewService> logger)
    {
        _sourceDb = sourceDb;
        _logger = logger;
    }

    public async Task<EobPreviewResult> PreviewAsync(Stream xlsxStream, CancellationToken ct)
    {
        var allLines = ParseWorkbook(xlsxStream);
        // Drop noise rows: nothing to post if the carrier paid $0 AND wrote
        // off $0 on the line. These are typically pending claims or pure
        // information lines on the EOB — they'd never produce a CT posting
        // anyway. Filtering here keeps the preview focused on actionable
        // lines and the counts honest.
        var lines = allLines
            .Where(l => l.PaidAmount > 0 || l.WriteOffAmount > 0)
            .ToList();
        if (lines.Count == 0)
        {
            return new EobPreviewResult(
                TotalLines: 0,
                Matched: Array.Empty<MatchedLine>(),
                Ambiguous: Array.Empty<AmbiguousLine>(),
                Unmatched: Array.Empty<UnmatchedLine>());
        }

        // Pre-fetch all candidate PSChiro charges in one round-trip — for each
        // unique (PatientName-normalized, DOS, CPT) tuple we'll need to score
        // against TranAmt. Names normalize to UPPER + collapsed whitespace.
        var distinctPatientNames = lines
            .Select(l => NormalizeName(l.PatientName))
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .ToList();

        await using var conn = _sourceDb.Create();

        // Look up PSChiro patients by normalized name. ChiroTouch stores names
        // as separate FirstName/LastName columns; we concatenate + normalize
        // server-side so the IN filter stays cheap. Multiple Patients rows can
        // share a name (case-per-injury model) — match returns ALL candidate
        // patient ids per name; downstream join filters by DOS.
        var patientLookup = await LookupPatientsByNameAsync(conn, distinctPatientNames);

        // For each EOB line, find candidate transactions.
        var matched = new List<MatchedLine>();
        var ambiguous = new List<AmbiguousLine>();
        var unmatched = new List<UnmatchedLine>();

        // Cache fuzzy lookups per normalized EOB name so we don't re-query
        // for every line of the same patient. Also remembers whether the
        // top suggestion was confident enough to auto-resolve transactions.
        var fuzzyCache = new Dictionary<string, (IReadOnlyList<PatientSuggestion> Suggestions, int? AutoPatientId)>();

        foreach (var line in lines)
        {
            var normName = NormalizeName(line.PatientName);
            if (!patientLookup.TryGetValue(normName, out var patientIds) || patientIds.Count == 0)
            {
                // Strict patient match failed. Try fuzzy + auto-resolve before
                // dropping into Unmatched. Rule: ONE suggestion at score ≥ 95
                // AND the per-line transaction match comes back clean (exact
                // amount). Anything fuzzier stays as chips for human review.
                if (!fuzzyCache.TryGetValue(normName, out var fuzzy))
                {
                    var sugg = await SuggestPatientsAsync(line.PatientName);
                    int? auto = (sugg.Count > 0 && sugg[0].Score >= 95
                                 && (sugg.Count == 1 || sugg[1].Score < sugg[0].Score - 10))
                        ? sugg[0].PatientId
                        : null;
                    fuzzy = (sugg, auto);
                    fuzzyCache[normName] = fuzzy;
                }

                if (fuzzy.AutoPatientId is int autoPid)
                {
                    var auto = await ResolveLineWithPatientAsync(line, autoPid, ct);
                    if (auto.Matched is not null)
                    {
                        matched.Add(auto.Matched);
                        continue;
                    }
                    if (auto.Ambiguous is not null)
                    {
                        // Fuzzy patient hit, but multiple txns share that
                        // DOS+CPT. Operator picks which one.
                        ambiguous.Add(auto.Ambiguous);
                        continue;
                    }
                    if (auto.Unmatched is not null)
                    {
                        // Fuzzy patient hit, no transaction found at all.
                        // Surface the specific "no transaction" reason but
                        // keep the original suggestions so the operator can
                        // try a different patient if our top guess is off.
                        unmatched.Add(auto.Unmatched with { Suggestions = fuzzy.Suggestions });
                        continue;
                    }
                }

                unmatched.Add(new UnmatchedLine(line, "Patient not found in PSChiro", fuzzy.Suggestions));
                continue;
            }

            // Charges with exact (patient, DOS, CPT, charge amount). Fast filter
            // first, then score in memory. DOS comparison is date-only (CT's
            // ledger view, our scrubber, and our existing join all use
            // .Date — see PatientDetailQueries notes).
            var candidates = await conn.QueryAsync<TxCandidate>(
                """
                SELECT ID         AS TranId,
                       PatID,
                       TranDate,
                       Code,
                       TranAmt,
                       ISNULL(PriPaidAmt, 0) AS PriPaidAmt,
                       ISNULL(WOAmt,      0) AS WOAmt
                FROM   dbo.Transactions
                WHERE  PatID IN @patientIds
                  AND  TranType    = 'C'
                  AND  TranSubType = 'SV'
                  AND  CAST(TranDate AS date) = @dos
                  AND  Code = @code;
                """,
                new
                {
                    patientIds,
                    dos = line.DateOfService?.Date,
                    code = line.ProceduralCode,
                });

            var candList = candidates.ToList();
            if (candList.Count == 0)
            {
                unmatched.Add(new UnmatchedLine(line, "No PSChiro charge matched (patient + DOS + CPT)"));
                continue;
            }

            // Exact-amount filter (the cleanest match signal). If exactly one
            // row matches on charge amount too, it's a 1.0-confidence match.
            var exactAmt = candList
                .Where(c => c.TranAmt == line.IndividualCharge)
                .ToList();

            if (exactAmt.Count == 1)
            {
                matched.Add(new MatchedLine(line, exactAmt[0], ProposedUpdate.From(exactAmt[0], line)));
            }
            else if (exactAmt.Count > 1)
            {
                ambiguous.Add(new AmbiguousLine(line, exactAmt,
                    "Multiple PSChiro charges share patient + DOS + CPT + amount"));
            }
            else
            {
                ambiguous.Add(new AmbiguousLine(line, candList,
                    "PSChiro charge(s) found on same DOS + CPT but no matching amount"));
            }
        }

        return new EobPreviewResult(
            TotalLines: lines.Count,
            Matched: matched,
            Ambiguous: ambiguous,
            Unmatched: unmatched);
    }

    // Parses an EOB Line Items workbook. Expects the 15-column schema from
    // memory/reference_eob_samples.md (first row is header). Tolerates blank
    // rows and missing optional columns; bails per-row on bad data.
    private static List<EobLine> ParseWorkbook(Stream xlsxStream)
    {
        using var wb = new XLWorkbook(xlsxStream);
        var ws = wb.Worksheet(1);
        var lines = new List<EobLine>();

        // Build header index — case-insensitive, whitespace-trimmed lookup.
        var headerRow = ws.FirstRowUsed();
        if (headerRow is null) return lines;

        var headerIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
        {
            var name = (cell.GetString() ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(name)) headerIdx[name] = cell.Address.ColumnNumber;
        }

        int? Col(string name) => headerIdx.TryGetValue(name, out var c) ? c : null;
        var iPatient = Col("Patient Name");
        var iDos = Col("Date of Service");
        var iCpt = Col("Procedural Code");
        var iCharge = Col("Individual Charge");
        var iWriteOff = Col("Write Off Amount");
        var iPaid = Col("Paid Amount");
        var iAllowed = Col("Allowed Charge");
        var iReasonCode = Col("Reason Code");
        var iReasonDesc = Col("Reason Description");
        var iClaim = Col("Claim Number");
        var iBill = Col("Bill Number");
        var iCheck = Col("Associated Check Number");

        if (iPatient is null || iDos is null || iCpt is null || iCharge is null)
            throw new InvalidOperationException(
                "EOB workbook missing required columns (Patient Name, Date of Service, Procedural Code, Individual Charge).");

        foreach (var row in ws.RowsUsed().Skip(1))
        {
            string Get(int? i) => i.HasValue ? (row.Cell(i.Value).GetString() ?? string.Empty).Trim() : string.Empty;
            decimal? GetDec(int? i)
            {
                if (i is null) return null;
                var cell = row.Cell(i.Value);
                if (cell.IsEmpty()) return null;
                return cell.TryGetValue(out double d) ? (decimal?)Convert.ToDecimal(d) : null;
            }
            DateTime? GetDate(int? i)
            {
                if (i is null) return null;
                var cell = row.Cell(i.Value);
                if (cell.IsEmpty()) return null;
                if (cell.TryGetValue(out DateTime dt)) return dt;
                var s = cell.GetString();
                return DateTime.TryParse(s, out var parsed) ? parsed : null;
            }

            var patient = Get(iPatient);
            var cpt = Get(iCpt);
            if (string.IsNullOrEmpty(patient) && string.IsNullOrEmpty(cpt)) continue; // skip blank

            lines.Add(new EobLine(
                PatientName: patient,
                DateOfService: GetDate(iDos),
                ProceduralCode: cpt,
                IndividualCharge: GetDec(iCharge) ?? 0m,
                WriteOffAmount: GetDec(iWriteOff) ?? 0m,
                PaidAmount: GetDec(iPaid) ?? 0m,
                AllowedCharge: GetDec(iAllowed) ?? 0m,
                ReasonCode: Get(iReasonCode),
                ReasonDescription: Get(iReasonDesc),
                ClaimNumber: Get(iClaim),
                BillNumber: Get(iBill),
                CheckNumber: Get(iCheck)));
        }

        return lines;
    }

    private static async Task<Dictionary<string, List<int>>> LookupPatientsByNameAsync(
        System.Data.Common.DbConnection conn, List<string> normalizedNames)
    {
        var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        if (normalizedNames.Count == 0) return result;

        // Pull all matching patients in one query — ChiroTouch's First/Last
        // split + uppercase + whitespace collapse on the server. Multiple
        // patient rows may share a name (case-per-injury), so the dict value
        // is a list, not a single id.
        var rows = await conn.QueryAsync<(int Id, string? First, string? Last)>(
            """
            SELECT ID, FirstName, LastName
            FROM   dbo.Patients
            WHERE  LTRIM(RTRIM(UPPER(FirstName + ' ' + LastName))) IN @names
               OR  LTRIM(RTRIM(UPPER(LastName  + ' ' + FirstName))) IN @names;
            """,
            new { names = normalizedNames });

        foreach (var r in rows)
        {
            // Index under both First-Last and Last-First orderings so the
            // EOB name shape doesn't matter.
            var fl = NormalizeName($"{r.First} {r.Last}");
            var lf = NormalizeName($"{r.Last} {r.First}");
            foreach (var key in new[] { fl, lf })
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (!result.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    result[key] = list;
                }
                if (!list.Contains(r.Id)) list.Add(r.Id);
            }
        }
        return result;
    }

    // Patient lookup by ChiroTouch AccountNo — the operator's manual override
    // when fuzzy suggestions miss. Returns null if no patient has that
    // AccountNo. Used by the "Map to CT #" dialog: operator types the account
    // number staff use day-to-day, we resolve it to the internal PSChiro
    // PatientID and surface the matched patient for confirmation.
    public async Task<PatientLookupHit?> LookupPatientByAccountNoAsync(int accountNo, CancellationToken ct = default)
    {
        await using var conn = _sourceDb.Create();
        return await conn.QuerySingleOrDefaultAsync<PatientLookupHit>(
            """
            SELECT TOP 1
              p.ID                                                    AS PatientId,
              p.AccountNo                                             AS AccountNo,
              LTRIM(RTRIM(p.LastName + ', ' + p.FirstName))           AS FullName,
              p.BirthDate                                             AS BirthDate
            FROM   dbo.Patients p
            WHERE  p.AccountNo = @accountNo;
            """,
            new { accountNo });
    }

    // Re-resolves a single EOB line against a chosen PSChiro patient — used
    // when the operator clicks a fuzzy suggestion in the UI. Returns the
    // line's new bucket (matched / ambiguous / still unmatched) so the
    // frontend can move the row out of "Unmatched" and into the right place.
    public async Task<EobResolveResult> ResolveLineWithPatientAsync(
        EobLine line, int patientId, CancellationToken ct = default)
    {
        await using var conn = _sourceDb.Create();
        var candidates = (await conn.QueryAsync<TxCandidate>(
            """
            SELECT ID         AS TranId,
                   PatID,
                   TranDate,
                   Code,
                   TranAmt,
                   ISNULL(PriPaidAmt, 0) AS PriPaidAmt,
                   ISNULL(WOAmt,      0) AS WOAmt
            FROM   dbo.Transactions
            WHERE  PatID       = @patientId
              AND  TranType    = 'C'
              AND  TranSubType = 'SV'
              AND  CAST(TranDate AS date) = @dos
              AND  Code = @code;
            """,
            new { patientId, dos = line.DateOfService?.Date, code = line.ProceduralCode })).ToList();

        if (candidates.Count == 0)
            return new EobResolveResult(
                Matched: null,
                Ambiguous: null,
                Unmatched: new UnmatchedLine(line,
                    "No PSChiro charge matched (patient + DOS + CPT) for the selected patient",
                    null));

        var exactAmt = candidates.Where(c => c.TranAmt == line.IndividualCharge).ToList();
        if (exactAmt.Count == 1)
            return new EobResolveResult(
                Matched: new MatchedLine(line, exactAmt[0], ProposedUpdate.From(exactAmt[0], line)),
                Ambiguous: null, Unmatched: null);

        if (exactAmt.Count > 1)
            return new EobResolveResult(
                Matched: null,
                Ambiguous: new AmbiguousLine(line, exactAmt,
                    "Multiple PSChiro charges share patient + DOS + CPT + amount"),
                Unmatched: null);

        return new EobResolveResult(
            Matched: null,
            Ambiguous: new AmbiguousLine(line, candidates,
                "PSChiro charge(s) found on same DOS + CPT but no matching amount"),
            Unmatched: null);
    }

    // Fuzzy patient suggestions for EOB lines whose name didn't strict-match.
    // Strategy: try both name-order interpretations ("First Last" and
    // "Last First" — the comma in "ZAMBRANO, AMADO" vs "AMADO ZAMBRANO"
    // matters), pull a candidate set keyed on each token's first 3 chars
    // (index-friendly), then rank in memory with Levenshtein distance.
    // Suppress low-confidence matches entirely — surfacing wrong suggestions
    // is worse than no suggestions.
    public async Task<IReadOnlyList<PatientSuggestion>> SuggestPatientsAsync(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return Array.Empty<PatientSuggestion>();

        var hasComma = rawName.Contains(',');
        var parts = NormalizeName(rawName)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Array.Empty<PatientSuggestion>();

        // Two candidate (last, first) interpretations of the input.
        // Comma in raw -> author meant "Last, First" — that one wins.
        // Otherwise default to "First Last" (last token = surname) since
        // that's the dominant EOB convention. We score both and keep the
        // better, so wrong guess is recoverable.
        var ifComma = (Last: parts[0], First: parts.Length > 1 ? parts[^1] : "");
        var ifSpace = (Last: parts[^1], First: parts.Length > 1 ? parts[0] : "");
        var interps = hasComma ? new[] { ifComma, ifSpace } : new[] { ifSpace, ifComma };

        // Pull candidates keyed on the first 3 chars of EITHER token —
        // catches the "right surname, wrong order" case. Bounded TOP 100 so
        // we never scan the whole patient table.
        var tokens = parts.Distinct().Where(p => p.Length >= 3).ToList();
        if (tokens.Count == 0) return Array.Empty<PatientSuggestion>();
        var prefixes = tokens.Select(t => t[..3]).Distinct().ToList();

        // Build the OR-clause dynamically — PSChiro is on SQL Server 2008
        // compat level, so STRING_SPLIT (2016+) isn't available. Explicit
        // @p0..@pN parameters keep it parameterized.
        //
        // Substring LIKE (%token%) catches patients with middle names that
        // the EOB drops ("Amado" in EOB vs "Amado Mejia" in PSChiro) — the
        // prefix-only LIKE missed those. Defeats indexes but the Patients
        // table is small enough for a full scan per unique unmatched name.
        // Also bumped TOP 100 -> 300 so we don't truncate older patient IDs
        // when the prefix is common.
        var args = new DynamicParameters();
        var clauses = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            args.Add($"t{i}", tokens[i]);
            clauses.Add($"p.LastName LIKE '%' + @t{i} + '%' OR p.FirstName LIKE '%' + @t{i} + '%'");
        }
        var whereSql = string.Join(" OR ", clauses);

        await using var conn = _sourceDb.Create();
        var rows = (await conn.QueryAsync<(int Id, string? First, string? Last)>(
            $"SELECT TOP 300 p.ID, p.FirstName, p.LastName FROM dbo.Patients p WHERE {whereSql} ORDER BY p.ID DESC;",
            args)).ToList();

        // Score every candidate against every interpretation; keep the best.
        // Last-name match weighs 3x first-name (last names typo less often).
        var scored = new List<PatientSuggestion>();
        foreach (var r in rows)
        {
            var pLast  = (r.Last  ?? "").Trim().ToUpperInvariant();
            var pFirst = (r.First ?? "").Trim().ToUpperInvariant();
            int best = 0;
            foreach (var (last, first) in interps)
            {
                var lastSim  = NameSim(last,  pLast);
                var firstSim = string.IsNullOrEmpty(first) ? 0 : NameSim(first, pFirst);
                var combined = (lastSim * 3 + firstSim) / 4;
                if (combined > best) best = combined;
            }
            // Quality gate: only surface suggestions that are clearly close.
            // 70 ≈ "one typo in either name" or "same surname, different
            // first-name spelling". Below that = noise, drop it.
            if (best >= 70)
                scored.Add(new PatientSuggestion(
                    r.Id, $"{r.Last?.Trim()}, {r.First?.Trim()}".Trim(' ', ','), best));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.FullName)
            .Take(5)
            .ToList();
    }

    // Name similarity 0-100. Exact = 100; substring containment (e.g.
    // EOB "Amado" vs PSChiro "Amado Mejia") = 95, since one is a clean
    // prefix/suffix of the other and that's a much stronger signal than
    // Levenshtein gives credit for; per-character edit drops the rest.
    // Empty/null on either side returns 0.
    private static int NameSim(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        if (a == b) return 100;
        // Either is contained in the other — middle names, hyphenated
        // surnames the EOB shortened, etc. Capped at 95 (not 100) so an
        // exact match still beats a contained-substring match.
        if (a.Contains(b) || b.Contains(a)) return 95;
        var d = Levenshtein(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 0;
        var sim = 100 - (100 * d / maxLen);
        return sim < 0 ? 0 : sim;
    }

    private static int Levenshtein(string a, string b)
    {
        var n = a.Length; var m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }

    private static string NormalizeName(string? raw)
    {
        // EOB workbooks frequently use "Last, First" with a comma; PSChiro
        // stores First/Last as separate columns we concatenate with a space.
        // Strip punctuation (commas, periods, hyphens) to a single space
        // before collapsing whitespace so both orderings normalize to the
        // same shape ("TEST FAKEE" or "FAKEE TEST" — caller indexes both).
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var cleaned = new string(raw.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        return string.Join(' ', cleaned.ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

public sealed record EobLine(
    string PatientName,
    DateTime? DateOfService,
    string ProceduralCode,
    decimal IndividualCharge,
    decimal WriteOffAmount,
    decimal PaidAmount,
    decimal AllowedCharge,
    string ReasonCode,
    string ReasonDescription,
    string ClaimNumber,
    string BillNumber,
    string CheckNumber);

public sealed record TxCandidate(
    int TranId,
    int PatID,
    DateTime TranDate,
    string? Code,
    decimal TranAmt,
    decimal PriPaidAmt,
    decimal WOAmt);

// What the UPDATE would look like if we wrote back. Field-level diff so the UI
// can render before/after side-by-side. NOT executed — preview-only.
public sealed record ProposedUpdate(
    int TranId,
    decimal CurrentPriPaidAmt,
    decimal ProposedPriPaidAmt,
    decimal CurrentWOAmt,
    decimal ProposedWOAmt,
    string ReasonCode,
    string ReasonDescription)
{
    public static ProposedUpdate From(TxCandidate tx, EobLine line) => new(
        TranId: tx.TranId,
        CurrentPriPaidAmt: tx.PriPaidAmt,
        ProposedPriPaidAmt: tx.PriPaidAmt + line.PaidAmount,
        CurrentWOAmt: tx.WOAmt,
        ProposedWOAmt: tx.WOAmt + line.WriteOffAmount,
        ReasonCode: line.ReasonCode,
        ReasonDescription: line.ReasonDescription);
}

public sealed record MatchedLine(EobLine Line, TxCandidate Match, ProposedUpdate Proposed);
public sealed record AmbiguousLine(EobLine Line, IReadOnlyList<TxCandidate> Candidates, string Reason);
// Suggestions surface fuzzy patient-name matches for lines that the strict
// name match missed. UI shows them as clickable chips; clicking re-runs the
// match constrained to the chosen patient (via /eob/resolve-line) and
// promotes the line into the right bucket.
public sealed record UnmatchedLine(
    EobLine Line,
    string Reason,
    IReadOnlyList<PatientSuggestion>? Suggestions = null);

public sealed record PatientSuggestion(int PatientId, string FullName, int Score);

// Result shape for /eob/lookup-patient — populated when AccountNo resolves
// to a single PSChiro patient. Frontend shows FullName + BirthDate for the
// operator to confirm before applying.
public sealed record PatientLookupHit(
    int PatientId,
    int? AccountNo,
    string? FullName,
    DateTime? BirthDate);

// Result shape for /eob/resolve-line — exactly one of the three is populated
// (matched / ambiguous / unmatched) so the frontend can move the row into
// the right bucket.
public sealed record EobResolveResult(
    MatchedLine? Matched,
    AmbiguousLine? Ambiguous,
    UnmatchedLine? Unmatched);

public sealed record EobPreviewResult(
    int TotalLines,
    IReadOnlyList<MatchedLine> Matched,
    IReadOnlyList<AmbiguousLine> Ambiguous,
    IReadOnlyList<UnmatchedLine> Unmatched);
