using System.Text.Json;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services.Scrubbing;

// Builds the scrubbing context from our DB + ChiroTouch reads, runs the
// scrubber, persists ScrubResult. The orchestrator is the single entry point
// for both manual scrubs (controller) and auto-scrubs (future ChartNoteSyncService).
public sealed class ScrubOrchestrator
{
    private readonly IDbContextFactory<WorkflowDbContext> _dbFactory;
    private readonly IPatientDetailQueries _detail;
    private readonly IScrubber _scrubber;
    private readonly ILogger<ScrubOrchestrator> _logger;

    public ScrubOrchestrator(
        IDbContextFactory<WorkflowDbContext> dbFactory,
        IPatientDetailQueries detail,
        IScrubber scrubber,
        ILogger<ScrubOrchestrator> logger)
    {
        _dbFactory = dbFactory;
        _detail = detail;
        _scrubber = scrubber;
        _logger = logger;
    }

    public async Task<ScrubResult> RunByChartNoteIdAsync(int patientId, int chartNoteId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var note = await db.DoctorNotes
            .FirstOrDefaultAsync(n => n.ChartNoteId == chartNoteId && n.PatientId == patientId, ct)
            ?? throw new InvalidOperationException($"DoctorNote not found for chartNoteId {chartNoteId} / patient {patientId}.");

        var ctx = await BuildContextAsync(db, note, ct);
        var run = await _scrubber.ScrubAsync(ctx, ct);

        var result = new ScrubResult
        {
            DoctorNoteId = note.Id,
            WorkflowCaseId = note.WorkflowCaseId,
            Verdict = run.Findings.Verdict,
            OverallConfidence = run.Findings.OverallConfidence,
            Summary = run.Findings.Summary,
            FindingsJson = JsonSerializer.Serialize(run.Findings),
            RawResponseJson = run.RawResponseJson,
            ModelUsed = run.ModelUsed,
            PromptVersion = run.PromptVersion,
            RanAt = DateTime.UtcNow,
        };
        db.ScrubResults.Add(result);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Scrub completed: note {NoteId} -> {Verdict} (confidence {Confidence}) via {Model}.",
            note.Id, result.Verdict, result.OverallConfidence, result.ModelUsed);

        return result;
    }

    public async Task<ScrubResult?> GetLatestForChartNoteAsync(int patientId, int chartNoteId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ScrubResults
            .AsNoTracking()
            .Where(r => r.DoctorNoteId == db.DoctorNotes
                .Where(n => n.ChartNoteId == chartNoteId && n.PatientId == patientId)
                .Select(n => n.Id)
                .FirstOrDefault())
            .OrderByDescending(r => r.RanAt)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<ScrubContext> BuildContextAsync(WorkflowDbContext db, DoctorNote note, CancellationToken ct)
    {
        // Charges for the note's matched visit. ChartNote→Appointment is matched
        // by (PatientID, same date) in our existing detail query — we keep the
        // same heuristic here by matching the visit-date via Appointments
        // through GetRecentNotesAsync's OUTER APPLY.
        var allRecent = await _detail.GetRecentNotesAsync(note.PatientId, 5);
        var match = allRecent.FirstOrDefault(n => n.Id == note.ChartNoteId);
        var visitId = match?.VisitId;

        IReadOnlyList<ScrubCharge> charges = visitId.HasValue
            ? (await _detail.GetChargesForVisitsAsync(new[] { visitId.Value }))
                .Select(c => new ScrubCharge(
                    Code: c.Code ?? string.Empty,
                    Description: c.Description,
                    Amount: c.Amount,
                    Diagnoses: c.Diagnoses))
                .ToList()
            : Array.Empty<ScrubCharge>();

        // Patient's documented diagnoses from dbo.Diagnoses (joined through
        // Appointments). This is the canonical source — catches codes the
        // doctor documented but hasn't billed yet (visible to scrubber as
        // missing alignment between docs and bill).
        var diagnoses = (await _detail.GetPatientDiagnosesAsync(note.PatientId))
            .Select(d => string.IsNullOrEmpty(d.Description)
                ? d.Code
                : $"{d.Code} {d.Description}")
            .ToList();

        // Prior notes (most recent excluding this one) for consistency context.
        // Cap text length per note to keep the prompt manageable.
        var priorNotes = (await db.DoctorNotes
            .AsNoTracking()
            .Where(n => n.PatientId == note.PatientId && n.Id != note.Id && n.PlainText != null)
            .OrderByDescending(n => n.NoteDate)
            .Take(2)
            .Select(n => new { n.NoteDate, n.PlainText })
            .ToListAsync(ct))
            .Select(n => new ScrubPriorNote(n.NoteDate, Truncate(n.PlainText, 2000)))
            .ToList();

        return new ScrubContext(
            DoctorNoteId: note.Id,
            ChartNoteId: note.ChartNoteId,
            NoteDate: note.NoteDate,
            NoteText: note.PlainText ?? string.Empty,
            VisitCharges: charges,
            PatientDiagnoses: diagnoses,
            PriorNotes: priorNotes);
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max] + "…";
}
