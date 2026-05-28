namespace Lugiano.Workflow.SyncService.ChiroTouch.Models;

// READ-ONLY projection of dbo.Patients (ChiroTouch). Never written.
public sealed class SourcePatient
{
    public int Id { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}
