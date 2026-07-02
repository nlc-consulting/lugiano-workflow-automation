using System.Globalization;
using System.Text.Json;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Services;
using Lugiano.Workflow.SyncService.Services.Scrubbing;
using Lugiano.Workflow.SyncService.Util;
using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Controllers;

[ApiController]
[Route("cases")]
public sealed class CasesController : ControllerBase
{
    private readonly IDbContextFactory<WorkflowDbContext> _factory;
    private readonly IPatientDetailQueries _detail;
    private readonly IChartNoteReadQueries _noteReads;
    private readonly WorkflowCaseService _cases;
    private readonly ScrubOrchestrator _scrubs;
    private readonly CorrectionRequestService _corrections;
    private readonly IPSChiroWriteService _pschiroWrite;
    private readonly ILogger<CasesController> _logger;

    public CasesController(
        IDbContextFactory<WorkflowDbContext> factory,
        IPatientDetailQueries detail,
        IChartNoteReadQueries noteReads,
        WorkflowCaseService cases,
        ScrubOrchestrator scrubs,
        CorrectionRequestService corrections,
        IPSChiroWriteService pschiroWrite,
        ILogger<CasesController> logger)
    {
        _factory = factory;
        _detail = detail;
        _noteReads = noteReads;
        _cases = cases;
        _scrubs = scrubs;
        _corrections = corrections;
        _pschiroWrite = pschiroWrite;
        _logger = logger;
    }

    // GET /cases — dashboard: the portal's workflow record, newest-first, with per-flow stamps.
    // ra-data-simple-rest: ?range=[start,end] + Content-Range header.
    [HttpGet]
    public async Task<IActionResult> GetCases(
        [FromQuery] string? range, [FromQuery] string? sort, [FromQuery] string? filter)
    {
        var (skip, take) = ParseRange(range);
        if (take <= 0 || take > 200) take = 25;
        var (sortField, sortDesc) = ParseSort(sort);
        var officeFilter = ParseOfficeFilter(filter);

        await using var db = await _factory.CreateDbContextAsync();

        // Load ALL cases — several visible columns (LastNoteDate, insurance
        // balance, latest scrub) are computed from joined data, so we can't
        // sort + paginate at the SQL level without denormalizing those onto
        // WorkflowCase. With ~700 cases the in-memory sort is sub-millisecond.
        // When this becomes a problem, pre-compute the joined fields on the
        // WorkflowCase row (or move pagination to a SQL view).
        var cases = await db.WorkflowCases.AsNoTracking().ToListAsync();

        var caseIds = cases.Select(c => c.Id).ToList();
        var patientIds = cases.Select(c => c.PatientId).ToList();

        // Per-note scrubs aggregated to a case-level chip. For each case:
        // take each note's LATEST scrub, then roll up:
        //   any fail        -> case = fail
        //   else needs_review -> case = needs_review
        //   else (all pass and every note scrubbed) -> case = pass
        //   else (some notes unscrubbed and no known issues) -> null (partial)
        // The full per-note breakdown lives on the case detail.
        var perNoteScrubs = await db.ScrubResults.AsNoTracking()
            .Where(s => s.DoctorNoteId != null && caseIds.Contains(s.WorkflowCaseId))
            .Select(s => new { s.WorkflowCaseId, NoteId = s.DoctorNoteId!.Value, s.Verdict, s.RanAt })
            .ToListAsync();

        // Distinct notes with at least one scrub.
        var latestByNote = perNoteScrubs
            .GroupBy(s => new { s.WorkflowCaseId, s.NoteId })
            .Select(g => g.OrderByDescending(s => s.RanAt).First())
            .ToList();

        // Latest note per case by clinical NoteDate (tiebreak on Id — newer Id
        // wins on same-date). Drives LastNoteDate + the scrub rollup. EF Core 8
        // doesn't translate `g.OrderBy(...).First()` projections inside GroupBy
        // (EmptyProjectionMember), so we pull the rows and group in memory —
        // bounded by page size × ~50 notes per case, cheap.
        var notesForCases = await db.DoctorNotes.AsNoTracking()
            .Where(n => caseIds.Contains(n.WorkflowCaseId))
            .Select(n => new { CaseId = n.WorkflowCaseId, NoteId = n.Id, n.NoteDate, n.ChartNoteId })
            .ToListAsync();
        var latestNotePerCase = notesForCases
            .GroupBy(n => n.CaseId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var latest = g.OrderByDescending(n => n.NoteDate).ThenByDescending(n => n.NoteId).First();
                    return new { latest.NoteId, latest.NoteDate, latest.ChartNoteId };
                });

        // ChiroTouch stores NoteDate as a date only (always midnight), so the
        // real "when it came in" clock is the signature timestamp. Pull the
        // signed time for each case's latest note so LastNoteDate can show it.
        var latestChartNoteIds = latestNotePerCase.Values
            .Where(v => v.ChartNoteId.HasValue)
            .Select(v => v.ChartNoteId!.Value)
            .Distinct()
            .ToList();
        var signedTimes = _detail.IsConfigured && latestChartNoteIds.Count > 0
            ? await _detail.GetSignedTimesAsync(latestChartNoteIds)
            : new Dictionary<int, DateTime>();

