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
    // When the note was signed off in ChiroTouch (MIN CN SigTimestamp in
    // dbo.Signatures) — the real "doctor finished" clock, not NoteDate (midnight
    // only). Matches CT's "Signed: …" line. Null for unsigned / portal correction.
    public DateTime? SignedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    // Set when this row's content is refreshed from ChiroTouch after initial
    // capture (note edited/finalized — SOAPPtr repointed). Null until first
    // refresh, distinguishing a frozen first-capture row from a reconciled one.
    public DateTime? UpdatedAt { get; set; }
    // True when authored as a portal correction. Stays true even after the
    // PSChiro writeback sets ChartNoteId — routes the next failing scrub to
    // "Human Review" instead of the "Doctor Queue" (fresh, un-corrected notes).
    public bool IsPortalAuthored { get; set; }
}
