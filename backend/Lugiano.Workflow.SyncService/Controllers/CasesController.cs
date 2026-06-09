using System.Globalization;
using System.Text.Json;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Services;
using Lugiano.Workflow.SyncService.Util;
using Lugiano.Workflow.SyncService.Workflow;
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

    public CasesController(
        IDbContextFactory<WorkflowDbContext> factory,
        IPatientDetailQueries detail,
        IChartNoteReadQueries noteReads,
        WorkflowCaseService cases)
    {
        _factory = factory;
        _detail = detail;
        _noteReads = noteReads;
        _cases = cases;
    }

    // GET /cases — dashboard: the portal's workflow record, newest-first, with per-flow stamps.
    // ra-data-simple-rest: ?range=[start,end] + Content-Range header.
    [HttpGet]
    public async Task<IActionResult> GetCases([FromQuery] string? range)
    {
        var (skip, take) = ParseRange(range);
        if (take <= 0 || take > 200) take = 25;

        await using var db = await _factory.CreateDbContextAsync();
        var total = await db.WorkflowCases.CountAsync();

        var cases = await db.WorkflowCases.AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .Skip(skip).Take(take)
            .ToListAsync();

        var rows = cases.Select(c =>
        {
            var insurance = c.InsuranceAddedAt != null;
            var notes = c.DoctorNotesReceivedAt != null;
            var pip = c.PipVerifiedAt != null;
            return new CaseDto(
                c.PatientId, c.PatientId, c.FirstName, c.LastName,
                new BillingReadiness(Insurance: insurance, Pip: pip, Notes: notes).DerivedState,
                insurance, notes, pip,
                c.InsuranceAddedAt, c.DoctorNotesReceivedAt, c.PipVerifiedAt,
                c.CreatedAt, c.UpdatedAt);
        }).ToList();

        var last = total == 0 ? 0 : Math.Min(skip + rows.Count - 1, total - 1);
        Response.Headers["Content-Range"] = $"cases {skip}-{last}/{total}";
        return Ok(rows);
    }

    // GET /cases/{id} — patient detail (live ChiroTouch info + our per-flow stamps). id == PatientId.
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCase(int id)
    {
        if (!_detail.IsConfigured) return NotFound();

        var demo = await _detail.GetDemographicsAsync(id);
        if (demo is null) return NotFound();

        var policies = await _detail.GetPoliciesAsync(id);
        var noteRows = await _detail.GetRecentNotesAsync(id, 5);
        // Charges for the visits the recent notes matched into — mirrors how
        // ChiroTouch shows the bill alongside the notes the reviewer is reading.
        var matchedVisitIds = noteRows
            .Where(n => n.VisitId.HasValue)
            .Select(n => n.VisitId!.Value);
        var chargeRows = await _detail.GetChargesForVisitsAsync(matchedVisitIds);

        var notes = new List<object>();
        var textBudget = 3;
        foreach (var n in noteRows)
        {
            string? text = null;
            if (textBudget-- > 0 && n.SoapPtr is int ptr and not 0)
            {
                // One bad note's RTF must not 500 the whole detail view.
                try { text = RtfConverter.ToPlainText(await _noteReads.GetNoteRtfAsync(ptr)); }
                catch { text = null; }
            }
            var (matchScore, matchReasons) = ScoreVisitMatch(n);

            notes.Add(new
            {
                id = n.Id,
                noteDate = n.NoteDate,
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
            });
        }

        // Diagnoses live in dbo.Diagnoses keyed by AppointmentID (PatientID
        // column on those rows is typically NULL). Join through Appointments
        // gets the patient's full active diagnosis set with descriptions already
        // populated — no catalog lookup needed.
        var diagnoses = (await _detail.GetPatientDiagnosesAsync(id))
            .Select(d => new { code = d.Code, description = d.Description })
            .ToList();

        await using var db = await _factory.CreateDbContextAsync();
        var wc = await db.WorkflowCases.AsNoTracking()
            .Where(c => c.PatientId == id)
            .Select(c => new
            {
                c.CurrentState, c.InsuranceAddedAt, c.DoctorNotesReceivedAt, c.PipVerifiedAt,
                c.CreatedAt, c.UpdatedAt,
            })
            .FirstOrDefaultAsync();

        var insurance = policies.Count > 0;
        var hasNotes = noteRows.Count > 0;
        var pip = wc?.PipVerifiedAt != null;
        // Always derive from the live ChiroTouch truth so the detail can't drift
        // from a stale stored state.
        var state = new BillingReadiness(
            Insurance: insurance, Pip: pip, Notes: hasNotes).DerivedState;

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
            diagnoses,
            notes,
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
    DateTime LastUpdatedAt);
