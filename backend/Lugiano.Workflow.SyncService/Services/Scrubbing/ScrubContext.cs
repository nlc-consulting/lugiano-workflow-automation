namespace Lugiano.Workflow.SyncService.Services.Scrubbing;

// Everything the scrubber needs to evaluate a single note in the context of
// the patient's full chart-note history. Built once per scrub by the
// orchestrator from our DB + ChiroTouch reads.
//
// OtherNotes contains every other note for this patient (not just prior), so
// the model can spot holistic patterns — documented-but-never-billed diagnoses,
// billed-but-never-documented charges, narrative drift across visits, etc.
public sealed record ScrubContext(
    int DoctorNoteId,
    int ChartNoteId,
    DateTime? NoteDate,
    string NoteText,
    IReadOnlyList<ScrubCharge> VisitCharges,
    IReadOnlyList<string> PatientDiagnoses,
    IReadOnlyList<ScrubOtherNote> OtherNotes);

public sealed record ScrubCharge(string Code, string? Description, decimal Amount, string? Diagnoses);

public sealed record ScrubOtherNote(DateTime? NoteDate, string Text);
