namespace Lugiano.Workflow.SyncService.Workflow.Models;

public sealed class DoctorNote
{
    public int Id { get; set; }
    public int WorkflowCaseId { get; set; }
    public int PatientId { get; set; }
    // Null for portal-authored corrections: the doctor responded inside our
    // portal instead of ChiroTouch, so there's no ChartNotes row to link to.
    // The unique index is filtered on NOT NULL so multiple portal-authored
    // rows can coexist.
    public int? ChartNoteId { get; set; }
    // ChiroTouch doctor ID (dbo.ChartNotes.DoctorID). Joins to
    // Doctor.ChiroTouchDoctorId — NOT to Doctor.Id (our local PK).
    public int? DoctorId { get; set; }
    public DateTime? NoteDate { get; set; }
    public int? SoapPtr { get; set; }
    public string? RawRtf { get; set; }
    public string? PlainText { get; set; }
    public DateTime CreatedAt { get; set; }
    // True when this row was authored as a portal correction (doctor responded
    // inside our portal). Stays true even after the PSChiro writeback sets
    // ChartNoteId — used to route the next failing scrub to "Human Review"
    // instead of back to the "Doctor Queue" (which is for fresh chart-sourced
    // notes that haven't been doctor-corrected yet).
    public bool IsPortalAuthored { get; set; }
}
