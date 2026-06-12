using System.Text.Json;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.ChiroTouch.Models;
using Lugiano.Workflow.SyncService.Services.Scrubbing;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Lugiano.Workflow.SyncService.Util;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services;

// Orchestration only: reads via IChartNoteReadQueries (ChiroTouch, read-only),
// writes via the workflow services (EF). No raw SQL here.
public sealed class ChartNoteSyncService
{
    private readonly IChartNoteReadQueries _reads;
    private readonly IPatientStatusQueries _status;
    private readonly WorkflowCaseService _cases;
    private readonly SyncStateService _syncState;
    private readonly CorrectionRequestService _corrections;
    private readonly ScrubOrchestrator _scrubs;
    private readonly ILogger<ChartNoteSyncService> _logger;

    public ChartNoteSyncService(
        IChartNoteReadQueries reads,
        IPatientStatusQueries status,
        WorkflowCaseService cases,
        SyncStateService syncState,
        CorrectionRequestService corrections,
        ScrubOrchestrator scrubs,
        ILogger<ChartNoteSyncService> logger)
    {
        _reads = reads;
        _status = status;
        _cases = cases;
        _syncState = syncState;
        _corrections = corrections;
        _scrubs = scrubs;
        _logger = logger;
    }

    public async Task<SyncResult> ProcessAsync(CancellationToken ct)
    {
        if (!_reads.IsConfigured)
        {
            _logger.LogInformation("ChartNotes: ChiroTouch connection not configured; skipping source poll.");
            return SyncResult.Empty;
        }

        long lastSeen = await _syncState.GetLastSeenAsync(SyncKeys.LastSeenChartNoteId);
        var rows = await _reads.GetNewChartNotesAsync(lastSeen);

        if (rows.Count == 0)
            return SyncResult.Empty;

        _logger.LogInformation("ChartNotes: {Count} new note(s) since ID {LastSeen}.",
            rows.Count, lastSeen);

        int casesTouched = 0;
        int eventsCreated = 0;
        long maxProcessedId = lastSeen;

        foreach (var (note, patient) in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Reconcile the WHOLE patient, not just this note: pull current
                // insurance + notes truth (real dates) so the case reflects reality.
                var status = await _status.GetStatusAsync(note.PatientId);
                int caseId = await _cases.CreateOrUpdateCaseAsync(
                    note.PatientId, patient.FirstName, patient.LastName,
                    hasInsurance: status.HasInsurance,
                    insuranceAddedAt: status.InsuranceEffectiveDate,
                    hasNotes: status.HasNotes,
                    doctorNotesReceivedAt: status.LatestNoteDate);
                casesTouched++;

                // First time we've encountered this patient → backfill their
                // entire historical note set from ChiroTouch before processing
                // the trigger note. Idempotent: re-runs no-op via existence
                // check. Subsequent polls for the same patient skip this block.
                if (!await _cases.PatientHasAnyDoctorNoteAsync(note.PatientId))
                {
                    var history = await _reads.GetAllChartNotesForPatientAsync(note.PatientId);
                    if (history.Count > 0)
                    {
                        _logger.LogInformation(
                            "ChartNotes: first encounter with patient {PatientId} — backfilling {Count} historical note(s).",
                            note.PatientId, history.Count);
                        foreach (var (h, _) in history)
                        {
                            ct.ThrowIfCancellationRequested();
                            await EnsureDoctorNoteSavedAsync(caseId, h);
                        }
                    }
                }

                // Trigger note itself. No-op if the backfill above already saved it.
                await EnsureDoctorNoteSavedAsync(caseId, note);

                if (!await _cases.WorkflowEventExistsAsync(SourceTables.ChartNotes, note.Id))
                {
                    await _cases.InsertWorkflowEventAsync(new WorkflowEvent
                    {
                        WorkflowCaseId = caseId,
                        PatientId = note.PatientId,
                        EventType = EventTypes.DoctorNoteReceived,
                        SourceSystem = SourceSystems.PSChiro,
                        SourceTable = SourceTables.ChartNotes,
                        SourceRecordId = note.Id,
                        EventDataJson = JsonSerializer.Serialize(note)
                    });
                    eventsCreated++;
                    _logger.LogInformation(
                        "ChartNotes: case {CaseId} (patient {PatientId}) reconciled, event DoctorNoteReceived (note {NoteId}).",
                        caseId, note.PatientId, note.Id);
                }

                // Auto-resolve any open kickback for this patient: a new note
                // arriving is the doctor's response (whether it's a fresh chart
                // entry or a corrected one).
                await _corrections.ResolveOpenForPatientAsync(note.PatientId, ct);

                // Auto-scrub policy: every new DoctorNote gets its own scrub.
                // Per-note model — older notes' verdicts no longer gate newer
                // ones. The gate's only job now is idempotency (a re-run of
                // sync that re-encounters the same note doesn't double-scrub).
                // Scrub errors are logged but never break the sync cycle —
                // sync's job is to capture state.
                try
                {
                    var doctorNoteId = await _cases.GetDoctorNoteIdByChartNoteIdAsync(note.Id);
                    if (doctorNoteId.HasValue
                        && await _scrubs.ShouldAutoScrubAsync(doctorNoteId.Value, ct))
                    {
                        var result = await _scrubs.RunForNoteAsync(doctorNoteId.Value, ct);
                        _logger.LogInformation(
                            "ChartNotes: auto-scrubbed note {NoteId} (case {CaseId}, patient {PatientId}) -> {Verdict}.",
                            doctorNoteId.Value, caseId, note.PatientId, result.Verdict);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ChartNotes: auto-scrub failed for note {NoteId} (case {CaseId}); continuing sync.",
                        note.Id, caseId);
                }

                if (note.Id > maxProcessedId)
                    maxProcessedId = note.Id;
                await _syncState.SetLastSeenAsync(SyncKeys.LastSeenChartNoteId, maxProcessedId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ChartNotes: failed processing note {NoteId} (patient {PatientId}); stopping this cycle to preserve ordering.",
                    note.Id, note.PatientId);
                break;
            }
        }

        return new SyncResult(rows.Count, casesTouched, eventsCreated);
    }

    // Inserts a DoctorNote row for a single source ChartNote, reconstructing
    // the RTF and plain text along the way. Idempotent — exits cleanly if the
    // ChartNoteId is already in our table. One bad RTF chain logs and stores
    // null text rather than breaking the whole sync.
    private async Task EnsureDoctorNoteSavedAsync(int caseId, SourceChartNote note)
    {
        if (await _cases.DoctorNoteExistsAsync(note.Id)) return;

        string? rawRtf = null;
        try
        {
            if (note.SoapPtr is int ptr and not 0)
                rawRtf = await _reads.GetNoteRtfAsync(ptr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ChartNotes: RTF reconstruction failed for note {NoteId}; storing without text.",
                note.Id);
        }

        await _cases.InsertDoctorNoteAsync(new DoctorNote
        {
            WorkflowCaseId = caseId,
            PatientId = note.PatientId,
            ChartNoteId = note.Id,
            DoctorId = note.DoctorId,
            NoteDate = note.NoteDate,
            SoapPtr = note.SoapPtr,
            RawRtf = rawRtf,
            PlainText = RtfConverter.ToPlainText(rawRtf),
        });
    }
}
