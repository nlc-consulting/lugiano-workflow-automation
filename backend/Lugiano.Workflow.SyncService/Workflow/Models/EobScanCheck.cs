namespace Lugiano.Workflow.SyncService.Workflow.Models;

// One row per check stub detected in a scanned EOB batch. Sedgwick-administered
// workers-comp EOBs print TWO stubs per payment (the Sedgwick-tracking view and
// the underlying-carrier view); both get rows here, linked via PairedCheckId so
// downstream consumers can collapse them when reconciling totals.
public sealed class EobScanCheck
{
    public int Id { get; set; }
    public int EobScanId { get; set; }

    // PDF page index (1-based, counts blank/back pages). NOT the EOB-internal
    // "Page 1 of 3" — see the EobScanner spike notes.
    public int PageNumber { get; set; }

    public string CheckNumber { get; set; } = string.Empty;
    public string? CheckDate { get; set; }
    public decimal Amount { get; set; }

    // Underlying insurance carrier as printed (e.g. "AIU INSURANCE CO").
    public string? Payer { get; set; }
    // TPA when separately identified on the stub (e.g. "GALLAGHER BASSETT").
    public string? Administrator { get; set; }

    // FK to the "other view" of the same payment. Symmetric — both rows of the
    // pair point at each other. Null when the EOB has only one stub.
    public int? PairedCheckId { get; set; }
}
