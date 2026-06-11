using System.Text.Json;
using Lugiano.Workflow.SyncService;
using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Controllers;

[ApiController]
[Route("human-review")]
public sealed class HumanReviewController : ControllerBase
{
    private readonly IDbContextFactory<WorkflowDbContext> _factory;

    public HumanReviewController(IDbContextFactory<WorkflowDbContext> factory) => _factory = factory;

    // GET /human-review — cases where a doctor-corrected (portal-authored)
    // note STILL failed. Doctor's already had a chance; this is the escalation
    // bucket for staff to review and either override the verdict or work the
    // note manually.
    [HttpGet]
    public async Task<IActionResult> GetReviewQueue([FromQuery] string? range)
    {
        var (skip, take) = ParseRange(range);
        if (take <= 0 || take > 200) take = 25;

        await using var db = await _factory.CreateDbContextAsync();

        // Pull all per-note scrubs whose underlying DoctorNote is portal-
        // authored. Reduce to latest-per-note in memory, keep failures.
        var portalScrubs = await db.ScrubResults.AsNoTracking()
            .Where(s => s.DoctorNoteId != null
                        && db.DoctorNotes.Any(n => n.Id == s.DoctorNoteId && n.IsPortalAuthored))
            .Select(s => new
            {
                s.WorkflowCaseId,
                NoteId = s.DoctorNoteId!.Value,
                s.Verdict,
                s.Summary,
                s.RanAt,
            })
            .ToListAsync();

        var latestPerNote = portalScrubs
            .GroupBy(s => new { s.WorkflowCaseId, s.NoteId })
            .Select(g => g.OrderByDescending(s => s.RanAt).First())
            .ToList();

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
                doctorNoteId = s.NoteId,
            };
        }).ToList();

        var last = total == 0 ? 0 : Math.Min(skip + rows.Count - 1, total - 1);
        Response.Headers["Content-Range"] = $"human-review {skip}-{last}/{total}";
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