        // Dashboard rollup: verdict of the LATEST NOTE's most recent scrub
        // (not "latest scrub run across notes"). Matches the user rule
        // "workflow table always deals with the last note and scrub result."
        // Older notes' verdicts still surface in Doctor Queue / Human Review,
        // which filter independently.
        var scrubByCase = latestByNote
            .Where(s => latestNotePerCase.TryGetValue(s.WorkflowCaseId, out var latest)
                        && latest.NoteId == s.NoteId)
            .ToDictionary(
                s => s.WorkflowCaseId,
                s => new { Verdict = s.Verdict, RanAt = s.RanAt });

        // Two complementary signals:
        //  - outstandingByPatient: count of UNBILLED charges (no BilledCharges
        //    row). Drives state transitions — ReadyForBilling requires "new
        //    work to claim" (unbilled > 0), AwaitingCharges otherwise.
        //  - insuranceByPatient: full insurance balance (unbilled + AR).
        //    Drives the dashboard chip — what insurance still owes us total.
        //    "All billed" was misleading when AR was sitting unpaid; the chip
        //    now shows the real dollar amount we can still collect against.
        var outstandingByPatient = _detail.IsConfigured
            ? await _detail.GetOutstandingChargesAsync(patientIds)
            : new Dictionary<int, OutstandingChargesSummary>();
        var insuranceByPatient = _detail.IsConfigured
            ? await _detail.GetInsuranceBalancesAsync(patientIds)
            : new Dictionary<int, decimal>();
        // Team identifies patients by AccountNo, not the internal Patients.ID.
        // Pulled in batch and surfaced on the dashboard alongside the PK so
        // both stay clickable while staff learn the new portal.
        var accountByPatient = _detail.IsConfigured
            ? await _detail.GetAccountNumbersAsync(patientIds)
            : new Dictionary<int, int>();
        // Office per patient (primary provider's facility → canonical label).
        var officeByPatient = _detail.IsConfigured
            ? await _detail.GetOfficesAsync(patientIds)
            : new Dictionary<int, string>();

        var rows = cases.Select(c =>
        {
            var insurance = c.InsuranceAddedAt != null;
            var notes = c.DoctorNotesReceivedAt != null;
            var pip = c.PipVerifiedAt != null;
            var scrub = scrubByCase.GetValueOrDefault(c.Id);
            outstandingByPatient.TryGetValue(c.PatientId, out var outstanding);
            var insuranceBalance = insuranceByPatient.GetValueOrDefault(c.PatientId);

            // "Last note" timestamp: prefer the latest note's signed time (the
            // real clock), fall back to its clinical NoteDate (carries a time
            // only for portal-injected notes), then to the workflow stamp.
            var latestNote = latestNotePerCase.GetValueOrDefault(c.Id);
            DateTime? lastNoteStamp = null;
            if (latestNote?.ChartNoteId is int cnId && signedTimes.TryGetValue(cnId, out var sigAt))
                lastNoteStamp = sigAt;
            lastNoteStamp ??= latestNote?.NoteDate;
            lastNoteStamp ??= c.DoctorNotesReceivedAt;

            return new CaseDto(
                c.PatientId, c.PatientId, c.FirstName, c.LastName,
                DeriveDashboardState(insurance, pip, notes, scrub?.Verdict, insuranceBalance),
                insurance, notes, pip,
                c.InsuranceAddedAt, c.DoctorNotesReceivedAt, c.PipVerifiedAt,
                c.CreatedAt, c.UpdatedAt,
                LatestScrubVerdict: scrub?.Verdict,
                LatestScrubAt: scrub?.RanAt,
                OutstandingChargesCount: outstanding?.Count ?? 0,
                // Repurposed: this field now carries the full insurance balance
                // (unbilled + AR), not just unbilled charges. The frontend
                // chip reads it as "what insurance owes us total".
                OutstandingChargesTotal: insuranceBalance,
                OldestOutstandingChargeDate: outstanding?.OldestChargeDate,
                // Clinical date + time of the latest note, as stored (clinic-
                // local wall clock). Sortable "yyyy-MM-dd HH:mm:ss" so the string
                // sort in ApplySort stays chronological; the dashboard formatter
                // (formatNoteStamp) renders it from the parts so the wall clock
                // can't shift across timezones.
                LastNoteDate: lastNoteStamp?.ToString("yyyy-MM-dd HH:mm:ss"),
                // Latest note's DoctorNote.Id — exposed so the dashboard's
                // "Scrub now" button can fire POST /notes/{id}/scrub for the
                // most recent note when auto-scrub is paused for cost control.
                LatestDoctorNoteId: latestNotePerCase.GetValueOrDefault(c.Id)?.NoteId,
                AccountNo: accountByPatient.TryGetValue(c.PatientId, out var acct) ? acct : (int?)null,
                Office: officeByPatient.GetValueOrDefault(c.PatientId, OfficeResolver.Main));
        }).ToList();

