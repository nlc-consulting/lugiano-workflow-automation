using System.Text.Json;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Services.Email;
using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services;

// Orchestrates a single kickback: validates inputs, resolves recipients +
// per-send email overrides, composes + sends the email, persists a
// CorrectionRequest, and transitions the WorkflowCase to AwaitingDoctorCorrection.
// Loop-cap escalates instead of re-sending past round 3.
public sealed class CorrectionRequestService
{
    private const int LoopCap = 3;

    private readonly IDbContextFactory<WorkflowDbContext> _dbFactory;
    private readonly IEmailSender _email;
    private readonly IPatientDetailQueries _detail;
    private readonly IConfiguration _config;
    private readonly ILogger<CorrectionRequestService> _logger;

    public CorrectionRequestService(
        IDbContextFactory<WorkflowDbContext> dbFactory,
        IEmailSender email,
        IPatientDetailQueries detail,
        IConfiguration config,
        ILogger<CorrectionRequestService> logger)
    {
        _dbFactory = dbFactory;
        _email = email;
        _detail = detail;
        _config = config;
        _logger = logger;
    }

    public sealed record KickbackInput(
        int DoctorNoteId,
        IReadOnlyList<int> RecipientDoctorIds,
        string? OverrideEmail,        // per-send override, doesn't update profile
        bool SaveOverrideAsDefault,   // if true, also write OverrideEmail to Doctor.Email
        IReadOnlyList<string> MissingItems,
        string? ReviewerComments,
        string? ReviewerEmail);

    public sealed record KickbackResult(
        int CorrectionRequestId,
        string State,
        int RoundNumber,
        string? RecipientEmailUsed,
        string CaseState);

