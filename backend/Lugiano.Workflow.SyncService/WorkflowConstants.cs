namespace Lugiano.Workflow.SyncService;

public static class WorkflowStates
{
    public const string AwaitingInsurance = "AwaitingInsurance";
    public const string AwaitingPipVerification = "AwaitingPipVerification";
    public const string AwaitingDoctorNotes = "AwaitingDoctorNotes";
    public const string ReadyForAiScrubbing = "ReadyForAiScrubbing";

    // Billing-readiness cascade: insurance -> PIP verified -> doctor notes -> ready.
    // Single source of truth used by the worker (stored state) and the API (display).
    public static string Derive(bool insurance, bool notes, bool pip) =>
        !insurance ? AwaitingInsurance
        : !pip ? AwaitingPipVerification
        : !notes ? AwaitingDoctorNotes
        : ReadyForAiScrubbing;
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
