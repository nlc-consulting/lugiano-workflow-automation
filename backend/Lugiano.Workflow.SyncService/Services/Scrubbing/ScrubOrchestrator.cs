using System.Text.Json;
using Lugiano.Workflow.SyncService;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services.Scrubbing;

// Per-note scrub orchestration. Each provider's chart note for a visit is
// the billable unit — one note becomes one HCFA claim line. The scrubber
// evaluates THAT note against THAT visit's DX and charges, with brief
// chart context from prior notes. One ScrubResult per scrub run, keyed to
// the DoctorNoteId being evaluated.
public sealed class ScrubOrchestrator
{
    private const int ContextNoteCount = 3;       // last N prior notes for context
    private const int ContextNoteCharCap = 1500;  // chars per context note (truncated)
    private const int FocalNoteCharCap = 8000;    // chars for the focal note (full SOAP fits)
    private const double CloneSimilarityThreshold = 0.95;

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

    // Run a per-note scrub. The note's visit defines the DX + charges scope;
    // a few prior notes ride along as brief chart context. Result is keyed to
    // the DoctorNoteId — re-scrubs append new ScrubResult rows.
    public async Task<ScrubResult> RunForNoteAsync(int doctorNoteId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var focal = await db.DoctorNotes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == doctorNoteId, ct)
            ?? throw new InvalidOperationException($"DoctorNote {doctorNoteId} not found.");

        // Resolve focal doctor name (DoctorId is the ChiroTouch ID).
        string? focalDoctorName = null;
        if (focal.DoctorId.HasValue)
        {
            focalDoctorName = await db.Doctors.AsNoTracking()
                .Where(d => d.ChiroTouchDoctorId == focal.DoctorId.Value)
                .Select(d => d.FullName)
                .FirstOrDefaultAsync(ct);
        }

        // Find the matched appointment for this note's date. Used to pull
        // per-Appointment Diagnoses + Transactions (charges).
        int? matchedApptId = null;
        IReadOnlyList<string> visitDiagnoses = Array.Empty<string>();
        IReadOnlyList<ScrubChargeLine> visitCharges = Array.Empty<ScrubChargeLine>();
        if (focal.NoteDate.HasValue && _detail.IsConfigured)
        {
            // GetRecentNotesAsync returns the matched VisitId for each note via
            // the OUTER APPLY (doctor-priority match heuristic). Pull a small
            // window covering the focal note's date and find the focal entry.
            var recent = await _detail.GetRecentNotesAsync(focal.PatientId, 50);
            var match = recent.FirstOrDefault(n =>
                focal.ChartNoteId.HasValue
                    ? n.Id == focal.ChartNoteId.Value
                    : false); // portal-authored notes don't match a PSChiro visit
            matchedApptId = match?.VisitId;

            if (matchedApptId.HasValue)
            {
                visitDiagnoses = (await _detail.GetDiagnosesForVisitsAsync(new[] { matchedApptId.Value }))
                    .Select(d => string.IsNullOrEmpty(d.Description) ? d.Code : $"{d.Code} {d.Description}")
                    .ToList();
                visitCharges = (await _detail.GetChargesForVisitsAsync(new[] { matchedApptId.Value }))
                    .Select(c => new ScrubChargeLine(c.Code ?? string.Empty, c.Description, c.Amount))
                    .ToList();
            }
        }

        // Per-note scrub stands alone: no chart-history context, no clone
        // comparison. Each note evaluated as the carrier would see it on the
        // claim — in isolation, against its visit's DX + charges.
        var focalText = Truncate(focal.PlainText ?? string.Empty, FocalNoteCharCap);

        var ctx = new ScrubContext(
            PatientId: focal.PatientId,
            WorkflowCaseId: focal.WorkflowCaseId,
            DoctorNoteId: focal.Id,
            ChartNoteId: focal.ChartNoteId,
            VisitAppointmentId: matchedApptId,
            FocalNoteDate: focal.NoteDate,
            FocalNoteDoctor: focalDoctorName,
            FocalNoteText: focalText,
            ItsVisitDiagnoses: visitDiagnoses,
            ItsVisitCharges: visitCharges,
            OtherNotes: Array.Empty<ScrubContextNote>(),
            ClonedFromPrior: false);

        var run = await _scrubber.ScrubAsync(ctx, ct);

        var result = new ScrubResult
        {
            DoctorNoteId = focal.Id,
            WorkflowCaseId = focal.WorkflowCaseId,
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
            "Note scrub: patient {PatientId}, note {NoteId} (dx {Dx}, charges {Charges}) -> {Verdict}.",
            focal.PatientId, focal.Id, visitDiagnoses.Count, visitCharges.Count, result.Verdict);

        return result;
    }

    // Latest per-note scrub for a specific DoctorNote.
    public async Task<ScrubResult?> GetLatestForNoteAsync(int doctorNoteId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ScrubResults.AsNoTracking()
            .Where(s => s.DoctorNoteId == doctorNoteId)
            .OrderByDescending(s => s.RanAt)
            .FirstOrDefaultAsync(ct);
    }

    // Auto-scrub gate: fire when a new note arrives if the patient's last
    // note-scrub never ran OR last verdict was a fail. Pass-state cases skip
    // so we don't re-scrub a clean chart on every arrival.
    public async Task<bool> ShouldAutoScrubAsync(int workflowCaseId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var latestVerdict = await db.ScrubResults
            .AsNoTracking()
            .Where(s => s.WorkflowCaseId == workflowCaseId)
            .OrderByDescending(s => s.RanAt)
            .Select(s => s.Verdict)
            .FirstOrDefaultAsync(ct);

        return latestVerdict is null || latestVerdict == ScrubVerdicts.Fail;
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max] + "…";

    private static double Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        int min = Math.Min(a.Length, b.Length);
        int max = Math.Max(a.Length, b.Length);
        int pre = 0;
        while (pre < min && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < min - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
        return (double)(pre + suf) / max;
    }
}

// Thrown when a per-note scrub can't run for some reason (focal note
// missing, etc.). Currently rare — kept for API symmetry with the previous
// case-level orchestrator.
public sealed class NoOutstandingChargesException : InvalidOperationException
{
    public int PatientId { get; }

    public NoOutstandingChargesException(int patientId)
        : base($"Patient {patientId} has no outstanding charges to scrub against.")
    {
        PatientId = patientId;
    }
}
