using System.Text.Json;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services;

// Orchestration only: reads via IInsuranceReadQueries (ChiroTouch, read-only),
// writes via the workflow services (EF). No raw SQL here.
public sealed class InsuranceSyncService
{
    private readonly IInsuranceReadQueries _reads;
    private readonly WorkflowCaseService _cases;
    private readonly SyncStateService _syncState;
    private readonly ILogger<InsuranceSyncService> _logger;

    public InsuranceSyncService(
        IInsuranceReadQueries reads,
        WorkflowCaseService cases,
        SyncStateService syncState,
        ILogger<InsuranceSyncService> logger)
    {
        _reads = reads;
        _cases = cases;
        _syncState = syncState;
        _logger = logger;
    }

    public async Task<SyncResult> ProcessAsync(CancellationToken ct)
    {
        if (!_reads.IsConfigured)
        {
            _logger.LogInformation("Insurance: ChiroTouch connection not configured; skipping source poll.");
            return SyncResult.Empty;
        }

        long lastSeen = await _syncState.GetLastSeenAsync(SyncKeys.LastSeenInsurancePolicyId);
        var rows = await _reads.GetNewPoliciesAsync(lastSeen);

        if (rows.Count == 0)
            return SyncResult.Empty;

        _logger.LogInformation("Insurance: {Count} new policy record(s) since ID {LastSeen}.",
            rows.Count, lastSeen);

        int casesTouched = 0;
        int eventsCreated = 0;
        long maxProcessedId = lastSeen;

        foreach (var (policy, patient) in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                int caseId = await _cases.CreateOrUpdateCaseAsync(
                    policy.PatientId, patient.FirstName, patient.LastName,
                    WorkflowStates.AwaitingPipVerification,
                    insuranceAddedAt: DateTime.UtcNow);
                casesTouched++;

                if (!await _cases.WorkflowEventExistsAsync(SourceTables.InsPolicies, policy.Id))
                {
                    await _cases.InsertWorkflowEventAsync(new WorkflowEvent
                    {
                        WorkflowCaseId = caseId,
                        PatientId = policy.PatientId,
                        EventType = EventTypes.InsuranceAdded,
                        SourceSystem = SourceSystems.PSChiro,
                        SourceTable = SourceTables.InsPolicies,
                        SourceRecordId = policy.Id,
                        EventDataJson = JsonSerializer.Serialize(policy)
                    });
                    eventsCreated++;
                    _logger.LogInformation(
                        "Insurance: case {CaseId} (patient {PatientId}) -> {State}, event InsuranceAdded (policy {PolicyId}, {InsCo}).",
                        caseId, policy.PatientId, WorkflowStates.AwaitingPipVerification, policy.Id, policy.InsCoName);
                }

                // Advance the cursor only after this record fully succeeds.
                if (policy.Id > maxProcessedId)
                    maxProcessedId = policy.Id;
                await _syncState.SetLastSeenAsync(SyncKeys.LastSeenInsurancePolicyId, maxProcessedId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Insurance: failed processing policy {PolicyId} (patient {PatientId}); stopping this cycle to preserve ordering.",
                    policy.Id, policy.PatientId);
                break; // leave cursor at the last good id; retry next poll
            }
        }

        return new SyncResult(rows.Count, casesTouched, eventsCreated);
    }
}
