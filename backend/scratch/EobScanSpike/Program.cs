// Standalone spike — prototype Claude Vision extraction over a scanned-mail
// EOB PDF. Mirrors the production ClaudeScrubber HTTP/tool_use pattern
// exactly (raw Anthropic Messages API, forced tool output) so once accuracy
// looks good we can lift the prompt + tool schema straight into SyncService.
//
// HOW TO RUN (PowerShell, from repo root):
//
//   dotnet run --project backend/scratch/EobScanSpike -- `
//     "\\serverlm\EOBs\papr eob 2026 year total\papr eob monthly 042026\papr eob daily 042226\non-lockbox mail 4.22.26.pdf" `
//     1 10
//
// Args: <pdfPath> <pageStartInclusive> <pageEndInclusive>
//
// API key sourced from backend/Lugiano.Workflow.SyncService/appsettings.Development.json
// (same Anthropic:ApiKey already used by the scrubber). No DB writes — pure
// dry-run, prints the extracted JSON + a side-by-side CSV stub for diffing
// against DS's EOB_Details output.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: dotnet run -- <pdfPath> <pageStart> <pageEnd>");
    return 1;
}
var pdfPath = args[0];
var pageStart = int.Parse(args[1]);
var pageEnd = int.Parse(args[2]);

if (!File.Exists(pdfPath))
{
    Console.Error.WriteLine($"PDF not found: {pdfPath}");
    return 1;
}

// Resolve API key from the scrubber's existing dev config so we don't have
// to maintain a second copy. Path is relative to the scratch project root.
var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "Lugiano.Workflow.SyncService", "appsettings.Development.json"), optional: true)
    .AddEnvironmentVariables()
    .Build();
var apiKey = config["Anthropic:ApiKey"]
    ?? config["ANTHROPIC_API_KEY"]
    ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException(
        "No Anthropic API key found — set Anthropic:ApiKey in appsettings.Development.json or ANTHROPIC_API_KEY env var.");

// Use Sonnet 4.6 (latest) — better OCR over scanned forms than 4.5.
const string model = "claude-sonnet-4-5";  // bump to "claude-sonnet-4-6" once available in your account

Console.WriteLine($"Loading PDF: {pdfPath}");
Console.WriteLine($"Slicing pages {pageStart}-{pageEnd}...");

