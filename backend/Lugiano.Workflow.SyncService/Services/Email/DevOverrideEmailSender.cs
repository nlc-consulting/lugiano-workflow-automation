using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services.Email;

// Dev-only decorator that redirects every outgoing email to a single address
// (Email:DevOverrideRecipient). Wraps whichever real sender is configured —
// stays transparent to the orchestration code. Persisted CorrectionRequest
// records still capture the original intended recipient, so audit isn't lost.
public sealed class DevOverrideEmailSender : IEmailSender
{
    private readonly IEmailSender _inner;
    private readonly string _overrideRecipient;
    private readonly ILogger<DevOverrideEmailSender> _logger;

    public DevOverrideEmailSender(
        IEmailSender inner,
        string overrideRecipient,
        ILogger<DevOverrideEmailSender> logger)
    {
        _inner = inner;
        _overrideRecipient = overrideRecipient;
        _logger = logger;
    }

    public Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var originalLabel = string.IsNullOrWhiteSpace(message.ToName)
            ? message.ToEmail
            : $"{message.ToName} <{message.ToEmail}>";

        var redirected = message with
        {
            ToEmail = _overrideRecipient,
            ToName = null,
            Subject = $"[DEV → {message.ToEmail}] {message.Subject}",
            PlainTextBody =
                $"=== DEV REDIRECT ===\n" +
                $"Original recipient: {originalLabel}\n" +
                $"====================\n\n" +
                message.PlainTextBody,
        };

        _logger.LogInformation(
            "Dev override: redirecting email for {Original} to {Override}.",
            message.ToEmail, _overrideRecipient);
        return _inner.SendAsync(redirected, ct);
    }
}
