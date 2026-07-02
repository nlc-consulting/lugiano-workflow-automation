using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services.EobScanning;

// Anthropic Messages API client for EOB extraction. Mirrors the scrubber:
// HTTP via IHttpClientFactory, prompt cache on the (stable) system block,
// forced tool_use to avoid parsing prose JSON. Prompt + tool schema validated
// via backend/scratch/EobScanSpike (4/22 ground-truth diff); text in ClaudeEobPrompt.
public interface IClaudeEobExtractor
{
    Task<EobExtractionResult> ExtractAsync(PdfChunk chunk, CancellationToken ct = default);
}

public sealed record EobExtractedCheck(
    int PageNumber,
    string CheckNumber,
    string? CheckDate,
    decimal Amount,
    string? Payer,
    string? Administrator);

public sealed record EobExtractedReason(string Code, string? Description);

public sealed record EobExtractedLineItem(
    int PageNumber,
    string? ClaimNumber,
    string? PatientName,
    string? BillNumber,
    string? ServiceDate,
    string? CheckNumber,
    string ProcedureCode,
    decimal Billed,
    decimal Allowed,
    decimal Paid,
    decimal WriteOff,
    IReadOnlyList<EobExtractedReason> ReasonCodes);

public sealed record EobExtractionResult(
    IReadOnlyList<EobExtractedCheck> Checks,
    IReadOnlyList<EobExtractedLineItem> LineItems,
    int InputTokens,
    int OutputTokens,
    string ModelUsed,
    string StopReason);

