namespace Lugiano.Workflow.SyncService;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    // Prototype polls every 30 seconds.
    public int PollingIntervalSeconds { get; set; } = 30;
}
