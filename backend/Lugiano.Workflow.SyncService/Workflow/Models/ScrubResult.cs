namespace Lugiano.Workflow.SyncService.Workflow.Models;

public sealed class ScrubResult
{
    public int Id { get; set; }
    public int DoctorNoteId { get; set; }
    public int WorkflowCaseId { get; set; }

    // ScrubVerdicts.* — see WorkflowConstants.
    public string Verdict { get; set; } = string.Empty;
    public int OverallConfidence { get; set; }
    public string? Summary { get; set; }

    public string FindingsJson { get; set; } = "{}";
    public string? RawResponseJson { get; set; }

    public string ModelUsed { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;

    public DateTime RanAt { get; set; }
}