        // Office filter (react-admin ?filter={"office":"Center City"}). Applied
        // before total/sort/paginate so the Content-Range total reflects the
        // filtered set and pagination stays correct.
        if (!string.IsNullOrWhiteSpace(officeFilter))
            rows = rows.Where(r => string.Equals(r.Office, officeFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var total = rows.Count;
        rows = ApplySort(rows, sortField, sortDesc);

        // Paginate after sort — react-admin's range is over the sorted result.
        var page = rows.Skip(skip).Take(take).ToList();
        var last = total == 0 ? 0 : Math.Min(skip + page.Count - 1, total - 1);
        Response.Headers["Content-Range"] = $"cases {skip}-{last}/{total}";
        return Ok(page);
    }

    // react-admin sends list filters as ?filter={"office":"...","q":"..."}.
    // We only honor the office filter today; unknown keys are ignored.
    private static string? ParseOfficeFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;
        try
        {
            using var doc = JsonDocument.Parse(filter);
            if (doc.RootElement.TryGetProperty("office", out var o) && o.ValueKind == JsonValueKind.String)
            {
                var v = o.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch { /* malformed filter — ignore */ }
        return null;
    }

    // react-admin ra-data-simple-rest sends sort as ?sort=["field","ORDER"].
    // Returns (field, desc). Defaults to LastUpdatedAt DESC when missing/bad.
    private static (string Field, bool Desc) ParseSort(string? sort)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(sort))
            {
                var a = JsonSerializer.Deserialize<string[]>(sort);
                if (a is { Length: 2 })
                    return (a[0] ?? string.Empty, string.Equals(a[1], "DESC", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { /* fall through to default */ }
        return ("lastUpdatedAt", true);
    }

    // Sort the dashboard rows by the requested field. Unknown fields fall back
    // to LastUpdatedAt — never throw, just keep the dashboard responsive.
    // Null values sort last regardless of direction (so "Scrub pending" /
    // "All paid" rows don't crowd the top when sorting on those columns).
    private static List<CaseDto> ApplySort(List<CaseDto> rows, string field, bool desc)
    {
        // Explicit nullable casts on non-nullable value-type fields so C#
        // overload resolution picks the struct-key Order helper.
        IOrderedEnumerable<CaseDto> sorted = field.ToLowerInvariant() switch
        {
            "patientid"               => Order(rows, r => (int?)r.PatientId, desc),
            "accountno"               => Order(rows, r => r.AccountNo, desc),
            "lastname"                => Order(rows, r => r.LastName, desc),
            "firstname"               => Order(rows, r => r.FirstName, desc),
            "insuranceprovided"       => Order(rows, r => (bool?)r.InsuranceProvided, desc),
            "lastnotedate"            => Order(rows, r => r.LastNoteDate, desc),
            "latestscrubat"           => Order(rows, r => r.LatestScrubAt, desc),
            "outstandingchargestotal" => Order(rows, r => (decimal?)r.OutstandingChargesTotal, desc),
            "office"                  => Order(rows, r => r.Office, desc),
            "currentstate"            => Order(rows, r => r.CurrentState, desc),
            "addedat"                 => Order(rows, r => (DateTime?)r.AddedAt, desc),
            "lastupdatedat"           => Order(rows, r => (DateTime?)r.LastUpdatedAt, desc),
            _                         => Order(rows, r => (DateTime?)r.LastUpdatedAt, true),
        };
        return sorted.ToList();
    }

    // Direction-aware ordering with nulls always at the bottom — for date /
    // string columns where "missing" should never be confused with "extreme".
    private static IOrderedEnumerable<CaseDto> Order<TKey>(
        List<CaseDto> rows, Func<CaseDto, TKey?> keySelector, bool desc) where TKey : struct
    {
        return desc
            ? rows.OrderBy(r => keySelector(r) == null).ThenByDescending(keySelector)
            : rows.OrderBy(r => keySelector(r) == null).ThenBy(keySelector);
    }
    private static IOrderedEnumerable<CaseDto> Order(
        List<CaseDto> rows, Func<CaseDto, string?> keySelector, bool desc)
    {
        return desc
            ? rows.OrderBy(r => string.IsNullOrEmpty(keySelector(r))).ThenByDescending(keySelector)
            : rows.OrderBy(r => string.IsNullOrEmpty(keySelector(r))).ThenBy(keySelector);
    }

    // GET /cases/{id} — patient detail (live ChiroTouch info + our per-flow stamps). id == PatientId.
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCase(int id)
    {
        if (!_detail.IsConfigured) return NotFound();

        var demo = await _detail.GetDemographicsAsync(id);
        if (demo is null) return NotFound();

        var policies = await _detail.GetPoliciesAsync(id);
        // Pull up to 50 notes — patient history is sometimes deep and the tab UI
        // scrolls horizontally so the cost is mostly in the SQL roundtrip.
        var noteRows = await _detail.GetRecentNotesAsync(id, 50);
        // Charges for the visits the recent notes matched into — mirrors how
        // ChiroTouch shows the bill alongside the notes the reviewer is reading.
        var matchedVisitIds = noteRows
            .Where(n => n.VisitId.HasValue)
            .Select(n => n.VisitId!.Value)
            .ToList();
        var chargeRows = await _detail.GetChargesForVisitsAsync(matchedVisitIds);

        // Diagnoses, scoped per visit. ChiroTouch's chart-note DX panel shows the
        // diagnosis set for that note's appointment (in Seq order), not the
        // patient's whole history — so group by appointment and attach each set to
        // its note rather than emitting a single patient-wide list.
        var diagnosesByVisit = (await _detail.GetDiagnosesForVisitsAsync(matchedVisitIds))
            .GroupBy(d => d.AppointmentId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d => (object)new { code = d.Code, description = d.Description }).ToList());

        // Pull cached PlainText from our DoctorNote table in a single query so
        // every note (not just the first 3) can display its body. Worker
        // reconstructs and stores PlainText on sync, so this is the fast path.
        // ChartNoteId is nullable on DoctorNote (portal-authored corrections
        // have null), so filter those out of this lookup — they're surfaced
        // separately below.
        var chartNoteIds = noteRows.Select(n => n.Id).ToArray();
        await using var noteDb = await _factory.CreateDbContextAsync();
        // Pull both PlainText AND our internal DoctorNote.Id keyed by ChartNoteId
        // so each note tab can carry its own latestScrub.
        var doctorNoteByChartId = await noteDb.DoctorNotes
            .AsNoTracking()
            .Where(n => n.ChartNoteId.HasValue && chartNoteIds.Contains(n.ChartNoteId.Value))
            .Select(n => new { ChartNoteId = n.ChartNoteId!.Value, n.Id, n.PlainText })
            .ToDictionaryAsync(n => n.ChartNoteId, n => new { n.Id, n.PlainText });

        // Build a doctorNoteId -> latest ScrubResult dictionary for the whole
        // patient so the per-tab ScrubPanel can render without a follow-up
        // fetch. Same dictionary feeds the case-level rollup below.
        var patientLatestPerNote = new Dictionary<int, ScrubResult>();
        var allPatientScrubs = await noteDb.ScrubResults.AsNoTracking()
            .Where(s => s.DoctorNoteId != null
                        && noteDb.DoctorNotes.Any(n => n.Id == s.DoctorNoteId && n.PatientId == id))
            .OrderByDescending(s => s.RanAt)
            .ToListAsync();
        foreach (var s in allPatientScrubs)
        {
            if (!patientLatestPerNote.ContainsKey(s.DoctorNoteId!.Value))
                patientLatestPerNote[s.DoctorNoteId.Value] = s;
        }

        object? ScrubProj(int? doctorNoteId)
        {
            if (doctorNoteId is null || !patientLatestPerNote.TryGetValue(doctorNoteId.Value, out var s)) return null;
            // Inline the full findings object so the ScrubPanel can render
            // section breakdowns + issues without a follow-up fetch. Without
            // this, the panel only had verdict + summary and "the detail"
            // for failures didn't appear.
            return new
            {
                id = s.Id,
                verdict = s.Verdict,
                overallConfidence = s.OverallConfidence,
                summary = s.Summary,
                ranAt = s.RanAt,
                findings = SafeParse(s.FindingsJson),
                modelUsed = s.ModelUsed,
                promptVersion = s.PromptVersion,
            };
        }

        static object? SafeParse(string? json) =>
            string.IsNullOrWhiteSpace(json)
                ? null
                : (object?)JsonSerializer.Deserialize<JsonElement>(json);

        // Build the combined notes list with a typed envelope so we can sort
        // chart + portal notes together without reflection.
        var noteCards = new List<NoteCard>();

        foreach (var n in noteRows)
        {
            var cached = doctorNoteByChartId.GetValueOrDefault(n.Id);
            string? text = cached?.PlainText;
            int? doctorNoteId = cached?.Id;
            // Live fallback for notes we haven't cached yet (existing patients
            // whose history pre-dates the sync). Slow but bounded per page;
            // task #43's historical backfill makes this path rare.
            if (text is null && n.SoapPtr is int ptr and not 0)
            {
                try { text = RtfConverter.ToPlainText(await _noteReads.GetNoteRtfAsync(ptr)); }
                catch { text = null; }
            }
            var (matchScore, matchReasons) = ScoreVisitMatch(n);

            noteCards.Add(new NoteCard(n.NoteDate, new
            {
                id = n.Id,
                source = "chart",
                doctorNoteId,
                // Clinical date as "yyyy-MM-dd" — keeps the client from
                // shifting midnight-UTC into the previous EDT evening.
                noteDate = n.NoteDate?.ToString("yyyy-MM-dd"),
                doctor = n.Doctor,
                status = n.Status,
                plainText = text,
                visitId = n.VisitId,
                visitTime = n.VisitTime,
                visitCheckIn = n.VisitCheckIn,
                visitCheckOut = n.VisitCheckOut,
                visitDoctor = n.VisitDoctor,
                visitsSameDay = n.VisitsSameDay,
                matchScore,
                matchReasons,
                diagnoses = n.VisitId is int vid && diagnosesByVisit.TryGetValue(vid, out var dx)
                    ? dx
                    : new List<object>(),
                // Pre-formatted to the stored (clinic-local) wall clock so it mirrors
                // ChiroTouch's "Signed: …" line exactly — no timezone re-shift client-side.
                signedAt = n.SignedAt?.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture),
                // Per-tab ScrubPanel reads this inline — no follow-up fetch.
                latestScrub = ScrubProj(doctorNoteId),
            }));
        }

        // Portal-authored corrections (no PSChiro ChartNote). Doctor authored
        // these inside our portal in response to a failed scrub; they belong
        // alongside the chart notes in the tabs so the reviewer sees the full
        // history. Each carries its latest scrub inline so the tab renders
        // without a follow-up fetch — there's no ChartNoteId for the existing
        // scrub endpoint to key off of.
        var portalNotes = await noteDb.DoctorNotes
            .AsNoTracking()
            .Where(n => n.PatientId == id && n.ChartNoteId == null)
            .OrderByDescending(n => n.NoteDate)
            .Select(n => new
            {
                n.Id,
                n.DoctorId,
                n.NoteDate,
                n.PlainText,
                n.CreatedAt,
            })
            .ToListAsync();

        if (portalNotes.Count > 0)
        {
            // DoctorId on DoctorNote is the ChiroTouch ID; map through Doctor.ChiroTouchDoctorId.
            var portalDoctorIds = portalNotes.Where(p => p.DoctorId.HasValue)
                .Select(p => p.DoctorId!.Value).Distinct().ToList();
            var doctorNamesByCtId = portalDoctorIds.Count == 0
                ? new Dictionary<int, string>()
                : await noteDb.Doctors.AsNoTracking()
                    .Where(d => portalDoctorIds.Contains(d.ChiroTouchDoctorId))
                    .ToDictionaryAsync(d => d.ChiroTouchDoctorId, d => d.FullName ?? string.Empty);

            foreach (var p in portalNotes)
            {
                noteCards.Add(new NoteCard(p.NoteDate, new
                {
                    // Negative id keeps tab keys unique against chart-note IDs
                    // (positive ints from PSChiro). The frontend doesn't read
                    // the value beyond uniqueness.
                    id = -p.Id,
                    source = "portal",
                    portalNoteId = p.Id,
                    noteDate = p.NoteDate?.ToString("yyyy-MM-dd"),
                    doctor = p.DoctorId.HasValue && doctorNamesByCtId.TryGetValue(p.DoctorId.Value, out var name)
                        ? name
                        : "Portal correction",
                    status = (int?)null,
                    plainText = p.PlainText,
                    visitId = (int?)null,
                    visitTime = (DateTime?)null,
                    visitCheckIn = (DateTime?)null,
                    visitCheckOut = (DateTime?)null,
                    visitDoctor = (string?)null,
                    visitsSameDay = 0,
                    matchScore = (int?)null,
                    matchReasons = new List<string>(),
                    diagnoses = new List<object>(),
                    createdAt = p.CreatedAt,
                    doctorNoteId = (int?)p.Id,
                    latestScrub = ScrubProj(p.Id),
                }));
            }
        }

        // Merge chart + portal notes into one tab strip, newest first.
        var notes = noteCards
            .OrderByDescending(c => c.NoteDate ?? DateTime.MinValue)
            .Select(c => c.Payload)
            .ToList();

        await using var db = await _factory.CreateDbContextAsync();
        var wc = await db.WorkflowCases.AsNoTracking()
            .Where(c => c.PatientId == id)
            .Select(c => new
            {
                c.Id,
                c.CurrentState, c.InsuranceAddedAt, c.DoctorNotesReceivedAt, c.PipVerifiedAt,
                c.CreatedAt, c.UpdatedAt,
            })
            .FirstOrDefaultAsync();

        // Latest note by clinical NoteDate (tiebreak on Id) — same rule as
        // the dashboard rollup. Drives both the case-level scrub headline
        // and the state derivation below.
        var latestDoctorNoteId = await noteDb.DoctorNotes.AsNoTracking()
            .Where(n => n.PatientId == id)
            .OrderByDescending(n => n.NoteDate).ThenByDescending(n => n.Id)
            .Select(n => (int?)n.Id)
            .FirstOrDefaultAsync();

        ScrubResult? latestNoteScrub = null;
        if (latestDoctorNoteId.HasValue)
            patientLatestPerNote.TryGetValue(latestDoctorNoteId.Value, out latestNoteScrub);

        // Case rollup: verdict of the LATEST NOTE's most recent scrub. The
        // counts (pass / needs_review / fail) stay alongside as context so
        // the reviewer sees "today's note passed, but 2 older ones need
        // review" at a glance — the headline still mirrors the dashboard.
        object? caseScrubSummary = null;
        if (patientLatestPerNote.Count > 0)
        {
            int passCount = patientLatestPerNote.Values.Count(s => s.Verdict == ScrubVerdicts.Pass);
            int needsReviewCount = patientLatestPerNote.Values.Count(s => s.Verdict == ScrubVerdicts.NeedsReview);
            int failCount = patientLatestPerNote.Values.Count(s => s.Verdict == ScrubVerdicts.Fail);
            caseScrubSummary = new
            {
                rolledVerdict = latestNoteScrub?.Verdict,
                passCount,
                needsReviewCount,
                failCount,
                scrubbedNoteCount = patientLatestPerNote.Count,
                latestRanAt = latestNoteScrub?.RanAt ?? patientLatestPerNote.Values.Max(s => s.RanAt),
            };
        }

        var insurance = policies.Count > 0;
        var hasNotes = noteRows.Count > 0;
        var pip = wc?.PipVerifiedAt != null;
        // Always derive from the live ChiroTouch truth so the detail can't drift
        // from a stale stored state. Same upgrade-to-ReadyForBilling rule as
        // the dashboard: latest-NOTE scrub passed + insurance still owes money.
        var insuranceBalanceDetail = _detail.IsConfigured
            ? (await _detail.GetInsuranceBalancesAsync(new[] { id }))
                .GetValueOrDefault(id)
            : 0m;
        var state = DeriveDashboardState(
            insurance, pip, hasNotes, latestNoteScrub?.Verdict, insuranceBalanceDetail);

        return Ok(new
        {
            id = demo.PatientId,
            patientId = demo.PatientId,
            firstName = demo.FirstName,
            middleName = demo.MiddleName,
            lastName = demo.LastName,
            sex = demo.Sex,
            address = demo.Address,
            city = demo.City,
            state = demo.State,
            zip = demo.Zip,
            primaryDoctor = demo.PrimaryDoctor,
            caseType = demo.CaseType,
            curInjuryDate = demo.CurInjuryDate,
            accountNo = demo.AccountNo,
            currentState = state,
            insuranceProvided = insurance,
            doctorNotesReceived = hasNotes,
            pipVerified = pip,
            // Clinical calendar dates (insurance effective / latest note) — emit as
            // date-only so the portal shows the real day without a timezone shift.
            insuranceAddedAt = wc?.InsuranceAddedAt?.ToString("yyyy-MM-dd"),
            doctorNotesReceivedAt = wc?.DoctorNotesReceivedAt?.ToString("yyyy-MM-dd"),
            pipVerifiedAt = wc?.PipVerifiedAt,
            addedAt = wc?.CreatedAt,
            lastUpdatedAt = wc?.UpdatedAt,
            caseScrubSummary,
            policies = policies.Select(p => new
            {
                id = p.Id,
                insurer = p.Insurer,
                coverageType = p.CoverageType,
                effectiveDate = p.EffectiveDate,
                terminationDate = p.TerminationDate,
            }),
            charges = chargeRows.Select(c => new
            {
                id = c.Id,
                appointmentId = c.AppointmentId,
                date = c.Date,
                code = c.Code,
                description = c.Description,
                amount = c.Amount,
                modifier1 = c.Modifier1,
                modifier2 = c.Modifier2,
                diagnoses = c.Diagnoses,
            }),
            notes,
        });
    }

    // POST /cases/{id}/doctor-notes — doctor authors a correction note in the
    // portal (no ChiroTouch ChartNote). id == PatientId. Persists to our DB,
    // auto-resolves open kickbacks for the patient, and re-scrubs immediately
    // so the doctor sees the new verdict before leaving the page.
    [HttpPost("{id:int}/doctor-notes")]
    public async Task<IActionResult> AuthorCorrectedNote(
        int id,
        [FromBody] AuthorCorrectedNoteRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Text))
            return BadRequest(new { error = "Note text is required." });

