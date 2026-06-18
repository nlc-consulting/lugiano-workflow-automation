using System.Globalization;
using Lugiano.Workflow.SyncService.Services;
using Microsoft.AspNetCore.Mvc;

namespace Lugiano.Workflow.SyncService.Controllers;

// Multi-page chart-notes PDF for sharing with attorneys, IME reviewers, or
// carriers. Cover sheet with patient demographics + case + insurance, then
// one page per chart note ordered newest-first.
[ApiController]
[Route("notes")]
public sealed class NotesController : ControllerBase
{
    private readonly NotesPreviewService _notes;

    public NotesController(NotesPreviewService notes) => _notes = notes;

    // GET /notes/preview?patientId=X[&from=YYYY-MM-DD][&to=YYYY-MM-DD]
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        [FromQuery] int patientId,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        CancellationToken ct = default)
    {
        if (patientId <= 0)
            return BadRequest(new { error = "patientId is required." });

        DateTime? fromDate = TryParseDate(from);
        DateTime? toDate = TryParseDate(to);
        // Inclusive end-of-day so &to=2026-06-12 actually includes 6/12 notes.
        if (toDate.HasValue) toDate = toDate.Value.Date.AddDays(1).AddTicks(-1);

        var data = await _notes.GetDataAsync(patientId, fromDate, toDate, ct);
        if (data is null)
            return NotFound(new { error = $"No patient or notes available for {patientId}." });

        var pdf = _notes.RenderPdf(data);
        var name = $"notes-{patientId}-{DateTime.Today:yyyyMMdd}.pdf";
        return File(pdf, "application/pdf", name);
    }

    private static DateTime? TryParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
}
