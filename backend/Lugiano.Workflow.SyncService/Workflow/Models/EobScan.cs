namespace Lugiano.Workflow.SyncService.Workflow.Models;

// One row per uploaded EOB scan PDF. Tracks the upload + processing run that
// extracted checks + line items via Claude Vision. Replaces the DS mail-scan
// vendor workflow — the same PDF that used to go to DS now goes through here.
public sealed class EobScan
{
    public int Id { get; set; }

    // As-uploaded filename (e.g. "non-lockbox mail 4.22.26.pdf"). Kept verbatim
    // for audit + filename-derived date parsing.
    public string SourceFilename { get; set; } = string.Empty;

    // Parsed from filename when possible, else null. Display-only — does NOT
    // drive any business logic.
    public DateTime? ScanDate { get; set; }

    public int PageCount { get; set; }
    public long FileSizeBytes { get; set; }

    // Absolute path to the stored PDF on disk (for re-runs / audit). Null once
    // the PDF is cleaned up by the retention job.
    public string? StoredPdfPath { get; set; }

    // EobScanStatuses.* — see WorkflowConstants.
    public string Status { get; set; } = "queued";
    public string? ErrorMessage { get; set; }

    public DateTime UploadedAt { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Chunking config used — captured so we know what slice size produced a
    // given scan's output when accuracy drifts.
    public int ChunkSize { get; set; }
    public int ChunkOverlap { get; set; }

    // Cost + model tracking. Sized aggressively so a single scan's full run
    // (potentially 30+ chunks across a 400-page PDF) fits cleanly.
    public string ModelUsed { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCostUsd { get; set; }

    public List<EobScanCheck> Checks { get; set; } = new();
    public List<EobScanLineItem> LineItems { get; set; } = new();
}
