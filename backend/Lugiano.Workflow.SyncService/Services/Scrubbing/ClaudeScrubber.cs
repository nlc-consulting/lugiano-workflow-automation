using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services.Scrubbing;

// Anthropic Messages API client. Uses prompt caching on the system block
// (rules + JSON schema are stable across calls) and forces structured output
// via tool_use so we don't depend on the model returning valid JSON in prose.
public sealed class ClaudeScrubber : IScrubber
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ToolName = "submit_scrub_findings";
    private const string HttpClientName = "anthropic";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ClaudeScrubber> _logger;

    public ClaudeScrubber(IHttpClientFactory httpFactory, IConfiguration config, ILogger<ClaudeScrubber> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<ScrubRun> ScrubAsync(ScrubContext context, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey()
            ?? throw new InvalidOperationException("Anthropic API key not configured (Anthropic:ApiKey or ANTHROPIC_API_KEY).");
        var model = _config["Anthropic:Model"] ?? "claude-sonnet-4-5";
        var systemPrompt = ScrubbingPrompt.GetSystemPrompt(_config);
        var promptVersion = ScrubbingPrompt.GetPromptVersion(_config);

        var payload = BuildPayload(model, systemPrompt, context);
        using var http = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Anthropic API error {Status}: {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Anthropic API returned {(int)resp.StatusCode}.");
        }

        var findings = ParseToolInput(body)
            ?? throw new InvalidOperationException("Anthropic response had no submit_scrub_findings tool_use block.");

        return new ScrubRun(findings, body, model, promptVersion);
    }

    // Accept the canonical nested form (Anthropic:ApiKey), the flat top-level
    // form (ANTHROPIC_API_KEY in config), and the OS env var — whichever's set.
    private string? ResolveApiKey() =>
        _config["Anthropic:ApiKey"] is { Length: > 0 } a ? a
        : _config["ANTHROPIC_API_KEY"] is { Length: > 0 } flat ? flat
        : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    private static object BuildPayload(string model, string systemPrompt, ScrubContext ctx)
    {
        var userText = BuildUserPrompt(ctx);
        return new
        {
            model,
            max_tokens = 2048,
            // System block as a content array so we can mark it cacheable —
            // the rules + schema are stable, only the user content changes.
            system = new object[]
            {
                new
                {
                    type = "text",
                    text = systemPrompt,
                    cache_control = new { type = "ephemeral" },
                },
            },
            tools = new[] { BuildToolDefinition() },
            tool_choice = new { type = "tool", name = ToolName },
            messages = new[]
            {
                new { role = "user", content = userText },
            },
        };
    }

    private static string BuildUserPrompt(ScrubContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== NOTE TO SCRUB ===");
        sb.Append("Date of service: ");
        sb.AppendLine(ctx.NoteDate?.ToString("yyyy-MM-dd") ?? "unknown");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(ctx.NoteText) ? "(note text unavailable)" : ctx.NoteText);
        sb.AppendLine();

        sb.AppendLine("=== CHARGES BILLED ON THIS VISIT ===");
        if (ctx.VisitCharges.Count == 0)
        {
            sb.AppendLine("(no charges entered for this visit)");
        }
        else
        {
            foreach (var c in ctx.VisitCharges)
            {
                sb.Append($"- {c.Code} {c.Description ?? string.Empty} ${c.Amount:F2}");
                if (!string.IsNullOrWhiteSpace(c.Diagnoses))
                    sb.Append($"  | linked Dx: {c.Diagnoses}");
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        sb.AppendLine("=== PATIENT'S DOCUMENTED DIAGNOSES ===");
        if (ctx.PatientDiagnoses.Count == 0)
            sb.AppendLine("(none extracted from notes)");
        else
            foreach (var d in ctx.PatientDiagnoses)
                sb.AppendLine($"- {d}");
        sb.AppendLine();

        if (ctx.PriorNotes.Count > 0)
        {
            sb.AppendLine("=== RECENT PRIOR NOTES (for consistency check) ===");
            foreach (var p in ctx.PriorNotes)
            {
                sb.Append("Date: ");
                sb.AppendLine(p.NoteDate?.ToString("yyyy-MM-dd") ?? "unknown");
                sb.AppendLine(p.Text);
                sb.AppendLine("---");
            }
        }

        return sb.ToString();
    }

    private static object BuildToolDefinition() => new
    {
        name = ToolName,
        description = "Submit the scrubbing analysis findings for this chart note.",
        input_schema = new
        {
            type = "object",
            required = new[] { "verdict", "overall_confidence", "summary", "sections" },
            properties = new
            {
                verdict = new
                {
                    type = "string",
                    @enum = new[] { "pass", "needs_review", "fail" },
                    description = "Overall verdict for the note.",
                },
                overall_confidence = new
                {
                    type = "integer",
                    minimum = 0,
                    maximum = 100,
                    description = "How confident you are in this verdict (0-100).",
                },
                summary = new
                {
                    type = "string",
                    description = "1-2 sentence reviewer-readable summary.",
                },
                sections = new
                {
                    type = "object",
                    required = new[] { "subjective", "objective", "assessment", "treatment_plan", "primary_treatment" },
                    properties = new
                    {
                        subjective = SectionSchema(),
                        objective = SectionSchema(),
                        assessment = AssessmentSchema(),
                        treatment_plan = SectionSchema(),
                        primary_treatment = SectionSchema(),
                    },
                },
                diagnosis_alignment = AlignmentSchema(),
                charge_alignment = AlignmentSchema(),
                issues = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        required = new[] { "severity", "description" },
                        properties = new
                        {
                            severity = new { type = "string", @enum = new[] { "high", "medium", "low" } },
                            category = new { type = "string" },
                            description = new { type = "string" },
                        },
                    },
                },
            },
        },
    };

    private static object SectionSchema() => new
    {
        type = "object",
        required = new[] { "present" },
        properties = new
        {
            present = new { type = "boolean" },
            notes = new { type = "string", description = "Brief justification or what's missing." },
        },
    };

    private static object AssessmentSchema() => new
    {
        type = "object",
        required = new[] { "present", "in_my_opinion_present" },
        properties = new
        {
            present = new { type = "boolean" },
            in_my_opinion_present = new
            {
                type = "boolean",
                description = "True iff the assessment contains 'in my opinion' or directly equivalent certainty language.",
            },
            notes = new { type = "string" },
        },
    };

    private static object AlignmentSchema() => new
    {
        type = "object",
        properties = new
        {
            score = new { type = "integer", minimum = 0, maximum = 100 },
            issues = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        code = new { type = "string" },
                        concern = new { type = "string" },
                    },
                },
            },
        },
    };

    // Anthropic returns: { content: [ { type: "tool_use", name: "submit_scrub_findings", input: {...} } ] }
    // Extract the input object and deserialize as ScrubFindings.
    private static ScrubFindings? ParseToolInput(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("content", out var content)) return null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                && block.TryGetProperty("name", out var n) && n.GetString() == ToolName
                && block.TryGetProperty("input", out var input))
            {
                return JsonSerializer.Deserialize<ScrubFindings>(input.GetRawText());
            }
        }
        return null;
    }
}
