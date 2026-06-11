using System.Text.Json;
using Lugiano.Workflow.SyncService.Services.Scrubbing;
using Microsoft.AspNetCore.Mvc;

namespace Lugiano.Workflow.SyncService.Controllers;

[ApiController]
public sealed class ScrubController : ControllerBase
{
    private readonly ScrubOrchestrator _orchestrator;

    public ScrubController(ScrubOrchestrator orchestrator) => _orchestrator = orchestrator;

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
