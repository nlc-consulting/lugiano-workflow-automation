using Lugiano.Workflow.SyncService.Services;
using Lugiano.Workflow.SyncService.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Controllers;

[ApiController]
public sealed class CorrectionsController : ControllerBase
{
    private readonly IDbContextFactory<WorkflowDbContext> _factory;
    private readonly CorrectionRequestService _corrections;

    public CorrectionsController(
        IDbContextFactory<WorkflowDbContext> factory,
        CorrectionRequestService corrections)
    {
        _factory = factory;
        _corrections = corrections;
    }

    // GET /cases/{patientId}/notes/{chartNoteId}/doctors
    // {chartNoteId} is the ChiroTouch ChartNote ID (what the portal already
    // knows from the patient detail). We resolve it to our local DoctorNote
    // internally — keeps the URL stable across our import/sync internals.
    // V1 returns primary doctor only (SecondaryDoctorID is task #41); the
    // array shape is forward-compatible for when secondary lands.
    [HttpGet("cases/{patientId:int}/notes/{chartNoteId:int}/doctors")]
    public async Task<IActionResult> GetNoteDoctors(int patientId, int chartNoteId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var note = await db.DoctorNotes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.ChartNoteId == chartNoteId && n.PatientId == patientId);
        if (note is null) return NotFound();

        var doctors = new List<object>();
        if (note.DoctorId is int chiroDocId)
        {
            var d = await db.Doctors
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ChiroTouchDoctorId == chiroDocId);
            if (d is not null)
            {
                doctors.Add(new
                {
                    id = d.Id,
                    chiroTouchDoctorId = d.ChiroTouchDoctorId,
                    fullName = d.FullName,
                    credentials = d.Credentials,
                    email = d.Email,
                    isActive = d.IsActive,
                    isPrimary = true,
                });
            }
        }

        return Ok(new { chartNoteId, patientId, doctors });
    }

    // POST /cases/{patientId}/notes/{chartNoteId}/kickback
    // Body shape: { recipientDoctorIds, overrideEmail?, saveOverrideAsDefault?,
    //               missingItems, reviewerComments?, reviewerEmail? }
    [HttpPost("cases/{patientId:int}/notes/{chartNoteId:int}/kickback")]
    public async Task<IActionResult> Kickback(int patientId, int chartNoteId, [FromBody] KickbackBody body)
    {
        if (body.RecipientDoctorIds is null || body.RecipientDoctorIds.Length == 0)
            return BadRequest(new { error = "recipientDoctorIds must contain at least one doctor." });

        await using var db = await _factory.CreateDbContextAsync();
        var doctorNoteId = await db.DoctorNotes
            .Where(n => n.ChartNoteId == chartNoteId && n.PatientId == patientId)
            .Select(n => (int?)n.Id)
            .FirstOrDefaultAsync();
        if (doctorNoteId is null)
            return NotFound(new { error = $"DoctorNote not found for chartNoteId {chartNoteId} / patient {patientId}." });

        try
        {
            var result = await _corrections.KickbackAsync(new CorrectionRequestService.KickbackInput(
                DoctorNoteId: doctorNoteId.Value,
                RecipientDoctorIds: body.RecipientDoctorIds,
                OverrideEmail: body.OverrideEmail,
                SaveOverrideAsDefault: body.SaveOverrideAsDefault ?? false,
                MissingItems: body.MissingItems ?? Array.Empty<string>(),
                ReviewerComments: body.ReviewerComments,
                ReviewerEmail: body.ReviewerEmail));

            return Ok(new
            {
                correctionRequestId = result.CorrectionRequestId,
                state = result.State,
                roundNumber = result.RoundNumber,
                recipientEmail = result.RecipientEmailUsed,
                caseState = result.CaseState,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

// Request DTO. Matches the modal's payload shape.
public sealed class KickbackBody
{
    public int[]? RecipientDoctorIds { get; set; }
    public string? OverrideEmail { get; set; }
    public bool? SaveOverrideAsDefault { get; set; }
    public string[]? MissingItems { get; set; }
    public string? ReviewerComments { get; set; }
    public string? ReviewerEmail { get; set; }
}
