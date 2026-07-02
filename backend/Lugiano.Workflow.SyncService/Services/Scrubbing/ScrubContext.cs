namespace Lugiano.Workflow.SyncService.Services.Scrubbing;

// Per-note scrub context, matching PSChiro billing: each provider's chart note
// for a visit becomes one HCFA claim line, with its own per-Appointment
// Diagnoses and the visit's Transactions as charges. FocalNote / ItsVisit*
// describe the billable unit; OtherNotes is background context (not evaluated).
// ClonedFromPrior flags focal text >=95% identical to a recent prior note
// (the practice's "carry forward previous visit" templating habit).
public sealed record ScrubContext(
    int PatientId,
    int WorkflowCaseId,
    int DoctorNoteId,
    int? ChartNoteId,
    int? VisitAppointmentId,
    DateTime? FocalNoteDate,
    string? FocalNoteDoctor,
    string FocalNoteText,
    IReadOnlyList<string> ItsVisitDiagnoses,
    IReadOnlyList<ScrubChargeLine> ItsVisitCharges,
    IReadOnlyList<ScrubContextNote> OtherNotes,
    bool ClonedFromPrior = false);

// Brief context entries from the patient's chart. Truncated and few — purely
// background, not under evaluation. The model uses them to see prior dx /
// treatment continuity.
public sealed record ScrubContextNote(DateTime? NoteDate, string? Doctor, string Text);

// One billed/billable CPT line on the focal note's visit. The model can flag
// when documentation doesn't support a code on this list — but does NOT
// suggest different codes.
public sealed record ScrubChargeLine(string Code, string? Description, decimal Amount);
