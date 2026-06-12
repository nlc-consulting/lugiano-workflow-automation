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
        var lines = ParseWorkbook(xlsxStream);
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

        foreach (var line in lines)
        {
            var normName = NormalizeName(line.PatientName);
            if (!patientLookup.TryGetValue(normName, out var patientIds) || patientIds.Count == 0)
            {
                unmatched.Add(new UnmatchedLine(line, "Patient not found in PSChiro"));
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

    private static string NormalizeName(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? string.Empty
            : string.Join(' ', raw.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
public sealed record UnmatchedLine(EobLine Line, string Reason);

public sealed record EobPreviewResult(
    int TotalLines,
    IReadOnlyList<MatchedLine> Matched,
    IReadOnlyList<AmbiguousLine> Ambiguous,
    IReadOnlyList<UnmatchedLine> Unmatched);
