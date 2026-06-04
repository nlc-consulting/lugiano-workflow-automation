namespace Lugiano.Workflow.SyncService.Workflow.Models;

// One row per kickback round on a DoctorNote. Resolves when a new note arrives
// for the patient; escalates when RoundNumber exceeds the loop cap.
public sealed class CorrectionRequest
{
    public int Id { get; set; }
    public int DoctorNoteId { get; set; }
    public int WorkflowCaseId { get; set; }

    // CorrectionStates.* — see WorkflowConstants.
    public string State { get; set; } = string.Empty;

    public string? ReviewerEmail { get; set; }
    public string? ReviewerComments { get; set; }
    public string? MissingItemsJson { get; set; }
    public string? RecipientDoctorIdsJson { get; set; }
    // Per-send override: NULL means "used the doctor's saved email".
    public string? RecipientOverrideEmail { get; set; }
    public int RoundNumber { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