        await using var db = await _factory.CreateDbContextAsync(ct);
        var wc = await db.WorkflowCases.FirstOrDefaultAsync(c => c.PatientId == id, ct);
        if (wc is null) return NotFound(new { error = $"No workflow case for patient {id}." });

        // Look up the original failing note so the PSChiro writeback can
        // reuse its date + doctor. ChiroTouch's UI is appointment-anchored,
        // so a correction must land on the same DOS as the original note's
        // visit to appear in the chart.
        //
        // Two-step lookup:
        //   1. Use the explicit OriginalDoctorNoteId from the modal if present
        //   2. Fall back to "patient's most recent failing per-note scrub"
        //      so the flow still works even if the frontend didn't supply it
        DoctorNote? original = req.OriginalDoctorNoteId.HasValue
            ? await _cases.GetDoctorNoteByIdAsync(req.OriginalDoctorNoteId.Value)
            : null;

        if (original is null)
        {
            // Find the most recent failing per-note scrub for this patient
            // and use that note as the implicit "original."
            var recentFailNoteId = await db.ScrubResults.AsNoTracking()
                .Where(s => s.DoctorNoteId != null
                            && s.Verdict == ScrubVerdicts.Fail
                            && db.DoctorNotes.Any(n => n.Id == s.DoctorNoteId && n.PatientId == id))
                .OrderByDescending(s => s.RanAt)
                .Select(s => s.DoctorNoteId)
                .FirstOrDefaultAsync(ct);
            if (recentFailNoteId.HasValue)
                original = await _cases.GetDoctorNoteByIdAsync(recentFailNoteId.Value);
        }

