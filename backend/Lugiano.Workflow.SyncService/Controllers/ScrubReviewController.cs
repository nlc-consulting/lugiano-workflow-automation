using System.Text.Json;
using Lugiano.Workflow.SyncService;
using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Controllers;

[ApiController]
[Route("scrub-review")]
public sealed class ScrubReviewController : ControllerBase
{
    private readonly IDbContextFactory<WorkflowDbContext> _factory;

    public ScrubReviewController(IDbContextFactory<WorkflowDbContext> factory) => _factory = factory;

    // GET /scrub-review — patients whose latest scrub verdict is 'fail'. Triage
    // queue: staff drills into /cases/{id}/show and decides to send back to the
    // doctor or fix on the billing side. ra-data-simple-rest: ?range=[start,end].
    [HttpGet]
    public async Task<IActionResult> GetReviewQueue([FromQuery] string? range)
    {
        var (skip, take) = ParseRange(range);
        if (take <= 0 || take > 200) take = 25;

        await using var db = await _factory.CreateDbContextAsync();

        // Per-note scrubs: take each note's latest; if ANY note's latest is Fail,
        // the case is in the queue. ScrubResult is append-only and small — thin
        // projection, reduce in memory. Scrub Review = failed CHART notes (doctor
        // not yet given a chance to correct); portal-authored failures route to
        // Human Review instead — see HumanReviewController.
        var allScrubs = await db.ScrubResults.AsNoTracking()
            .Where(s => s.DoctorNoteId != null
                        && db.DoctorNotes.Any(n => n.Id == s.DoctorNoteId && !n.IsPortalAuthored))
            .Select(s => new
            {
                s.WorkflowCaseId,
                NoteId = s.DoctorNoteId!.Value,
                s.Verdict,
                s.Summary,
                s.RanAt,
            })
            .ToListAsync();

        // Latest scrub per note.
        var latestPerNote = allScrubs
            .GroupBy(s => new { s.WorkflowCaseId, s.NoteId })
            .Select(g => g.OrderByDescending(s => s.RanAt).First())
            .ToList();

        // Case rollup — one row per case, most-recent failing note as its rep.
        var failedLatest = latestPerNote
            .Where(s => s.Verdict == ScrubVerdicts.Fail)
            .GroupBy(s => s.WorkflowCaseId)
            .Select(g => g.OrderByDescending(s => s.RanAt).First())
            .OrderByDescending(s => s.RanAt)
            .ToList();

        var total = failedLatest.Count;
        var page = failedLatest.Skip(skip).Take(take).ToList();

        var caseIds = page.Select(s => s.WorkflowCaseId).Distinct().ToList();
        var casesById = caseIds.Count == 0
            ? new Dictionary<int, WorkflowCase>()
            : await db.WorkflowCases.AsNoTracking()
                .Where(c => caseIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id);

        // Pre-fetch failing notes' plain text + authoring doctor so the Doctor
        // View modal can edit in-place and the list can show "would be sent to
        // Dr. X" (admins see every row; real doctor scoping is task #40).
        var noteIds = page.Select(s => s.NoteId).Distinct().ToList();
        var noteInfoById = noteIds.Count == 0
            ? new Dictionary<int, (string? PlainText, int? DoctorId)>()
            : (await db.DoctorNotes.AsNoTracking()
                .Where(n => noteIds.Contains(n.Id))
                .Select(n => new { n.Id, n.PlainText, n.DoctorId })
                .ToListAsync())
                .ToDictionary(n => n.Id, n => (n.PlainText, n.DoctorId));

        // Map ChiroTouch doctor ids -> names. Doctor.ChiroTouchDoctorId is the
        // bridge column; FullName is "First Last, Credential".
        var ctDoctorIds = noteInfoById.Values
            .Select(v => v.DoctorId)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .Distinct()
            .ToList();
        var doctorNameByCtId = ctDoctorIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Doctors.AsNoTracking()
                .Where(d => ctDoctorIds.Contains(d.ChiroTouchDoctorId))
                .ToDictionaryAsync(d => d.ChiroTouchDoctorId, d => d.FullName ?? string.Empty);

        var rows = page.Select(s =>
        {
            casesById.TryGetValue(s.WorkflowCaseId, out var c);
            noteInfoById.TryGetValue(s.NoteId, out var info);
            string? doctor = null;
            if (info.DoctorId.HasValue && doctorNameByCtId.TryGetValue(info.DoctorId.Value, out var name))
                doctor = name;
            return new
            {
                id = c?.PatientId ?? 0,
                patientId = c?.PatientId ?? 0,
                firstName = c?.FirstName,
                lastName = c?.LastName,
                latestScrubAt = s.RanAt,
                latestScrubVerdict = s.Verdict,
                summary = s.Summary,
                // The failing note. Modal passes this as originalDoctorNoteId so
                // the writeback anchors to the correct visit's date + doctor.
                doctorNoteId = s.NoteId,
                // Pre-fills the modal textarea for in-place editing. Plain text —
                // RTF was stripped on sync; the writeback re-wraps in minimal RTF
                // for TX_RTF32.
                originalText = info.PlainText,
                // Authoring doctor — lets admins see whose queue this would be in
                // once portal logins are scoped (task #40).
                doctor,
            };
        }).ToList();

        var last = total == 0 ? 0 : Math.Min(skip + rows.Count - 1, total - 1);
        Response.Headers["Content-Range"] = $"scrub-review {skip}-{last}/{total}";
        return Ok(rows);
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
}
