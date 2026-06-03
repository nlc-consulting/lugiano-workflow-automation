namespace Lugiano.Workflow.SyncService.Workflow.Models;

public sealed class DoctorNote
{
    public int Id { get; set; }
    public int WorkflowCaseId { get; set; }
    public int PatientId { get; set; }
    public int ChartNoteId { get; set; }
    // ChiroTouch doctor ID (dbo.ChartNotes.DoctorID). Joins to
    // Doctor.ChiroTouchDoctorId — NOT to Doctor.Id (our local PK).
    public int? DoctorId { get; set; }
    public DateTime? NoteDate { get; set; }
    public int? SoapPtr { get; set; }
    public string? RawRtf { get; set; }
    public string? PlainText { get; set; }
    public DateTime CreatedAt { get; set; }
}
