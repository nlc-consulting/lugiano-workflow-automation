namespace Lugiano.Workflow.SyncService.Workflow.Models;

public sealed class WorkflowEvent
{
    public int Id { get; set; }
    public int WorkflowCaseId { get; set; }
    public int PatientId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string SourceTable { get; set; } = string.Empty;
    public long SourceRecordId { get; set; }
    public string? EventDataJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
