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

    // GET /scrub-review — patients whose latest scrub verdict is 'fail'.
    // The triage queue: staff opens each, drills into /cases/{id}/show, and
    // decides whether to send back to the doctor or fix on the billing side.
    // ra-data-simple-rest: ?range=[start,end] + Content-Range.
    [HttpGet]
    public async Task<IActionResult> GetReviewQueue([FromQuery] string? range)
    {
        var (skip, take) = ParseRange(range);
        if (take <= 0 || take > 200) take = 25;

        await using var db = await _factory.CreateDbContextAsync();

        // Per-note scrubs (DoctorNoteId IS NOT NULL). For each note, take its
        // latest scrub. Roll up to the case: if ANY note's latest is Fail,
        // the case is in the review queue. ScrubResult is append-only and
        // small in practice — pull a thin projection, reduce in memory.
        var allScrubs = await db.ScrubResults.AsNoTracking()
            .Where(s => s.DoctorNoteId != null)
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

        // Case rollup — keep one row per case with the most-recent failing
        // note as its representative for the queue.
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

        var rows = page.Select(s =>
        {
            casesById.TryGetValue(s.WorkflowCaseId, out var c);
            return new
            {
                id = c?.PatientId ?? 0,
                patientId = c?.PatientId ?? 0,
                firstName = c?.FirstName,
                lastName = c?.LastName,
                latestScrubAt = s.RanAt,
                latestScrubVerdict = s.Verdict,
                summary = s.Summary,
                // The specific failing note. The Doctor View modal passes this
                // as originalDoctorNoteId on submit so the PSChiro writeback
                // anchors to the correct visit's date + doctor.
                doctorNoteId = s.NoteId,
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
