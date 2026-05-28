namespace Lugiano.Workflow.SyncService.Workflow.Models;

public sealed class WorkflowCase
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string CurrentState { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Per-flow stamps: when each milestone was first recorded in the portal.
    // PipVerifiedAt is editable (rep can set the actual approval date).
    public DateTime? InsuranceAddedAt { get; set; }
    public DateTime? DoctorNotesReceivedAt { get; set; }
    public DateTime? PipVerifiedAt { get; set; }

    // Navigation properties (#1) — convenient for timeline/admin queries.
    public ICollection<WorkflowEvent> Events { get; set; } = new List<WorkflowEvent>();
    public ICollection<DoctorNote> DoctorNotes { get; set; } = new List<DoctorNote>();
}
