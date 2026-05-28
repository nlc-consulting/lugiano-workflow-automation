namespace Lugiano.Workflow.SyncService.ChiroTouch.Models;

// READ-ONLY projection of dbo.ChartNotes (ChiroTouch). Never written.
public sealed class SourceChartNote
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public int? DoctorId { get; set; }
    public DateTime? NoteDate { get; set; }
    public int? SoapPtr { get; set; }
    public string? Status { get; set; }
}
