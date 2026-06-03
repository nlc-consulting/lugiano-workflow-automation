using System.Text.Json;
using Lugiano.Workflow.SyncService.ChiroTouch;
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
    private readonly ILogger<ChartNoteSyncService> _logger;

    public ChartNoteSyncService(
        IChartNoteReadQueries reads,
        IPatientStatusQueries status,
        WorkflowCaseService cases,
        SyncStateService syncState,
        ILogger<ChartNoteSyncService> logger)
    {
        _reads = reads;
        _status = status;
        _cases = cases;
        _syncState = syncState;
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

                if (!await _cases.DoctorNoteExistsAsync(note.Id))
                {
                    string? rawRtf = note.SoapPtr is int ptr and not 0
                        ? await _reads.GetNoteRtfAsync(ptr)
                        : null;
                    string? plainText = RtfConverter.ToPlainText(rawRtf);

                    await _cases.InsertDoctorNoteAsync(new DoctorNote
                    {
                        WorkflowCaseId = caseId,
                        PatientId = note.PatientId,
                        ChartNoteId = note.Id,
                        DoctorId = note.DoctorId,
                        NoteDate = note.NoteDate,
                        SoapPtr = note.SoapPtr,
                        RawRtf = rawRtf,
                        PlainText = plainText
                    });
                }

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
}
