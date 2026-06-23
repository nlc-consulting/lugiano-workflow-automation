using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lugiano.Workflow.SyncService.Services.Fax;

// Thin wrapper around Documo's v1 fax-send REST endpoint. One method:
// SendAsync(toFax, pdfBytes, filename) -> { faxId, status }.
//
// Auth: Documo's documented pattern is HTTP Basic where the API key is the
// username and the password is empty. (Their JWT-format token works as a
// Basic credential — Documo treats the whole token as the "username".)
// If your account has been migrated to Bearer auth, flip BuildAuthHeader().
//
// Endpoint shape (confirm against the dashboard's API ref before live use):
//   POST {BaseUrl}/v1/faxes
//   Content-Type: multipart/form-data
//   Fields:
//     faxNumber  — recipient (E.164 or 10-digit US)
//     attachment — file bytes (the PDF)
//     coverPage  — "false" (we generate our own first page)
//     callerId   — optional, overrides Documo account default
//   Response (200): { "id": "...", "status": "queued" }
public sealed class DocumoFaxClient
{
    private readonly HttpClient _http;
    private readonly DocumoOptions _options;
    private readonly ILogger<DocumoFaxClient> _logger;

    public DocumoFaxClient(HttpClient http, IOptions<DocumoOptions> options, ILogger<DocumoFaxClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("Documo:BaseUrl is not configured.");
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<DocumoSendResult> SendAsync(
        string toFax, byte[] pdfBytes, string filename, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Documo:ApiKey is not configured.");

        var normalized = NormalizeFaxNumber(toFax);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"Recipient fax number '{toFax}' is not a valid 10-digit US number.");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(normalized), "faxNumber");
        form.Add(new StringContent("false"), "coverPage");
        if (!string.IsNullOrWhiteSpace(_options.FromNumber))
            form.Add(new StringContent(_options.FromNumber!), "callerId");

        var pdf = new ByteArrayContent(pdfBytes);
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "attachment", filename);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/faxes")
        {
            Content = form,
        };
        req.Headers.Authorization = BuildAuthHeader();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Documo send failed: to={To} status={Status} body={Body}",
                normalized, (int)resp.StatusCode, body);
            throw new InvalidOperationException(
                $"Documo send failed ({(int)resp.StatusCode}): {Trim(body, 400)}");
        }

        var (id, status) = ParseResponse(body);
        _logger.LogInformation(
            "Documo send accepted: to={To} faxId={FaxId} status={Status}", normalized, id, status);
        return new DocumoSendResult(id, status, normalized, pdfBytes.Length);
    }

    // Basic auth with the JWT-style API key as the username and empty password.
    // To switch to Bearer (newer Documo accounts may require it), replace with:
    //   return new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    private AuthenticationHeaderValue BuildAuthHeader()
    {
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(_options.ApiKey + ":"));
        return new AuthenticationHeaderValue("Basic", encoded);
    }

    // Documo accepts E.164 (+13125551234) or plain digits. Strip everything
    // non-numeric, keep the last 10 (handles "+1", "1-", "(312) 555-...").
    // Returns "" if we don't have a sensible US-shaped number.
    private static string NormalizeFaxNumber(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length < 10) return string.Empty;
        return digits[^10..];
    }

    // Documo's standard success body is { "id": "...", "status": "..." }.
    // Some endpoints wrap data under { "data": {...} } — handle both.
    private static (string Id, string Status) ParseResponse(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            var node = root.TryGetProperty("data", out var d) ? d : root;
            var id = node.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var status = node.TryGetProperty("status", out var s) ? s.GetString() ?? "queued" : "queued";
            return (id, status);
        }
        catch
        {
            return ("", "queued");
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public sealed record DocumoSendResult(string FaxId, string Status, string To, int Bytes);