        // Doctor attribution: the correction is recorded as the ORIGINAL
        // note's doctor (we're correcting their work in their name). Falls
        // back to req.DoctorId, then to "no doctor" for portal-only cases.
        var attributedDoctorId = original?.DoctorId ?? req.DoctorId;
        var attributedNoteDate = original?.NoteDate ?? req.NoteDate;

        var doctorNoteId = await _cases.InsertPortalAuthoredNoteAsync(
            workflowCaseId: wc.Id,
            patientId: id,
            doctorId: attributedDoctorId,
            text: req.Text.Trim(),
            noteDate: attributedNoteDate);

        // PSChiro writeback. Preferred path when we have the original
        // ChartNoteId: UPDATE the existing note's body in place — preserves
        // CT metadata (note id, signature, appointment linkage) and avoids
        // duplicate notes for the same DOS. Fallback to INSERT a new
        // ChartNote only when the original has no ChartNoteId (rare:
        // chain-of-corrections where the prior correction was itself
        // portal-authored).
        int? writebackChartNoteId = null;
        if (_pschiroWrite.IsConfigured)
        {
            try
            {
                if (original?.ChartNoteId is int existingChartNoteId)
                {
                    await _pschiroWrite.UpdateChartNoteBodyAsync(
                        chartNoteId: existingChartNoteId,
                        plainText: req.Text.Trim(),
                        ct: ct);
                    await _cases.LinkPortalNoteToChartNoteAsync(doctorNoteId, existingChartNoteId);
                    writebackChartNoteId = existingChartNoteId;
                }
                else if (attributedDoctorId.HasValue && attributedNoteDate.HasValue)
                {
                    var writeResult = await _pschiroWrite.WriteCorrectionChartNoteAsync(
                        patientId: id,
                        doctorId: attributedDoctorId.Value,
                        noteDate: attributedNoteDate.Value,
                        plainText: req.Text.Trim(),
                        ct: ct);
                    await _cases.LinkPortalNoteToChartNoteAsync(doctorNoteId, writeResult.ChartNoteId);
                    writebackChartNoteId = writeResult.ChartNoteId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PSChiro writeback failed for portal note {DoctorNoteId} (patient {PatientId}); correction is in our DB but not yet in ChiroTouch.",
                    doctorNoteId, id);
            }
        }

