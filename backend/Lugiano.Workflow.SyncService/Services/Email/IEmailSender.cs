namespace Lugiano.Workflow.SyncService.Services.Email;

public interface IEmailSender
{
    Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default);
}
