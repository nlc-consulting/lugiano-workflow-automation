namespace Lugiano.Workflow.SyncService.Services.Scrubbing;

public interface IScrubber
{
    // Returns the parsed findings + the raw model response (for audit) +
    // the model identifier used. Throws on transport/parse failure.
    Task<ScrubRun> ScrubAsync(ScrubContext context, CancellationToken ct = default);
}

public sealed record ScrubRun(ScrubFindings Findings, string RawResponseJson, string ModelUsed, string PromptVersion);