        // Open kickbacks resolved — the doctor just responded, regardless of
        // whether the next scrub passes or fails (it might still fail, in
        // which case the case stays in the review queue for human follow-up).
        await _corrections.ResolveOpenForPatientAsync(id, ct);

        // Re-scrub fires in the background so the doctor doesn't wait the
        // full Claude round-trip (5-15s). The scrub result lands in the queue
        // and refreshes the case's rolled-up verdict on its own — the doctor
        // doesn't need to see it in the modal. Captures the scrub orchestrator
        // by reference (singleton-scoped) and the doctor note id by value.
        var noteIdForScrub = doctorNoteId;
        var scrubs = _scrubs;
        var logger = _logger;
        _ = Task.Run(async () =>
        {
            try
            {
                // Respect the master kill-switch — when AutoScrub is disabled
                // (cost control), the corrected note stays as "Scrub pending"
                // until the operator manually fires it.
                if (!scrubs.AutoScrubEnabled)
                {
                    logger.LogInformation(
                        "Post-correction re-scrub skipped for note {NoteId}: Scrubbing:AutoScrub=false.",
                        noteIdForScrub);
                    return;
                }
                await scrubs.RunForNoteAsync(noteIdForScrub, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Background re-scrub failed for portal correction note {NoteId}.",
                    noteIdForScrub);
            }
        });

