namespace Lugiano.Workflow.SyncService;

public static class WorkflowStates
{
    public const string AwaitingInsurance = "AwaitingInsurance";
    public const string AwaitingPipVerification = "AwaitingPipVerification";
    public const string AwaitingDoctorNotes = "AwaitingDoctorNotes";
    public const string ReadyForAiScrubbing = "ReadyForAiScrubbing";
    // Note has been sent back to the doctor; resumes the cascade when a
    // corrected ChartNote arrives via sync.
    public const string AwaitingDoctorCorrection = "AwaitingDoctorCorrection";
}

// Lifecycle of a single CorrectionRequest. Pending = created but not yet
// emailed; Sent = email dispatched; Resolved = corrected note arrived and
// auto-cleared; Escalated = hit the round cap, needs manual handling.
public static class CorrectionStates
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Resolved = "Resolved";
    public const string Escalated = "Escalated";
}

// Possible scrubber verdicts. Pass = ready for billing; NeedsReview = minor
// issues, reviewer should glance; Fail = significant issues, kickback warranted.
public static class ScrubVerdicts
{
    public const string Pass = "pass";
    public const string NeedsReview = "needs_review";
    public const string Fail = "fail";
}

// Snapshot of a patient's billing-readiness flags. Owns the cascade so adding a
// new gate (signature, charges, scrub-passed, etc.) is a property + one branch
// here rather than a signature change at every call site. Always construct with
// named args so the boolean parameters stay readable.
//
// NOTE: PIP is currently hidden from the UI and bypassed in the cascade — it
// isn't part of the billing critical path right now. The Pip field stays in
// the record (call sites still pass it) and the AwaitingPipVerification state
// constant stays, so reintroducing the gate is a one-line uncomment.
public readonly record struct BillingReadiness(bool Insurance, bool Pip, bool Notes)
{
    public string DerivedState =>
        !Insurance ? WorkflowStates.AwaitingInsurance
        // : !Pip  ? WorkflowStates.AwaitingPipVerification   // PIP gate — see note above
        : !Notes   ? WorkflowStates.AwaitingDoctorNotes
        : WorkflowStates.ReadyForAiScrubbing;
}

public static class EventTypes
{
    public const string InsuranceAdded = "InsuranceAdded";
    public const string DoctorNoteReceived = "DoctorNoteReceived";
    public const string PipVerified = "PipVerified";
}

public static class SyncKeys
{
    public const string LastSeenInsurancePolicyId = "LastSeenInsurancePolicyId";
    public const string LastSeenChartNoteId = "LastSeenChartNoteId";
}

public static class SourceTables
{
    public const string InsPolicies = "dbo.InsPolicies";
    public const string ChartNotes = "dbo.ChartNotes";
}

// Which system produced an event. Today everything comes from ChiroTouch; the
// portal/AI will add their own values later.
public static class SourceSystems
{
    public const string PSChiro = "PSChiro";
    public const string Portal = "Portal";
    public const string Ai = "AI";
    public const string ManualReview = "ManualReview";
}