// Slice the requested page range into a temp PDF — keeps the API payload
// well under the 32MB/100-page limit and lets us iterate cheaply.
var slicedPath = Path.Combine(Path.GetTempPath(), $"eob-spike-{Guid.NewGuid():N}.pdf");
try
{
    using (var src = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import))
    {
        if (pageStart < 1 || pageEnd > src.PageCount || pageStart > pageEnd)
        {
            Console.Error.WriteLine($"Invalid range — PDF has {src.PageCount} pages.");
            return 1;
        }
        using var dst = new PdfDocument();
        for (int i = pageStart - 1; i < pageEnd; i++)
            dst.AddPage(src.Pages[i]);
        dst.Save(slicedPath);
    }
    var slicedBytes = await File.ReadAllBytesAsync(slicedPath);
    var slicedMB = slicedBytes.Length / 1024.0 / 1024.0;
    Console.WriteLine($"Sliced PDF: {slicedMB:F1} MB ({pageEnd - pageStart + 1} pages)");
    if (slicedBytes.Length > 32 * 1024 * 1024)
    {
        Console.Error.WriteLine("Sliced PDF exceeds 32MB — narrow the page range.");
        return 1;
    }

    var base64 = Convert.ToBase64String(slicedBytes);
    Console.WriteLine($"Base64 payload: {base64.Length / 1024.0 / 1024.0:F1} MB");

    var payload = BuildPayload(model, base64, pageStart);
    var json = JsonSerializer.Serialize(payload);

    Console.WriteLine("Calling Anthropic Messages API...");
    var started = DateTime.UtcNow;
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
    req.Headers.Add("x-api-key", apiKey);
    req.Headers.Add("anthropic-version", "2023-06-01");
    req.Headers.Add("anthropic-beta", "pdfs-2024-09-25");

    using var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    var elapsed = DateTime.UtcNow - started;
    Console.WriteLine($"Response: {(int)resp.StatusCode} in {elapsed.TotalSeconds:F1}s");

    if (!resp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine("Anthropic error body:");
        Console.Error.WriteLine(body);
        return 1;
    }

    using var doc = JsonDocument.Parse(body);

    // Surface truncation loudly — silently capped output is the worst bug.
    if (doc.RootElement.TryGetProperty("stop_reason", out var stopReason))
    {
        var reason = stopReason.GetString();
        if (reason == "max_tokens")
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("⚠️  RESPONSE TRUNCATED — model hit max_tokens cap. Extraction is INCOMPLETE.");
            Console.Error.WriteLine("    Either bump max_tokens further or narrow the page range.");
            Console.Error.WriteLine();
        }
        else
        {
            Console.WriteLine($"Stop reason: {reason}");
        }
    }

    // Token usage / cost — handy for sizing prod budget.
    if (doc.RootElement.TryGetProperty("usage", out var usage))
    {
        var inTok = usage.GetProperty("input_tokens").GetInt32();
        var outTok = usage.GetProperty("output_tokens").GetInt32();
        // Sonnet 4.5/4.6: $3/MTok in, $15/MTok out
        var cost = inTok * 3.0 / 1_000_000 + outTok * 15.0 / 1_000_000;
        Console.WriteLine($"Tokens: {inTok} in + {outTok} out = ${cost:F3} for this slice");
        // Extrapolate to full-scan cost
        var pages = pageEnd - pageStart + 1;
        var perPage = cost / pages;
        Console.WriteLine($"  ~${perPage:F4}/page → ~${perPage * 100:F2} per 100-page scan");
    }

    // Pull the tool_use block — same parser shape as ClaudeScrubber.
    JsonElement? extraction = null;
    if (doc.RootElement.TryGetProperty("content", out var content))
    {
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                && block.TryGetProperty("name", out var n) && n.GetString() == "submit_eob_extraction"
                && block.TryGetProperty("input", out var input))
            {
                extraction = input;
                break;
            }
        }
    }
    if (extraction is null)
    {
        Console.Error.WriteLine("No submit_eob_extraction tool_use block in response — raw body follows:");
        Console.Error.WriteLine(body);
        return 1;
    }

    var pretty = JsonSerializer.Serialize(extraction.Value, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine();
    Console.WriteLine("===== EXTRACTED =====");
    Console.WriteLine(pretty);

    // Drop a CSV alongside the spike binary for visual diff against DS output.
    var stem = Path.GetFileNameWithoutExtension(pdfPath);
    var checksCsv = Path.Combine(Path.GetTempPath(), $"{stem}_p{pageStart}-{pageEnd}_checks.csv");
    var linesCsv = Path.Combine(Path.GetTempPath(), $"{stem}_p{pageStart}-{pageEnd}_lines.csv");
    WriteChecksCsv(extraction.Value, checksCsv);
    WriteLinesCsv(extraction.Value, linesCsv);
    Console.WriteLine();
    Console.WriteLine($"Checks CSV: {checksCsv}");
    Console.WriteLine($"Lines CSV:  {linesCsv}");

    return 0;
}
finally
{
    if (File.Exists(slicedPath)) File.Delete(slicedPath);
}

static object BuildPayload(string model, string pdfBase64, int firstPageOffset) => new
{
    model,
    // 32K — well above what a 50-page slice needs. Default 8192 truncated the
    // line_items array entirely on the first 50-page run. Sonnet 4.5 supports
    // up to 64K output, so 32K leaves headroom for 100-page slices too.
    max_tokens = 32768,
    system = new object[]
    {
        new
        {
            type = "text",
            text = SystemPrompt(),
            cache_control = new { type = "ephemeral" },
        },
    },
    tools = new[] { ToolDefinition() },
    tool_choice = new { type = "tool", name = "submit_eob_extraction" },
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
                        $"Extract every check and every service line from this scanned EOB batch.\n" +
                        $"Page numbers in your output must reflect the ORIGINAL document — this slice starts at page {firstPageOffset}, " +
                        $"so the first page you see should be reported as page {firstPageOffset}.",
                },
            },
        },
    },
};

