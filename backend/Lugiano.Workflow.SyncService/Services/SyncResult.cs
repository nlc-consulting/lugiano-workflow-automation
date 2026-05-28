namespace Lugiano.Workflow.SyncService.Services;

public readonly record struct SyncResult(int Found, int CasesTouched, int EventsCreated)
{
    public static SyncResult Empty => new(0, 0, 0);
}
