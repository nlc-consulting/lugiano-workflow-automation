using System.Text.Json.Serialization;

namespace Lugiano.Workflow.SyncService.Services.Scrubbing;

// Parsed scrubber output — mirrors the JSON the model returns via the
// submit_scrub_findings tool. We persist this as FindingsJson and use it
// directly for the portal UI.
public sealed class ScrubFindings
{
    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "needs_review";

    [JsonPropertyName("overall_confidence")]
    public int OverallConfidence { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("sections")]
    public ScrubSections Sections { get; set; } = new();

    [JsonPropertyName("diagnosis_alignment")]
    public ScrubAlignment DiagnosisAlignment { get; set; } = new();

    [JsonPropertyName("charge_alignment")]
    public ScrubAlignment ChargeAlignment { get; set; } = new();

    [JsonPropertyName("issues")]
    public List<ScrubIssue> Issues { get; set; } = new();
}

public sealed class ScrubSections
{
    [JsonPropertyName("subjective")] public ScrubSection Subjective { get; set; } = new();
    [JsonPropertyName("objective")] public ScrubSection Objective { get; set; } = new();
    [JsonPropertyName("assessment")] public ScrubAssessmentSection Assessment { get; set; } = new();
    [JsonPropertyName("treatment_plan")] public ScrubSection TreatmentPlan { get; set; } = new();
    [JsonPropertyName("primary_treatment")] public ScrubSection PrimaryTreatment { get; set; } = new();
}

public class ScrubSection
{
    [JsonPropertyName("present")] public bool Present { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}

public sealed class ScrubAssessmentSection : ScrubSection
{
    [JsonPropertyName("in_my_opinion_present")] public bool InMyOpinionPresent { get; set; }
}

public sealed class ScrubAlignment
{
    [JsonPropertyName("score")] public int Score { get; set; }
    [JsonPropertyName("issues")] public List<ScrubAlignmentIssue> Issues { get; set; } = new();
}

public sealed class ScrubAlignmentIssue
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("concern")] public string? Concern { get; set; }
}

public sealed class ScrubIssue
{
    [JsonPropertyName("severity")] public string Severity { get; set; } = "low";
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}
