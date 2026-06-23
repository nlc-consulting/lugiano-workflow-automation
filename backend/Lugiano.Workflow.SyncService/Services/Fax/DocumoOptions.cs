namespace Lugiano.Workflow.SyncService.Services.Fax;

// Documo cloud-fax configuration. Bound from the "Documo" config section.
// PROD-TODO: ApiKey must move to a secrets store before any non-dev deploy
// (see task #36 — also covers PSChiro + Anthropic credentials).
public sealed class DocumoOptions
{
    public string BaseUrl { get; set; } = "https://api.documo.com";
    public string ApiKey { get; set; } = string.Empty;
    // Optional. If blank, Documo uses the account default "from" number.
    public string? FromNumber { get; set; }
}