        return Ok(new
        {
            doctorNoteId,
            chartNoteId = writebackChartNoteId,
        });
    }

    // POST /cases/{id}/verify-pip?date=YYYY-MM-DD — portal-driven; id == PatientId.
    // Records/edits the PIP verified date (defaults to today). Not sourced from ChiroTouch.
    [HttpPost("{id:int}/verify-pip")]
    public async Task<IActionResult> VerifyPip(int id, [FromQuery] string? date)
    {
        var verified = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(date) &&
            DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            verified = parsed;
        }

        await _cases.SetPipVerifiedAsync(id, verified);
        return Ok(new { id, patientId = id, pipVerified = true, pipVerifiedAt = verified });
    }

    // Dashboard/detail state derivation. Cascade: insurance → notes →
    // ReadyForAiScrubbing → (pass + insurance owes us) ReadyForBilling, or
    // (pass + insurance owes nothing) AwaitingCharges. Per-appointment state
    // (one per visit, not one per patient) is the proper fix — tracked as
    // task #53; this read-time derivation is the bridge.
    //
    // Gates on insurance balance (unbilled + AR), not just unbilled count —
    // a case with all claims sent but $$ in AR is still "ReadyForBilling"
    // from a billing-team perspective (collectible work). AwaitingCharges
    // means "clean slate, waiting for next visit's charges to land".
    private static string DeriveDashboardState(
        bool insurance, bool pip, bool notes, string? latestScrubVerdict, decimal insuranceBalance)
    {
        var baseState = new BillingReadiness(Insurance: insurance, Pip: pip, Notes: notes).DerivedState;
        if (baseState == WorkflowStates.ReadyForAiScrubbing
            && latestScrubVerdict == ScrubVerdicts.Pass)
        {
            return insuranceBalance > 0
                ? WorkflowStates.ReadyForBilling
                : WorkflowStates.AwaitingCharges;
        }
        return baseState;
    }

    private static (int skip, int take) ParseRange(string? range)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(range))
            {
                var a = JsonSerializer.Deserialize<int[]>(range);
                if (a is { Length: 2 }) return (a[0], a[1] - a[0] + 1);
            }
        }
        catch { /* fall through */ }
        return (0, 25);
    }

    // Heuristic note->visit match score (0-100). Start at 100, deduct per weakness;
    // tune the weights here as Jacob calibrates against real reviews.
    private static (int Score, IReadOnlyList<string> Reasons) ScoreVisitMatch(ChartNoteRow n)
    {
        var reasons = new List<string>();
        if (n.VisitId is null)
        {
            reasons.Add("No matching appointment that day");
            return (0, reasons);
        }

        var score = 100;
        if (n.VisitsSameDay > 1)
        {
            score -= 30 * (n.VisitsSameDay - 1);
            reasons.Add($"{n.VisitsSameDay} appointments same day");
        }

        var noteDr = n.Doctor?.Trim();
        var visitDr = n.VisitDoctor?.Trim();
        var bothNamed = !string.IsNullOrEmpty(noteDr) && !string.IsNullOrEmpty(visitDr);
        if (bothNamed && !string.Equals(noteDr, visitDr, StringComparison.OrdinalIgnoreCase))
        {
            score -= 25;
            reasons.Add("Different doctor on appointment");
        }

        var visited = n.VisitCheckOut is not null || n.VisitCheckIn is not null;
        if (!visited)
        {
            score -= 20;
            reasons.Add("Visit not yet checked in");
        }

        score = Math.Max(0, score);

        return (score, reasons);
    }
}

