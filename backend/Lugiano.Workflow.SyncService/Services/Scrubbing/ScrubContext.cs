namespace Lugiano.Workflow.SyncService.Services.Scrubbing;

// Everything the scrubber needs to evaluate a single note. Built once per
// scrub by the orchestrator from our DB + ChiroTouch reads.
public sealed record ScrubContext(
    int DoctorNoteId,
    int ChartNoteId,
    DateTime? NoteDate,
    string NoteText,
    IReadOnlyList<ScrubCharge> VisitCharges,
    IReadOnlyList<string> PatientDiagnoses,
    IReadOnlyList<ScrubPriorNote> PriorNotes);

public sealed record ScrubCharge(string Code, string? Description, decimal Amount, string? Diagnoses);

public sealed record ScrubPriorNote(DateTime? NoteDate, string Text);
