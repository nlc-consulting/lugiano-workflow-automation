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

        // Load ALL cases — visible columns (LastNoteDate, insurance balance,
        // latest scrub) are computed from joins, so we can't sort/paginate in
        // SQL. ~700 rows, in-memory sort is sub-millisecond.
        var cases = await db.WorkflowCases.AsNoTracking().ToListAsync();

        var caseIds = cases.Select(c => c.Id).ToList();
        var patientIds = cases.Select(c => c.PatientId).ToList();

        // Per-note scrubs aggregated to a case-level chip: take each note's
        // latest scrub, then roll up (any fail -> fail; else needs_review ->
        // needs_review; else all pass -> pass; else partial -> null). Full
        // per-note breakdown lives on the case detail.
        var perNoteScrubs = await db.ScrubResults.AsNoTracking()
            .Where(s => s.DoctorNoteId != null && caseIds.Contains(s.WorkflowCaseId))
            .Select(s => new { s.WorkflowCaseId, NoteId = s.DoctorNoteId!.Value, s.Verdict, s.RanAt })
            .ToListAsync();

        var latestByNote = perNoteScrubs
            .GroupBy(s => new { s.WorkflowCaseId, s.NoteId })
            .Select(g => g.OrderByDescending(s => s.RanAt).First())
            .ToList();

        // Latest note per case by NoteDate (tiebreak on Id). Drives LastNoteDate
        // + the scrub rollup. EF Core 8 can't translate g.OrderBy().First()
        // inside GroupBy (EmptyProjectionMember), so we group in memory — cheap.
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

        // ChiroTouch stores NoteDate as date-only (midnight), so the real "when
        // it came in" clock is the signature timestamp. Pull the signed time for
        // each case's latest note so LastNoteDate can show it.
        var latestChartNoteIds = latestNotePerCase.Values
            .Where(v => v.ChartNoteId.HasValue)
            .Select(v => v.ChartNoteId!.Value)
            .Distinct()
            .ToList();
        var signedTimes = _detail.IsConfigured && latestChartNoteIds.Count > 0
            ? await _detail.GetSignedTimesAsync(latestChartNoteIds)
            : new Dictionary<int, DateTime>();

        // Dashboard rollup: verdict of the LATEST NOTE's most recent scrub (not
        // "latest scrub across notes") — per the rule "workflow table always
        // deals with the last note and scrub result." Older notes still surface
        // in Doctor Queue / Human Review, which filter independently.
        var scrubByCase = latestByNote
            .Where(s => latestNotePerCase.TryGetValue(s.WorkflowCaseId, out var latest)
                        && latest.NoteId == s.NoteId)
            .ToDictionary(
                s => s.WorkflowCaseId,
                s => new { Verdict = s.Verdict, RanAt = s.RanAt });

        // Two complementary signals:
        //  - outstandingByPatient: count of UNBILLED charges (no BilledCharges
        //    row). Drives state transitions — ReadyForBilling requires unbilled
        //    > 0, AwaitingCharges otherwise.
        //  - insuranceByPatient: full insurance balance (unbilled + AR). Drives
        //    the dashboard chip. "All billed" was misleading when AR sat unpaid;
        //    the chip now shows the real collectible dollar amount.
        var outstandingByPatient = _detail.IsConfigured
            ? await _detail.GetOutstandingChargesAsync(patientIds)
            : new Dictionary<int, OutstandingChargesSummary>();
        var insuranceByPatient = _detail.IsConfigured
            ? await _detail.GetInsuranceBalancesAsync(patientIds)
            : new Dictionary<int, decimal>();
        // Team identifies patients by AccountNo, not the internal Patients.ID —
        // surfaced alongside the PK while staff learn the new portal.
        var accountByPatient = _detail.IsConfigured
            ? await _detail.GetAccountNumbersAsync(patientIds)
            : new Dictionary<int, int>();
        // Office per patient (primary provider's facility → canonical label).
        var officeByPatient = _detail.IsConfigured
            ? await _detail.GetOfficesAsync(patientIds)
            : new Dictionary<int, string>();
        // Missing-note visits per patient — Appointments with billed service
        // charges but no matching ChartNote by that doctor on that date.
        // Surfaces the gap Jacob flagged (Wilson 6/23 Saias: 4 CPTs billed, no
        // note written).
        var missingNotesByPatient = _detail.IsConfigured
            ? await _detail.GetMissingNoteVisitCountsAsync(patientIds)
            : new Dictionary<int, int>();

        var rows = cases.Select(c =>
        {
            var insurance = c.InsuranceAddedAt != null;
            var notes = c.DoctorNotesReceivedAt != null;
            var pip = c.PipVerifiedAt != null;
            var scrub = scrubByCase.GetValueOrDefault(c.Id);
            outstandingByPatient.TryGetValue(c.PatientId, out var outstanding);
            var insuranceBalance = insuranceByPatient.GetValueOrDefault(c.PatientId);

            // "Last note" timestamp: prefer the signed time (real clock), fall
            // back to NoteDate (has a time only for portal-injected notes), then
            // to the workflow stamp.
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
                // (unbilled + AR), not just unbilled charges.
                OutstandingChargesTotal: insuranceBalance,
                OldestOutstandingChargeDate: outstanding?.OldestChargeDate,
                // Latest note's clinical date+time, clinic-local wall clock.
                // Sortable "yyyy-MM-dd HH:mm:ss" so the string sort stays
                // chronological; formatNoteStamp renders from the parts so the
                // wall clock can't shift across timezones.
                LastNoteDate: lastNoteStamp?.ToString("yyyy-MM-dd HH:mm:ss"),
                // Latest note's DoctorNote.Id — drives the "Scrub now" button
                // (POST /notes/{id}/scrub) when auto-scrub is paused for cost.
                LatestDoctorNoteId: latestNotePerCase.GetValueOrDefault(c.Id)?.NoteId,
                AccountNo: accountByPatient.TryGetValue(c.PatientId, out var acct) ? acct : (int?)null,
                Office: officeByPatient.GetValueOrDefault(c.PatientId, OfficeResolver.Main),
                MissingNoteVisitCount: missingNotesByPatient.GetValueOrDefault(c.PatientId, 0));
        }).ToList();

        // Office filter (react-admin ?filter={"office":"..."}). Applied before
        // total/sort/paginate so Content-Range reflects the filtered set.
        if (!string.IsNullOrWhiteSpace(officeFilter))
            rows = rows.Where(r => string.Equals(r.Office, officeFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var total = rows.Count;
        rows = ApplySort(rows, sortField, sortDesc);

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

    // Sort dashboard rows. Unknown fields fall back to LastUpdatedAt (never
    // throw). Nulls sort last regardless of direction so "Scrub pending" /
    // "All paid" rows don't crowd the top.
    private static List<CaseDto> ApplySort(List<CaseDto> rows, string field, bool desc)
    {
        // Explicit nullable casts on non-nullable value-type fields so overload
        // resolution picks the struct-key Order helper.
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
            "missingnotevisitcount"   => Order(rows, r => (int?)r.MissingNoteVisitCount, desc),
            "office"                  => Order(rows, r => r.Office, desc),
            "currentstate"            => Order(rows, r => r.CurrentState, desc),
            "addedat"                 => Order(rows, r => (DateTime?)r.AddedAt, desc),
            "lastupdatedat"           => Order(rows, r => (DateTime?)r.LastUpdatedAt, desc),
            _                         => Order(rows, r => (DateTime?)r.LastUpdatedAt, true),
        };
        return sorted.ToList();
    }

    // Direction-aware ordering with nulls always at the bottom — so "missing"
    // is never confused with "extreme".
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
        // Cap at 50 notes — history can be deep; cost is mostly the SQL roundtrip.
        var noteRows = await _detail.GetRecentNotesAsync(id, 50);
        // Charges for the visits the recent notes matched into — mirrors how
        // ChiroTouch shows the bill alongside the notes.
        var matchedVisitIds = noteRows
            .Where(n => n.VisitId.HasValue)
            .Select(n => n.VisitId!.Value)
            .ToList();
        var chargeRows = await _detail.GetChargesForVisitsAsync(matchedVisitIds);

        // Diagnoses scoped per visit: ChiroTouch's DX panel shows the set for
        // that note's appointment (Seq order), not the patient's whole history —
        // so group by appointment and attach each set to its note.
        var diagnosesByVisit = (await _detail.GetDiagnosesForVisitsAsync(matchedVisitIds))
            .GroupBy(d => d.AppointmentId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d => (object)new { code = d.Code, description = d.Description }).ToList());

        // Cached PlainText from DoctorNote in one query so every note can show
        // its body (worker stores PlainText on sync — the fast path). Filter out
        // null ChartNoteId (portal-authored corrections) — surfaced separately
        // below.
        var chartNoteIds = noteRows.Select(n => n.Id).ToArray();
        await using var noteDb = await _factory.CreateDbContextAsync();
        // Key PlainText + internal DoctorNote.Id by ChartNoteId so each tab
        // carries its own latestScrub.
        var doctorNoteByChartId = await noteDb.DoctorNotes
            .AsNoTracking()
            .Where(n => n.ChartNoteId.HasValue && chartNoteIds.Contains(n.ChartNoteId.Value))
            .Select(n => new { ChartNoteId = n.ChartNoteId!.Value, n.Id, n.PlainText, n.RawRtf })
            .ToDictionaryAsync(n => n.ChartNoteId, n => new { n.Id, n.PlainText, n.RawRtf });

        // doctorNoteId -> latest ScrubResult for the whole patient so the per-tab
        // ScrubPanel renders without a follow-up fetch. Also feeds the case-level
        // rollup below.
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
            // Inline the full findings so the ScrubPanel can render section
            // breakdowns + issues without a follow-up fetch (was verdict +
            // summary only, so failure detail didn't appear).
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

        // Typed envelope so we can sort chart + portal notes together without
        // reflection.
        var noteCards = new List<NoteCard>();

        foreach (var n in noteRows)
        {
            var cached = doctorNoteByChartId.GetValueOrDefault(n.Id);
            string? text = cached?.PlainText;
            string? rawRtf = cached?.RawRtf;
            int? doctorNoteId = cached?.Id;
            // Live fallback for uncached notes (patients whose history pre-dates
            // the sync). Slow but bounded per page; task #43's historical
            // backfill makes this path rare.
            if (text is null && n.SoapPtr is int ptr and not 0)
            {
                try
                {
                    rawRtf = await _noteReads.GetNoteRtfAsync(ptr);
                    text = RtfConverter.ToPlainText(rawRtf);
                }
                catch { text = null; }
            }
            // Colored/bold runs so the portal note matches ChiroTouch's
            // formatting (blue/red). Null when not RTF — client falls back to
            // plainText.
            var richBody = RtfRichConverter.ToRuns(rawRtf);
            var (matchScore, matchReasons) = ScoreVisitMatch(n);

            noteCards.Add(new NoteCard(n.NoteDate, new
            {
                id = n.Id,
                source = "chart",
                doctorNoteId,
                // "yyyy-MM-dd" keeps the client from shifting midnight-UTC into
                // the previous EDT evening.
                noteDate = n.NoteDate?.ToString("yyyy-MM-dd"),
                doctor = n.Doctor,
                status = n.Status,
                plainText = text,
                richBody,
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

        // Portal-authored corrections (no PSChiro ChartNote), authored in
        // response to a failed scrub; shown alongside chart notes for full
        // history. Latest scrub inlined (no ChartNoteId for the scrub endpoint
        // to key off of).
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
                    // (positive PSChiro ints); frontend only uses it for uniqueness.
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

        // Latest note by NoteDate (tiebreak on Id) — same rule as the dashboard
        // rollup. Drives the case-level scrub headline and state derivation.
        var latestDoctorNoteId = await noteDb.DoctorNotes.AsNoTracking()
            .Where(n => n.PatientId == id)
            .OrderByDescending(n => n.NoteDate).ThenByDescending(n => n.Id)
            .Select(n => (int?)n.Id)
            .FirstOrDefaultAsync();

        ScrubResult? latestNoteScrub = null;
        if (latestDoctorNoteId.HasValue)
            patientLatestPerNote.TryGetValue(latestDoctorNoteId.Value, out latestNoteScrub);

        // Case rollup: verdict of the LATEST NOTE's most recent scrub. Counts
        // (pass / needs_review / fail) ride alongside as context ("today passed,
        // but 2 older ones need review") while the headline mirrors the dashboard.
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
        // Always derive from live ChiroTouch truth so the detail can't drift
        // from stale stored state. Same upgrade-to-ReadyForBilling rule as the
        // dashboard: latest-NOTE scrub passed + insurance still owes money.
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
            // Clinical calendar dates — emit as date-only so the portal shows
            // the real day without a timezone shift.
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

        // Look up the original failing note so the writeback reuses its date +
        // doctor. ChiroTouch is appointment-anchored — a correction must land on
        // the same DOS as the original visit to appear in the chart. Prefer the
        // explicit OriginalDoctorNoteId; else fall back to the patient's most
        // recent failing per-note scrub.
        DoctorNote? original = req.OriginalDoctorNoteId.HasValue
            ? await _cases.GetDoctorNoteByIdAsync(req.OriginalDoctorNoteId.Value)
            : null;

        if (original is null)
        {
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

        // Attribution: correction is recorded under the ORIGINAL note's doctor
        // (their work, their name). Falls back to req.DoctorId, then no doctor.
        var attributedDoctorId = original?.DoctorId ?? req.DoctorId;
        var attributedNoteDate = original?.NoteDate ?? req.NoteDate;

        var doctorNoteId = await _cases.InsertPortalAuthoredNoteAsync(
            workflowCaseId: wc.Id,
            patientId: id,
            doctorId: attributedDoctorId,
            text: req.Text.Trim(),
            noteDate: attributedNoteDate);

        // PSChiro writeback. With the original ChartNoteId: UPDATE the body in
        // place — preserves CT metadata (note id, signature, appointment link)
        // and avoids duplicate notes for the same DOS. INSERT a new ChartNote
        // only when there's no ChartNoteId (rare: chained portal-authored
        // corrections).
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

        // Resolve open kickbacks — the doctor responded, regardless of whether
        // the next scrub passes (if it fails, the case stays in review for
        // human follow-up).
        await _corrections.ResolveOpenForPatientAsync(id, ct);

        // Re-scrub in the background so the doctor doesn't wait the Claude
        // round-trip (5-15s); the result refreshes the rolled-up verdict on its
        // own. Captures the orchestrator by reference and the note id by value.
        var noteIdForScrub = doctorNoteId;
        var scrubs = _scrubs;
        var logger = _logger;
        _ = Task.Run(async () =>
        {
            try
            {
                // Master kill-switch — when AutoScrub is disabled (cost control),
                // the note stays "Scrub pending" until fired manually.
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

    // State derivation cascade: insurance → notes → ReadyForAiScrubbing →
    // (pass + insurance owes us) ReadyForBilling, else (pass + owes nothing)
    // AwaitingCharges. Per-appointment state is the proper fix (task #53); this
    // read-time derivation is the bridge.
    //
    // Gates on insurance balance (unbilled + AR), not just unbilled count — a
    // case with all claims sent but $$ in AR is still ReadyForBilling
    // (collectible). AwaitingCharges = clean slate, waiting for next charges.
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

    // Heuristic note->visit match score (0-100). Start at 100, deduct per
    // weakness; tune the weights as Jacob calibrates against real reviews.
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

// Doctor-authored correction note from the portal. OriginalDoctorNoteId anchors
// the correction to a specific failing note — its date + doctor are reused for
// the writeback so it lands on the same visit (ChiroTouch is appointment-anchored).
public record AuthorCorrectedNoteRequest(
    int? DoctorId,
    string Text,
    DateTime? NoteDate,
    int? OriginalDoctorNoteId);

// Envelope used by GetCase to interleave chart + portal notes by date before
// serializing. Payload is the per-note object the frontend consumes.
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
    // Case-level scrub verdict. Null when no scrub has run so the dashboard can
    // render "Not scrubbed" distinctly from a verdict.
    string? LatestScrubVerdict,
    DateTime? LatestScrubAt,
    // Outstanding (unbilled) charges — the third billing-readiness gate.
    // Count == 0 means nothing to bill right now.
    int OutstandingChargesCount,
    decimal OutstandingChargesTotal,
    DateTime? OldestOutstandingChargeDate,
    // Latest note's clinical date as "yyyy-MM-dd" (calendar date, not a
    // timestamp) — plain date string keeps the client from shifting it to the
    // previous EDT evening (midnight-UTC quirk).
    string? LastNoteDate,
    // Latest note's DoctorNote.Id — drives the manual "Scrub now" button on
    // pending rows. Null when the case has no notes yet.
    int? LatestDoctorNoteId,
    // PSChiro AccountNo — the human-facing ID the team uses. Surfaced alongside
    // Patients.ID so staff needn't memorize new keys.
    int? AccountNo,
    // Canonical office label (primary provider's facility). Drives the Office
    // column + filter. See OfficeResolver.
    string? Office,
    // Count of (date, doctor) billable visits with service charges but no
    // matching ChartNote. Non-zero = biller must chase missing documentation.
    // See GetMissingNoteVisitCountsAsync for the detection rule.
    int MissingNoteVisitCount);
