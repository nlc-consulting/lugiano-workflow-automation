using System.Text;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services.Email;

public sealed class FileEmailSender : IEmailSender
{
    private readonly string _outputDir;
    private readonly string _fromEmail;
    private readonly string _fromName;
    private readonly ILogger<FileEmailSender> _logger;

    public FileEmailSender(IConfiguration config, ILogger<FileEmailSender> logger)
    {
        _outputDir = config["Email:File:OutputDirectory"] ?? "emails";
        _fromEmail = config["Email:From:Email"] ?? "noreply@lugianomedical.local";
        _fromName = config["Email:From:Name"] ?? "Lugiano Portal";
        _logger = logger;
        Directory.CreateDirectory(_outputDir);
    }

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var safeTo = Sanitize(message.ToEmail);
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safeTo}.eml";
        var path = Path.Combine(_outputDir, name);

        var sb = new StringBuilder();
        sb.AppendLine($"From: \"{_fromName}\" <{_fromEmail}>");
        sb.AppendLine(message.ToName is null
            ? $"To: {message.ToEmail}"
            : $"To: \"{message.ToName}\" <{message.ToEmail}>");
        sb.AppendLine($"Subject: {message.Subject}");
        sb.AppendLine($"Date: {DateTime.UtcNow:R}");
        sb.AppendLine("Content-Type: text/plain; charset=utf-8");
        sb.AppendLine();
        sb.AppendLine(message.PlainTextBody);

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
        _logger.LogInformation(
            "Correction email written to disk (FileEmailSender): {Path} -> {Recipient}",
            path, message.ToEmail);
        return true;
    }

    private static string Sanitize(string email) =>
        new string(email.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
