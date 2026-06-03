namespace Lugiano.Workflow.SyncService;

public static class WorkflowStates
{
    public const string AwaitingInsurance = "AwaitingInsurance";
    public const string AwaitingPipVerification = "AwaitingPipVerification";
    public const string AwaitingDoctorNotes = "AwaitingDoctorNotes";
    public const string ReadyForAiScrubbing = "ReadyForAiScrubbing";
}

// Snapshot of a patient's billing-readiness flags. Owns the cascade so adding a
// new gate (signature, charges, scrub-passed, etc.) is a property + one branch
// here rather than a signature change at every call site. Always construct with
// named args so the boolean parameters stay readable.
public readonly record struct BillingReadiness(bool Insurance, bool Pip, bool Notes)
{
    public string DerivedState =>
        !Insurance ? WorkflowStates.AwaitingInsurance
        : !Pip     ? WorkflowStates.AwaitingPipVerification
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
