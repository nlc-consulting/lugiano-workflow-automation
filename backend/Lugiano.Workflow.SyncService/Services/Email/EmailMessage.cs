namespace Lugiano.Workflow.SyncService.Services.Email;

public sealed record EmailMessage(
    string ToEmail,
    string? ToName,
    string Subject,
    string PlainTextBody,
    string? HtmlBody = null);
