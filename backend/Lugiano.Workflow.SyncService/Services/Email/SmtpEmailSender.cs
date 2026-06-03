using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Lugiano.Workflow.SyncService.Services.Email;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _password;
    private readonly bool _useStartTls;
    private readonly string _fromEmail;
    private readonly string _fromName;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger)
    {
        _host = config["Email:Smtp:Host"]
            ?? throw new InvalidOperationException("Email:Smtp:Host is required for SmtpEmailSender.");
        _port = int.TryParse(config["Email:Smtp:Port"], out var p) ? p : 587;
        _user = config["Email:Smtp:User"];
        _password = config["Email:Smtp:Password"];
        _useStartTls = bool.TryParse(config["Email:Smtp:UseStartTls"], out var s) ? s : true;
        _fromEmail = config["Email:From:Email"] ?? "noreply@lugianomedical.local";
        _fromName = config["Email:From:Name"] ?? "Lugiano Portal";
        _logger = logger;
    }

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        try
        {
            var mime = new MimeMessage();
            mime.From.Add(new MailboxAddress(_fromName, _fromEmail));
            mime.To.Add(new MailboxAddress(message.ToName ?? string.Empty, message.ToEmail));
            mime.Subject = message.Subject;
            mime.Body = new TextPart("plain") { Text = message.PlainTextBody };

            using var client = new SmtpClient();
            await client.ConnectAsync(
                _host, _port,
                _useStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto,
                ct);
            if (!string.IsNullOrEmpty(_user))
                await client.AuthenticateAsync(_user, _password ?? string.Empty, ct);
            await client.SendAsync(mime, ct);
            await client.DisconnectAsync(true, ct);

            _logger.LogInformation(
                "Correction email sent via SMTP: {Recipient} via {Host}:{Port}",
                message.ToEmail, _host, _port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SMTP send failed for {Recipient} via {Host}:{Port}",
                message.ToEmail, _host, _port);
            return false;
        }
    }
}