public sealed class ClaudeEobExtractor : IClaudeEobExtractor
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ToolName = "submit_eob_extraction";
    private const string HttpClientName = "anthropic";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ClaudeEobExtractor> _logger;

    public ClaudeEobExtractor(IHttpClientFactory httpFactory, IConfiguration config, ILogger<ClaudeEobExtractor> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<EobExtractionResult> ExtractAsync(PdfChunk chunk, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey()
            ?? throw new InvalidOperationException("Anthropic API key not configured (Anthropic:ApiKey or ANTHROPIC_API_KEY).");
        // Sonnet 4.5 by default (matches the scrubber). Bump via config once
        // 4.6 quality is validated on EOB scans specifically.
        var model = _config["Anthropic:Model"] ?? "claude-sonnet-4-5";

        var base64 = Convert.ToBase64String(chunk.Bytes);
        var payload = BuildPayload(model, base64, chunk.StartPage);
        var json = JsonSerializer.Serialize(payload);

        using var http = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        // PDF document blocks are GA — beta header dropped 6/30/2026 after
        // all-chunks-400 failures on scan #7. The sonnet-4-5 API now rejects
        // "pdfs-2024-09-25" as unknown; re-add only if PDFs break again.

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Anthropic EOB API error {Status} on chunk pp{Start}-{End}: {Body}",
                (int)resp.StatusCode, chunk.StartPage, chunk.EndPage, body);
            throw new InvalidOperationException($"Anthropic API returned {(int)resp.StatusCode} on chunk pp{chunk.StartPage}-{chunk.EndPage}.");
        }

        using var doc = JsonDocument.Parse(body);
        var stopReason = doc.RootElement.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "" : "";
        if (stopReason == "max_tokens")
            _logger.LogWarning(
                "EOB extraction TRUNCATED on chunk pp{Start}-{End} — model hit max_tokens. Extraction is incomplete.",
                chunk.StartPage, chunk.EndPage);

        var (inTok, outTok) = ParseTokens(doc.RootElement);
        var input = ExtractToolInput(doc.RootElement)
            ?? throw new InvalidOperationException(
                $"No {ToolName} tool_use block in response for chunk pp{chunk.StartPage}-{chunk.EndPage}.");

        return new EobExtractionResult(
            Checks: ParseChecks(input),
            LineItems: ParseLineItems(input),
            InputTokens: inTok,
            OutputTokens: outTok,
            ModelUsed: model,
            StopReason: stopReason);
    }

    private string? ResolveApiKey() =>
        _config["Anthropic:ApiKey"] is { Length: > 0 } a ? a
        : _config["ANTHROPIC_API_KEY"] is { Length: > 0 } flat ? flat
        : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    private static object BuildPayload(string model, string pdfBase64, int firstPageOffset) => new
    {
        model,
        // 32K leaves headroom for a chunk's typical output. Sonnet 4.5 supports
        // up to 64K output if we need to go bigger later.
        max_tokens = 32768,
        system = new object[]
        {
            new
            {
                type = "text",
                text = ClaudeEobPrompt.SystemPrompt,
                cache_control = new { type = "ephemeral" },
            },
        },
        tools = new[] { ClaudeEobPrompt.ToolDefinition },
        tool_choice = new { type = "tool", name = ToolName },
        messages = new[]
        {
            new
            {
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "document",
                        source = new
                        {
                            type = "base64",
                            media_type = "application/pdf",
                            data = pdfBase64,
                        },
                    },
                    new
                    {
                        type = "text",
                        text =
                            $"Extract every check and every service line from this scanned EOB slice.\n" +
                            $"Page numbers in your output must reflect the ORIGINAL document — this slice starts at page {firstPageOffset}, " +
                            $"so the first page you see should be reported as page {firstPageOffset}.",
                    },
                },
            },
        },
    };

    private static (int inTok, int outTok) ParseTokens(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)) return (0, 0);
        var inTok = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
        var outTok = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
        return (inTok, outTok);
    }

    private static JsonElement? ExtractToolInput(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content)) return null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                && block.TryGetProperty("name", out var n) && n.GetString() == ToolName
                && block.TryGetProperty("input", out var input))
            {
                return input.Clone();
            }
        }
        return null;
    }

    private static List<EobExtractedCheck> ParseChecks(JsonElement? inputOpt)
    {
        var list = new List<EobExtractedCheck>();
        if (inputOpt is not JsonElement input) return list;
        if (!input.TryGetProperty("checks", out var checks)) return list;
        foreach (var c in checks.EnumerateArray())
        {
            list.Add(new EobExtractedCheck(
                PageNumber: GetInt(c, "page_number"),
                CheckNumber: GetStr(c, "check_number") ?? "",
                CheckDate: GetStr(c, "check_date"),
                Amount: GetDec(c, "amount"),
                Payer: GetStr(c, "payer"),
                Administrator: GetStr(c, "administrator")));
        }
        return list;
    }

    private static List<EobExtractedLineItem> ParseLineItems(JsonElement? inputOpt)
    {
        var list = new List<EobExtractedLineItem>();
        if (inputOpt is not JsonElement input) return list;
        if (!input.TryGetProperty("line_items", out var items)) return list;
        foreach (var l in items.EnumerateArray())
        {
            var reasons = new List<EobExtractedReason>();
            if (l.TryGetProperty("reason_codes", out var rc))
            {
                foreach (var r in rc.EnumerateArray())
                {
                    var code = GetStr(r, "code");
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    reasons.Add(new EobExtractedReason(code, GetStr(r, "description")));
                }
            }
            list.Add(new EobExtractedLineItem(
                PageNumber: GetInt(l, "page_number"),
                ClaimNumber: GetStr(l, "claim_number"),
                PatientName: GetStr(l, "patient_name"),
                BillNumber: GetStr(l, "bill_number"),
                ServiceDate: GetStr(l, "service_date"),
                CheckNumber: GetStr(l, "check_number"),
                ProcedureCode: GetStr(l, "procedure_code") ?? "",
                Billed: GetDec(l, "billed"),
                Allowed: GetDec(l, "allowed"),
                Paid: GetDec(l, "paid"),
                WriteOff: GetDec(l, "write_off"),
                ReasonCodes: reasons));
        }
        return list;
    }

    private static string? GetStr(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int GetInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetInt32(),
            JsonValueKind.String => int.TryParse(v.GetString(), out var i) ? i : 0,
            _ => 0,
        };
    }

    private static decimal GetDec(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return 0m;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(v.GetString(), out var d) ? d : 0m,
            _ => 0m,
        };
    }
}
