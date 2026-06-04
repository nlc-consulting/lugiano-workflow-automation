using System.Text.Json;
using Lugiano.Workflow.SyncService.Services.Scrubbing;
using Microsoft.AspNetCore.Mvc;

namespace Lugiano.Workflow.SyncService.Controllers;

[ApiController]
public sealed class ScrubController : ControllerBase
{
    private readonly ScrubOrchestrator _orchestrator;

    public ScrubController(ScrubOrchestrator orchestrator) => _orchestrator = orchestrator;

    // POST /cases/{patientId}/notes/{chartNoteId}/scrub
    // Manually runs the scrubber against a single note. Synchronous — the
    // request blocks for ~5-15s while Claude responds.
    [HttpPost("cases/{patientId:int}/notes/{chartNoteId:int}/scrub")]
    public async Task<IActionResult> Scrub(int patientId, int chartNoteId, CancellationToken ct)
    {
        try
        {
            var result = await _orchestrator.RunByChartNoteIdAsync(patientId, chartNoteId, ct);
            return Ok(Project(result));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // GET /cases/{patientId}/notes/{chartNoteId}/scrub — latest result if any.
    [HttpGet("cases/{patientId:int}/notes/{chartNoteId:int}/scrub")]
    public async Task<IActionResult> GetLatest(int patientId, int chartNoteId, CancellationToken ct)
    {
        var result = await _orchestrator.GetLatestForChartNoteAsync(patientId, chartNoteId, ct);
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
