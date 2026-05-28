namespace Lugiano.Workflow.SyncService.Workflow.Models;

public sealed class SyncState
{
    public int Id { get; set; }
    public string SyncKey { get; set; } = string.Empty;
    public long LastSeenId { get; set; }
    public DateTime UpdatedAt { get; set; }
}
