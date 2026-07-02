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
    // Latest note passed scrub and there are unbilled charges on the chart —
    // bill queued for next batch. Derived at read-time in CasesController.GetCases
    // (not stored) until state is refactored to per-appointment.
    public const string ReadyForBilling = "ReadyForBilling";
    // Insurance + notes + passing scrub in place, but no unbilled charges yet —
    // waiting for front-desk/billing to land the CPT lines. A charges-arrived
    // trigger advances this to ReadyForBilling automatically (task #29).
    public const string AwaitingCharges = "AwaitingCharges";
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
// gate is a property + one branch here, not a signature change at every call
// site. Always construct with named args so the booleans stay readable.
//
// NOTE: PIP is currently hidden from the UI and bypassed in the cascade — not
// on the billing critical path. The Pip field and AwaitingPipVerification state
// stay (call sites still pass Pip) so reintroducing the gate is a one-line uncomment.
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
    // CN signature cursor for the "note edited/re-signed" trigger. Stored as
    // DateTime.Ticks (SyncState.LastSeenId is a long) of the newest CN
    // SigTimestamp we've reconciled. Notes' IDs don't move on edit, so this
    // signature clock — not the ID cursor — is what surfaces changed notes.
    public const string LastSeenSignatureTicks = "LastSeenSignatureTicks";
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

// Lifecycle of an EOB scan upload. Queued = uploaded, not yet processing;
// Running = chunks fanning out to Claude; Completed = all chunks merged +
// persisted; Failed = terminal error, ErrorMessage explains.
public static class EobScanStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
