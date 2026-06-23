using Lugiano.Workflow.SyncService.Services;
using Microsoft.AspNetCore.Mvc;

namespace Lugiano.Workflow.SyncService.Controllers;

// Demo-only helpers. These exist to fire repeatable test flows against the
// Fakee Test patient (2765) without manual SQL. Remove or gate behind an
// auth / env flag before shipping to a real environment.
[ApiController]
[Route("test")]
public sealed class TestController : ControllerBase
{
    private readonly IPSChiroWriteService _pschiroWrite;
    private readonly ILogger<TestController> _logger;

    public TestController(
        IPSChiroWriteService pschiroWrite,
        ILogger<TestController> logger)
    {
        _pschiroWrite = pschiroWrite;
        _logger = logger;
    }

    // POST /test/inject-note — inserts a thin chart note into PSChiro for the
    // Fakee Test patient (or whatever override). Uses the same validated 3-table
    // recipe (ChartText + ChartNotes + Signatures) as the portal-correction
    // writeback. All fields default to the validated demo values:
    //   patientId = 2765 (Fakee Test)
    //   doctorId  = 142  (Joel Kerak, DC, NB — has stored signature)
    //   noteDate  = today (must match an existing Appointment to render in CT)
    //   text      = "Patient seen today." (intentionally thin → fails scrub)
    //
    // The next sync tick picks the note up, auto-scrubs it (per-note gate),
    // and a failing verdict surfaces it in Doctor View for the kickback demo.
    [HttpPost("inject-note")]
    public async Task<IActionResult> InjectNote([FromBody] InjectNoteRequest? req, CancellationToken ct)
    {
        if (!_pschiroWrite.IsConfigured)
            return BadRequest(new { error = "PSChiro write account is not configured (set ChiroTouchWrite connection string)." });

        var patientId = req?.PatientId ?? 2765;
        var doctorId = req?.DoctorId ?? 142;
        var noteDate = (req?.NoteDate ?? DateTime.Today).Date;
        // Default text mirrors the spec Nick provided: identifiable as a test
        // note, intentionally thin so it fails scrub. Safe to delete from CT
        // chart history if the demo is aborted mid-flow.
        var text = string.IsNullOrWhiteSpace(req?.Text)
            ? $"Test note ({noteDate:M/d/yyyy}) inserted by Lugiano portal automation on {DateTime.Now:yyyy-MM-dd HH:mm}. No clinical content; safe to delete."
            : req!.Text!.Trim();

        try
        {
            var result = await _pschiroWrite.WriteCorrectionChartNoteAsync(
                patientId: patientId,
                doctorId: doctorId,
                noteDate: noteDate,
                plainText: text,
                ct: ct);

            _logger.LogInformation(
                "Test inject-note: patient {PatientId} doctor {DoctorId} date {NoteDate:yyyy-MM-dd} -> ChartNotes.ID {ChartNoteId}.",
                patientId, doctorId, noteDate, result.ChartNoteId);

            return Ok(new
            {
                patientId,
                doctorId,
                noteDate = noteDate.ToString("yyyy-MM-dd"),
                chartNoteId = result.ChartNoteId,
                chartTextPtr = result.ChartTextPtr,
                hint = "Wait for next sync tick (~30s) — note will appear in Doctor View if scrub fails.",
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public sealed record InjectNoteRequest(int? PatientId, int? DoctorId, DateTime? NoteDate, string? Text);
}
