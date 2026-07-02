using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lugiano.Workflow.SyncService.Services.Fax;

// Thin wrapper around Documo's v1 fax-send REST endpoint.
// Auth: HTTP Basic with the API key as username, empty password (Documo's
// JWT token works as the whole "username"). If your account was migrated to
// Bearer auth, flip BuildAuthHeader().
//
// Endpoint shape (confirm against the dashboard's API ref before live use):
//   POST {BaseUrl}/v1/faxes  multipart/form-data
//   Fields: faxNumber (E.164 or 10-digit US), attachment (PDF bytes),
//     coverPage="false" (we generate our own page 1), callerId (optional).
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
        // Documo's POST /v1/faxes wants "attachments" (plural) per their docs.
        form.Add(pdf, "attachments", filename);

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

    // Documo's non-standard auth: literal "Basic " + the raw JWT (NOT
    // base64 user:password). Anything else returns "Wrong authorization type"
    // with their JWT-based keys.
    private AuthenticationHeaderValue BuildAuthHeader() =>
        new("Basic", _options.ApiKey);

    // Documo requires E.164 with country code (+1 for US): strip non-numeric,
    // take last 10 digits, prepend "1". Without the prefix Documo rejects with
    // "Invalid country calling code".
    private static string NormalizeFaxNumber(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length < 10) return string.Empty;
        return "1" + digits[^10..];
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