// Doctor-authored correction note from the portal. OriginalDoctorNoteId, when
// provided, anchors the correction to a specific failing note — its date and
// doctor are reused for the PSChiro writeback so the correction lands on the
// same visit (ChiroTouch's UI is appointment-anchored).
public record AuthorCorrectedNoteRequest(
    int? DoctorId,
    string Text,
    DateTime? NoteDate,
    int? OriginalDoctorNoteId);

// Internal envelope used by GetCase to interleave chart notes and portal-authored
// corrections by date before serializing. Payload is the per-note anonymous object
// the frontend consumes.
internal sealed record NoteCard(DateTime? NoteDate, object Payload);

// Dashboard row (serialized to camelCase; id == PatientId).
public record CaseDto(
    int Id,
    int PatientId,
    string? FirstName,
    string? LastName,
    string CurrentState,
    bool InsuranceProvided,
    bool DoctorNotesReceived,
    bool PipVerified,
    DateTime? InsuranceAddedAt,
    DateTime? DoctorNotesReceivedAt,
    DateTime? PipVerifiedAt,
    DateTime AddedAt,
    DateTime LastUpdatedAt,
    // Case-level scrub verdict. Null when no case scrub has run yet so the
    // dashboard can render "Not scrubbed" distinctly from a verdict.
    string? LatestScrubVerdict,
    DateTime? LatestScrubAt,
    // Outstanding (unbilled) service charges for the patient — the third
    // billing-readiness gate. Count == 0 means "nothing to bill right now".
    int OutstandingChargesCount,
    decimal OutstandingChargesTotal,
    DateTime? OldestOutstandingChargeDate,
    // Most recent chart note's clinical date as "yyyy-MM-dd" — calendar date,
    // not a timestamp. Emitting as a plain date string keeps the client from
    // shifting it to the previous evening in EDT (midnight-UTC quirk).
    string? LastNoteDate,
    // Internal DoctorNote.Id of the latest note (drives the manual "Scrub now"
    // button on rows where verdict is pending). Null when the case has no
    // notes yet.
    int? LatestDoctorNoteId,
    // PSChiro AccountNo — the human-facing ID the team uses to identify
    // patients. Surfaced on the dashboard alongside the internal Patients.ID
    // so staff aren't forced to memorize new keys.
    int? AccountNo,
    // Canonical office label (patient's primary provider's facility). Drives the
    // dashboard Office column + the office filter. See OfficeResolver.
    string? Office);