static string SystemPrompt() => """
You are extracting structured data from a scanned multi-page batch of insurance EOB
(Explanation of Benefits) documents mailed to a chiropractic billing office. The scan
is a concatenation of many independent EOBs from different carriers. Each EOB typically
has a check stub on its first page followed by one or more pages of itemized service
lines (one row per CPT/procedure code per visit).

Extract two arrays:

1) CHECKS — one entry per check stub you find.
   - page_number: the PDF page where the check stub appears (see PAGE NUMBERING below).
   - check_number: as printed on the stub.
   - check_date: as printed, preserving the original format.
   - amount: numeric dollar amount of the check.
   - payer: the underlying insurance carrier name as printed (see PAYER vs ADMINISTRATOR).
   - administrator: third-party administrator name if separately identified (else empty).
   - Some EOBs have $0 checks (full denial) — record these too.

2) LINE_ITEMS — one entry per service line on the EOB.
   - page_number: the PDF page where the line appears (see PAGE NUMBERING below).
   - claim_number: as printed (preserve dashes, leading zeros, suffixes including
     parenthesized codes like "(0396)").
   - patient_name: EXACTLY as printed — do not reorder or normalize. If it reads
     "ZAMBRANO, AMADO" output that, if it reads "AMADO ZAMBRANO" output that.
   - bill_number: the carrier's internal bill/claim line id (often blank).
   - service_date: date of service for this line, as printed.
   - check_number: the check that paid this line, if you can determine it from
     context (same EOB / same payer). Leave blank if unsure.
   - procedure_code: CPT or HCPCS code as printed, INCLUDING modifiers (e.g.
     "97150-GP" not "97150").
   - billed: amount the provider billed for this line.
   - allowed: amount the carrier allowed.
   - paid: amount the carrier paid.
   - write_off: contractual adjustment / write-off amount.
   - reason_codes: array of objects { code, description }. EOBs often stack
     multiple reason codes per line — capture ALL of them.

PAGE NUMBERING (CRITICAL):
- Page numbers MUST refer to the PDF reader's literal page index — i.e. "page 1"
  is the very first sheet in the file, regardless of what's printed on it.
- Count EVERY page: blank pages, separator pages, back-sides of check stubs,
  cover sheets — they all increment the count.
- Do NOT use the EOB document's internal page numbering (e.g. "Page 1 of 3"
  printed inside the EOB body). Use the PDF index.
- The user message tells you the starting page offset of this slice; add it to
  the slice-relative index. For example, if the slice starts at page 11 and a
  check appears on the 3rd page of the slice, report page_number = 13.

PAYER vs ADMINISTRATOR:
- Workers-comp and PIP EOBs are often handled by a Third-Party Administrator
  (TPA) on behalf of the underlying insurer. Common TPAs: Gallagher Bassett,
  Sedgwick, Crawford & Company, ESIS, Helmsman, Broadspire, CorVel.
- When both are printed on the check stub, capture them SEPARATELY:
  - `payer` = the underlying insurance carrier (e.g. "AIU INSURANCE CO",
    "LIBERTY MUTUAL", "TRAVELERS"). This is the entity legally responsible
    for the claim.
  - `administrator` = the TPA processing the claim.
- If only one entity is named, put it in `payer` and leave `administrator` empty.

GENERAL RULES:
- Include ZERO-PAID lines (denials, exhausted-policy rows). Their reason codes
  drive triage downstream — they are NOT noise.
- Do NOT normalize patient names, claim numbers, dates, or amounts. Preserve
  the carrier's original formatting.
- Numbers: strip currency symbols and commas; emit as plain decimals.
- If a value is illegible or unmistakably absent, leave the field empty rather
  than guessing.
""";

