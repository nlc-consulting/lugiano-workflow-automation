using System.Text.Json;
using Lugiano.Workflow.SyncService.Services.Scrubbing;
using Lugiano.Workflow.SyncService.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Controllers;

[ApiController]
public sealed class ScrubController : ControllerBase
{
    private readonly ScrubOrchestrator _orchestrator;
    private readonly IDbContextFactory<WorkflowDbContext> _dbFactory;

    public ScrubController(
        ScrubOrchestrator orchestrator,
        IDbContextFactory<WorkflowDbContext> dbFactory)
    {
        _orchestrator = orchestrator;
        _dbFactory = dbFactory;
    }

    // POST /notes/{doctorNoteId}/scrub — run a per-note scrub. The note's
    // visit defines DX + charges scope; brief chart context rides along.
    // Synchronous; blocks ~5-15s while Claude responds.
    [HttpPost("notes/{doctorNoteId:int}/scrub")]
    public async Task<IActionResult> Scrub(int doctorNoteId, CancellationToken ct)
    {
        try
        {
            var result = await _orchestrator.RunForNoteAsync(doctorNoteId, ct);
            return Ok(Project(result));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // GET /notes/{doctorNoteId}/scrub — latest scrub result for this note.
    [HttpGet("notes/{doctorNoteId:int}/scrub")]
    public async Task<IActionResult> GetLatest(int doctorNoteId, CancellationToken ct)
    {
        var result = await _orchestrator.GetLatestForNoteAsync(doctorNoteId, ct);
        if (result is null) return NoContent();
        return Ok(Project(result));
    }

    // POST /notes/{doctorNoteId}/override — manually flip this note's verdict.
    // Used by reviewers / admins to mark a note 'good' or send it back to 'bad'
    // without re-running Claude. Writes a fresh ScrubResult so the latest-wins
    // rollup picks it up immediately.
    [HttpPost("notes/{doctorNoteId:int}/override")]
    public async Task<IActionResult> Override(
        int doctorNoteId,
        [FromBody] OverrideRequest req,
        [FromServices] Services.WorkflowCaseService cases,
        CancellationToken ct)
    {
        if (req?.Verdict is not ("pass" or "fail" or "needs_review"))
            return BadRequest(new { error = "Verdict must be 'pass', 'fail', or 'needs_review'." });

        try
        {
            var result = await cases.OverrideScrubVerdictAsync(
                doctorNoteId, req.Verdict, req.OverriddenBy, req.Reason);
            return Ok(Project(result));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public sealed record OverrideRequest(string Verdict, string? OverriddenBy, string? Reason);

    // POST /scrub/backfill-latest?dryRun=true&topN=50 — fills in scrub results
    // for the latest DoctorNote per case where that note has no scrub yet.
    // Bounded by topN (oldest-updated cases first) so cost is predictable.
    // dryRun=true returns identified counts without firing Claude.
    // Sequential execution — predictable load on the Anthropic API and easy
    // to interrupt. Re-run to pick up cases that errored.
    [HttpPost("scrub/backfill-latest")]
    public async Task<IActionResult> BackfillLatest(
        [FromQuery] bool dryRun = true,
        [FromQuery] int topN = 50,
        CancellationToken ct = default)
    {
        topN = Math.Clamp(topN, 1, 200);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Pull all notes for cases-of-interest, group in memory (EF Core 8
        // can't translate g.OrderBy().First() projections). Cheap — note
        // count is bounded by total cases × ~50 history per case.
        var allNotes = await db.DoctorNotes.AsNoTracking()
            .Select(n => new { n.Id, n.WorkflowCaseId, n.NoteDate })
            .ToListAsync(ct);

        var latestPerCase = allNotes
            .GroupBy(n => n.WorkflowCaseId)
            .Select(g => g.OrderByDescending(n => n.NoteDate).ThenByDescending(n => n.Id).First())
            .Select(n => n.Id)
            .ToHashSet();

        // Note ids with at least one ScrubResult — skip those.
        var scrubbedNoteIds = await db.ScrubResults.AsNoTracking()
            .Where(s => s.DoctorNoteId != null)
            .Select(s => s.DoctorNoteId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var scrubbedSet = scrubbedNoteIds.ToHashSet();

        var candidates = latestPerCase.Where(id => !scrubbedSet.Contains(id)).Take(topN).ToList();

        if (dryRun)
        {
            return Ok(new
            {
                dryRun = true,
                identified = candidates.Count,
                topN,
                exampleNoteIds = candidates.Take(10).ToList(),
            });
        }

        var scrubbed = 0;
        var errors = new List<object>();
        foreach (var noteId in candidates)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await _orchestrator.RunForNoteAsync(noteId, ct);
                scrubbed++;
            }
            catch (Exception ex)
            {
                errors.Add(new { noteId, error = ex.Message });
            }
        }

        return Ok(new
        {
            dryRun = false,
            identified = candidates.Count,
            scrubbed,
            errorCount = errors.Count,
            errors,
        });
    }

    private static object Project(Lugiano.Workflow.SyncService.Workflow.Models.ScrubResult r) => new
    {
        id = r.Id,
        doctorNoteId = r.DoctorNoteId,
        verdict = r.Verdict,
        overallConfidence = r.OverallConfidence,
        summary = r.Summary,
        findings = JsonSerializer.Deserialize<JsonElement>(r.FindingsJson),
        modelUsed = r.ModelUsed,
        promptVersion = r.PromptVersion,
        ranAt = r.RanAt,
    };
}