    public async Task<KickbackResult> KickbackAsync(KickbackInput input, CancellationToken ct = default)
    {
        if (input.RecipientDoctorIds.Count == 0)
            throw new ArgumentException("At least one recipient doctor must be selected.", nameof(input));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var note = await db.DoctorNotes.FirstOrDefaultAsync(n => n.Id == input.DoctorNoteId, ct)
            ?? throw new InvalidOperationException($"DoctorNote {input.DoctorNoteId} not found.");
        var wc = await db.WorkflowCases.FirstOrDefaultAsync(c => c.Id == note.WorkflowCaseId, ct)
            ?? throw new InvalidOperationException($"WorkflowCase {note.WorkflowCaseId} not found.");

        // Determine the round number from prior requests for this note.
        var priorRoundMax = await db.CorrectionRequests
            .Where(r => r.DoctorNoteId == note.Id)
            .Select(r => (int?)r.RoundNumber)
            .MaxAsync(ct);
        var round = (priorRoundMax ?? 0) + 1;

        // Resolve the primary recipient doctor + email. V1 supports a single
        // recipient (per task #41 we punted multi-doctor); we still accept a
        // list in the input so the API shape is stable when secondary lands.
        var primaryDoctorId = input.RecipientDoctorIds[0];
        var doctor = await db.Doctors.FirstOrDefaultAsync(d => d.Id == primaryDoctorId, ct)
            ?? throw new InvalidOperationException($"Doctor {primaryDoctorId} not found.");

        var recipientEmail = !string.IsNullOrWhiteSpace(input.OverrideEmail)
            ? input.OverrideEmail!.Trim()
            : doctor.Email;
        // Email is off for now — kickbacks surface in the doctor's in-portal
        // Doctor Review instead (Kickback:SendEmail flips it back on). When off
        // we no longer require an email on file, so a doctor with a blank email
        // (e.g. the per-office records) can still be sent a correction.
        var sendEmail = _config.GetValue("Kickback:SendEmail", false)
            && !string.IsNullOrWhiteSpace(recipientEmail);

        // Loop-cap: don't keep emailing the doctor forever.
        if (round > LoopCap)
        {
            var escalated = new CorrectionRequest
            {
                DoctorNoteId = note.Id,
                WorkflowCaseId = wc.Id,
                State = CorrectionStates.Escalated,
                ReviewerEmail = input.ReviewerEmail,
                ReviewerComments = input.ReviewerComments,
                MissingItemsJson = JsonSerializer.Serialize(input.MissingItems),
                RecipientDoctorIdsJson = JsonSerializer.Serialize(input.RecipientDoctorIds),
                RecipientOverrideEmail = input.OverrideEmail,
                RoundNumber = round,
                CreatedAt = DateTime.UtcNow,
            };
            db.CorrectionRequests.Add(escalated);
            await db.SaveChangesAsync(ct);
            _logger.LogWarning(
                "Kickback escalated for DoctorNote {NoteId} at round {Round} (cap {Cap}).",
                note.Id, round, LoopCap);
            return new KickbackResult(escalated.Id, escalated.State, round, recipientEmail, wc.CurrentState);
        }

        // Optionally persist the override as the doctor's new default email.
        if (input.SaveOverrideAsDefault && !string.IsNullOrWhiteSpace(input.OverrideEmail))
        {
            doctor.Email = input.OverrideEmail!.Trim();
            doctor.UpdatedAt = DateTime.UtcNow;
        }

        var now = DateTime.UtcNow;
        var request = new CorrectionRequest
        {
            DoctorNoteId = note.Id,
            WorkflowCaseId = wc.Id,
            State = CorrectionStates.Pending,
            ReviewerEmail = input.ReviewerEmail,
            ReviewerComments = input.ReviewerComments,
            MissingItemsJson = JsonSerializer.Serialize(input.MissingItems),
            RecipientDoctorIdsJson = JsonSerializer.Serialize(input.RecipientDoctorIds),
            RecipientOverrideEmail = input.OverrideEmail,
            RoundNumber = round,
            CreatedAt = now,
        };
        db.CorrectionRequests.Add(request);
        await db.SaveChangesAsync(ct);

        if (sendEmail)
        {
            // Demographics live in ChiroTouch — only needed for the email body.
            var demo = await _detail.GetDemographicsAsync(wc.PatientId);
            var patientCtx = new CorrectionEmailComposer.PatientContext(
                FirstName: demo?.FirstName ?? wc.FirstName,
                LastName: demo?.LastName ?? wc.LastName,
                DateOfBirth: null,                  // not in our current demographics read
                DateOfService: note.NoteDate);
            var message = CorrectionEmailComposer.Compose(
                recipientEmail!, doctor.FullName, patientCtx,
                input.MissingItems, input.ReviewerComments, round);

            var sent = await _email.SendAsync(message, ct);
            if (sent)
            {
                request.State = CorrectionStates.Sent;
                request.SentAt = DateTime.UtcNow;
            }
        }

        // Always transition the case so the doctor sees the awaiting-correction
        // status. With email off the request stays Pending until the doctor
        // corrects it in-portal (the note is already in their Doctor Review).
        wc.CurrentState = WorkflowStates.AwaitingDoctorCorrection;
        wc.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Kickback recorded for DoctorNote {NoteId}, round {Round}, recipient {Recipient} (email {EmailState}).",
            note.Id, round, doctor.FullName, sendEmail ? "sent" : "skipped");

        return new KickbackResult(request.Id, request.State, round, sendEmail ? recipientEmail : null, wc.CurrentState);
    }

    // Auto-resolve: marks any open CorrectionRequests for the patient as Resolved.
    // Called from ChartNoteSyncService when a new note arrives for the patient.
    // Returns the number of requests resolved (0 means there were no open ones).
    public async Task<int> ResolveOpenForPatientAsync(int patientId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var caseId = await db.WorkflowCases
            .Where(c => c.PatientId == patientId)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync(ct);
        if (caseId is null) return 0;

        var open = await db.CorrectionRequests
            .Where(r => r.WorkflowCaseId == caseId
                && (r.State == CorrectionStates.Pending || r.State == CorrectionStates.Sent))
            .ToListAsync(ct);
        if (open.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var r in open)
        {
            r.State = CorrectionStates.Resolved;
            r.ResolvedAt = now;
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Auto-resolved {Count} CorrectionRequest(s) for patient {PatientId} after new note arrival.",
            open.Count, patientId);
        return open.Count;
    }
}