static object ToolDefinition() => new
{
    name = "submit_eob_extraction",
    description = "Submit all checks and service lines extracted from the EOB scan.",
    input_schema = new
    {
        type = "object",
        required = new[] { "checks", "line_items" },
        properties = new
        {
            checks = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    required = new[] { "page_number", "check_number", "amount" },
                    properties = new
                    {
                        page_number = new { type = "integer" },
                        check_number = new { type = "string" },
                        check_date = new { type = "string", description = "As printed; format preserved." },
                        amount = new { type = "number" },
                        payer = new { type = "string", description = "Underlying insurance carrier — NOT the TPA." },
                        administrator = new { type = "string", description = "TPA (e.g. Gallagher Bassett) when separately named; else empty." },
                    },
                },
            },
            line_items = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    required = new[] { "page_number", "patient_name", "procedure_code", "billed", "paid", "write_off" },
                    properties = new
                    {
                        page_number = new { type = "integer" },
                        claim_number = new { type = "string" },
                        patient_name = new { type = "string", description = "Exactly as printed; no reordering." },
                        bill_number = new { type = "string" },
                        service_date = new { type = "string" },
                        check_number = new { type = "string", description = "Linked check if determinable." },
                        procedure_code = new { type = "string" },
                        billed = new { type = "number" },
                        allowed = new { type = "number" },
                        paid = new { type = "number" },
                        write_off = new { type = "number" },
                        reason_codes = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                required = new[] { "code" },
                                properties = new
                                {
                                    code = new { type = "string" },
                                    description = new { type = "string" },
                                },
                            },
                        },
                    },
                },
            },
        },
    },
};

static void WriteChecksCsv(JsonElement extraction, string path)
{
    using var sw = new StreamWriter(path);
    sw.WriteLine("page_number,check_number,check_date,amount,payer,administrator");
    if (!extraction.TryGetProperty("checks", out var checks)) return;
    foreach (var c in checks.EnumerateArray())
    {
        sw.WriteLine(string.Join(",", new[]
        {
            S(c, "page_number"),
            Csv(S(c, "check_number")),
            Csv(S(c, "check_date")),
            S(c, "amount"),
            Csv(S(c, "payer")),
            Csv(S(c, "administrator")),
        }));
    }
}

static void WriteLinesCsv(JsonElement extraction, string path)
{
    using var sw = new StreamWriter(path);
    sw.WriteLine("page_number,claim_number,patient_name,bill_number,service_date,check_number,procedure_code,billed,allowed,paid,write_off,reason_codes");
    if (!extraction.TryGetProperty("line_items", out var lines)) return;
    foreach (var l in lines.EnumerateArray())
    {
        var reasons = "";
        if (l.TryGetProperty("reason_codes", out var rc))
        {
            var parts = new List<string>();
            foreach (var r in rc.EnumerateArray())
            {
                var code = r.TryGetProperty("code", out var c1) ? c1.GetString() : "";
                var desc = r.TryGetProperty("description", out var d1) ? d1.GetString() : "";
                parts.Add(string.IsNullOrEmpty(desc) ? code ?? "" : $"{code}: {desc}");
            }
            reasons = string.Join(" | ", parts);
        }
        sw.WriteLine(string.Join(",", new[]
        {
            S(l, "page_number"),
            Csv(S(l, "claim_number")),
            Csv(S(l, "patient_name")),
            Csv(S(l, "bill_number")),
            Csv(S(l, "service_date")),
            Csv(S(l, "check_number")),
            Csv(S(l, "procedure_code")),
            S(l, "billed"),
            S(l, "allowed"),
            S(l, "paid"),
            S(l, "write_off"),
            Csv(reasons),
        }));
    }
}

static string S(JsonElement obj, string prop)
{
    if (!obj.TryGetProperty(prop, out var v)) return "";
    return v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.Null => "",
        _ => v.GetRawText(),
    };
}

static string Csv(string s)
{
    if (string.IsNullOrEmpty(s)) return "";
    if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    return s;
}
