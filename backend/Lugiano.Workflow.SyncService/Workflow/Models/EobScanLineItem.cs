namespace Lugiano.Workflow.SyncService.Workflow.Models;

// One row per CPT/HCPCS service line on an EOB. Includes zero-paid lines
// (denials, exhausted-policy rows) because their reason codes drive triage —
// the live-posting preview is what filters them out, not this layer.
public sealed class EobScanLineItem
{
    public int Id { get; set; }
    public int EobScanId { get; set; }

    public int PageNumber { get; set; }

    public string? ClaimNumber { get; set; }
    // Patient name EXACTLY as printed (no reordering, no normalization). Match
    // layer downstream is responsible for resolving variants like
    // "ZAMBRANO, AMADO" vs "AMADO ZAMBRANO" to a single ChiroTouch patient.
    public string? PatientNameRaw { get; set; }
    public string? BillNumber { get; set; }
    public string? ServiceDate { get; set; }
    // The check that paid this line (when Claude can determine it from layout).
    // Resolves to an EobScanCheck.CheckNumber within the same scan.
    public string? CheckNumber { get; set; }

    // Includes modifiers (e.g. "97150-GP"). DS strips modifiers — we keep them
    // because they actually affect reimbursement.
    public string ProcedureCode { get; set; } = string.Empty;

    public decimal BilledAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal WriteOffAmount { get; set; }

    // JSON array: [{"code":"00663","description":"..."}, ...]
    // Multiple codes per line is common — DS uses "/" delimiters in their xlsx
    // but JSON keeps the (code, description) pairing clean for downstream.
    // Descriptions are backfilled by the orchestrator from sibling lines in
    // the same scan when Claude shortcuts them on repeats.
    public string ReasonCodesJson { get; set; } = "[]";
}
