namespace Lugiano.Workflow.SyncService.ChiroTouch.Models;

// READ-ONLY projection of dbo.InsPolicies (ChiroTouch). Never written.
public sealed class SourceInsurancePolicy
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public string? CoverageType { get; set; }
    public string? InsCoName { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public bool Hidden { get; set; }
}
