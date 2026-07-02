using Lugiano.Workflow.SyncService.Services;

namespace Lugiano.Workflow.SyncService;

public sealed class Worker : BackgroundService
{
    private readonly InsuranceSyncService _insurance;
    private readonly ChartNoteSyncService _chartNotes;
    private readonly SyncStateService _syncState;
    private readonly WorkerOptions _options;
    private readonly ILogger<Worker> _logger;

    public Worker(
        InsuranceSyncService insurance,
        ChartNoteSyncService chartNotes,
        SyncStateService syncState,
        WorkerOptions options,
        ILogger<Worker> logger)
    {
        _insurance = insurance;
        _chartNotes = chartNotes;
        _syncState = syncState;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.PollingIntervalSeconds);
        _logger.LogInformation("Worker started. Polling every {Seconds}s.", _options.PollingIntervalSeconds);

        using var timer = new PeriodicTimer(interval);
        do
        {
            await RunCycleAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        try
        {
            var insurance = await _insurance.ProcessAsync(ct);
            var chartNotes = await _chartNotes.ProcessAsync(ct);
            // Second chart-note pass: catch notes that were EDITED/re-signed
            // after we first captured them (their ID never changes, so the
            // ProcessAsync ID-cursor above can't see them). Driven off the CN
            // signature clock.
            var notesRefreshed = await _chartNotes.ReconcileRecentlySignedAsync(ct);

            _logger.LogInformation(
                "Cycle complete. Insurance: found={InsFound}, casesTouched={InsCases}, events={InsEvents}. " +
                "ChartNotes: found={NoteFound}, casesTouched={NoteCases}, events={NoteEvents}, refreshed={NoteRefreshed}.",
                insurance.Found, insurance.CasesTouched, insurance.EventsCreated,
                chartNotes.Found, chartNotes.CasesTouched, chartNotes.EventsCreated, notesRefreshed);

            foreach (var state in await _syncState.GetAllAsync())
                _logger.LogInformation("SyncState[{Key}] = {LastSeenId} (updated {UpdatedAt:u}).",
                    state.SyncKey, state.LastSeenId, state.UpdatedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown requested — let the loop exit cleanly.
        }
        catch (Exception ex)
        {
            // Never let a cycle crash the worker; log and wait for the next tick.
            _logger.LogError(ex, "Poll cycle failed; will retry on next tick.");
        }
    }
}
