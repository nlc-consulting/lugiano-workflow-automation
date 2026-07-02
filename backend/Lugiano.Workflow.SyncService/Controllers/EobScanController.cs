using System.Text.Json;
using ClosedXML.Excel;
using Lugiano.Workflow.SyncService.Services.EobScanning;
using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Controllers;

// EOB scanner endpoints — replace the DS mail-scan vendor. Accepts a scanned
// PDF, extracts checks + line items via Claude Vision, persists them to our
// DB so they're immediately usable in the EOB preview flow (no manual xlsx
// import). xlsx export is available as an audit artifact only.
[ApiController]
[Route("eob/scan")]
public sealed class EobScanController : ControllerBase
{
    private readonly EobScanService _scanner;
    private readonly IDbContextFactory<WorkflowDbContext> _dbFactory;

    public EobScanController(EobScanService scanner, IDbContextFactory<WorkflowDbContext> dbFactory)
    {
        _scanner = scanner;
        _dbFactory = dbFactory;
    }

    // POST /eob/scan — multipart/form-data, field name "file" (.pdf).
    // Returns immediately with the scanId; client polls GET /eob/scan/{id}
    // for status. Background processing runs through ChunkSize/Overlap +
    // Claude per-chunk + dedupe + persist.
    [HttpPost]
    [RequestSizeLimit(300 * 1024 * 1024)]  // 300MB — biggest LM scan we've seen was 250MB
    [RequestFormLimits(MultipartBodyLengthLimit = 300 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Upload a scanned EOB PDF as field 'file'." });
        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "File must be .pdf." });

        await using var stream = file.OpenReadStream();
        var scan = await _scanner.StartScanAsync(stream, file.FileName, ct);
        return Accepted(new
        {
            id = scan.Id,
            status = scan.Status,
            sourceFilename = scan.SourceFilename,
            pageCount = scan.PageCount,
        });
    }

    // GET /eob/scan/{id} — current status + extracted rows. Poll while
    // status ∈ { queued, running }; when completed, the checks + lineItems
    // arrays are populated.
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var scan = await db.EobScans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (scan is null) return NotFound();

        var checks = await db.EobScanChecks
            .AsNoTracking()
            .Where(c => c.EobScanId == id)
            .OrderBy(c => c.PageNumber)
            .ToListAsync(ct);
        var lines = await db.EobScanLineItems
            .AsNoTracking()
            .Where(l => l.EobScanId == id)
            .OrderBy(l => l.PageNumber).ThenBy(l => l.ProcedureCode)
            .ToListAsync(ct);

        return Ok(new
        {
            id = scan.Id,
            status = scan.Status,
            errorMessage = scan.ErrorMessage,
            sourceFilename = scan.SourceFilename,
            scanDate = scan.ScanDate,
            pageCount = scan.PageCount,
            fileSizeBytes = scan.FileSizeBytes,
            uploadedAt = scan.UploadedAt,
            processingStartedAt = scan.ProcessingStartedAt,
            completedAt = scan.CompletedAt,
            chunkSize = scan.ChunkSize,
            chunkOverlap = scan.ChunkOverlap,
            modelUsed = scan.ModelUsed,
            inputTokens = scan.InputTokens,
            outputTokens = scan.OutputTokens,
            estimatedCostUsd = scan.EstimatedCostUsd,
            checks = checks.Select(c => new
            {
                c.Id,
                c.PageNumber,
                c.CheckNumber,
                c.CheckDate,
                c.Amount,
                c.Payer,
                c.Administrator,
                c.PairedCheckId,
            }),
            lineItems = lines.Select(l => new
            {
                l.Id,
                l.PageNumber,
                l.ClaimNumber,
                l.PatientNameRaw,
                l.BillNumber,
                l.ServiceDate,
                l.CheckNumber,
                l.ProcedureCode,
                l.BilledAmount,
                l.AllowedAmount,
                l.PaidAmount,
                l.WriteOffAmount,
                reasonCodes = DeserializeReasons(l.ReasonCodesJson),
            }),
        });
    }

    // GET /eob/scan — list recent scans for the dashboard.
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (take < 1) take = 1;
        if (take > 200) take = 200;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var scans = await db.EobScans
            .AsNoTracking()
            .OrderByDescending(s => s.UploadedAt)
            .Take(take)
            .Select(s => new
            {
                s.Id,
                s.SourceFilename,
                s.ScanDate,
                s.PageCount,
                s.Status,
                s.UploadedAt,
                s.CompletedAt,
                s.EstimatedCostUsd,
                checkCount = db.EobScanChecks.Count(c => c.EobScanId == s.Id),
                lineCount = db.EobScanLineItems.Count(l => l.EobScanId == s.Id),
            })
            .ToListAsync(ct);
        return Ok(scans);
    }

    // GET /eob/scan/{id}/export.xlsx — audit artifact mirroring DS's two
    // worksheets (Checks + EOB Line Items) so staff can compare/archive in
    // the format they're used to. Pure read — DB rows are the source of truth.
    [HttpGet("{id:int}/export.xlsx")]
    public async Task<IActionResult> Export(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var scan = await db.EobScans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (scan is null) return NotFound();

        var checks = await db.EobScanChecks
            .AsNoTracking()
            .Where(c => c.EobScanId == id)
            .OrderBy(c => c.PageNumber)
            .ToListAsync(ct);
        var lines = await db.EobScanLineItems
            .AsNoTracking()
            .Where(l => l.EobScanId == id)
            .OrderBy(l => l.PageNumber).ThenBy(l => l.ProcedureCode)
            .ToListAsync(ct);

        using var wb = new XLWorkbook();
        var checksSheet = wb.Worksheets.Add("Checks");
        checksSheet.Cell(1, 1).Value = "Page Number";
        checksSheet.Cell(1, 2).Value = "Check Number";
        checksSheet.Cell(1, 3).Value = "Check Date";
        checksSheet.Cell(1, 4).Value = "Check Amount";
        checksSheet.Cell(1, 5).Value = "Payer";
        checksSheet.Cell(1, 6).Value = "Administrator";
        for (int i = 0; i < checks.Count; i++)
        {
            var c = checks[i];
            var row = i + 2;
            checksSheet.Cell(row, 1).Value = c.PageNumber;
            checksSheet.Cell(row, 2).Value = c.CheckNumber;
            checksSheet.Cell(row, 3).Value = c.CheckDate ?? "";
            checksSheet.Cell(row, 4).Value = (double)c.Amount;
            checksSheet.Cell(row, 5).Value = c.Payer ?? "";
            checksSheet.Cell(row, 6).Value = c.Administrator ?? "";
        }
        checksSheet.Columns().AdjustToContents();

        var linesSheet = wb.Worksheets.Add("EOB Line Items");
        var headers = new[]
        {
            "Page Number", "Claim Number", "Patient Name", "Bill Number",
            "Date of Service", "Associated Check Number", "Procedural Code",
            "Billed Amount", "Allowed Charge", "Paid Amount", "Write Off Amount",
            "Reason Code", "Reason Description",
        };
        for (int i = 0; i < headers.Length; i++)
            linesSheet.Cell(1, i + 1).Value = headers[i];
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var row = i + 2;
            var reasons = DeserializeReasons(l.ReasonCodesJson);
            linesSheet.Cell(row, 1).Value = l.PageNumber;
            linesSheet.Cell(row, 2).Value = l.ClaimNumber ?? "";
            linesSheet.Cell(row, 3).Value = l.PatientNameRaw ?? "";
            linesSheet.Cell(row, 4).Value = l.BillNumber ?? "";
            linesSheet.Cell(row, 5).Value = l.ServiceDate ?? "";
            linesSheet.Cell(row, 6).Value = l.CheckNumber ?? "";
            linesSheet.Cell(row, 7).Value = l.ProcedureCode;
            linesSheet.Cell(row, 8).Value = (double)l.BilledAmount;
            linesSheet.Cell(row, 9).Value = (double)l.AllowedAmount;
            linesSheet.Cell(row, 10).Value = (double)l.PaidAmount;
            linesSheet.Cell(row, 11).Value = (double)l.WriteOffAmount;
            linesSheet.Cell(row, 12).Value = string.Join(" / ", reasons.Select(r => r.code));
            linesSheet.Cell(row, 13).Value = string.Join(" | ", reasons.Select(r => r.description ?? ""));
        }
        linesSheet.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var bytes = ms.ToArray();
        var safeName = SanitizeForFilename(Path.GetFileNameWithoutExtension(scan.SourceFilename));
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"EOB_Scan_{scan.Id}_{safeName}.xlsx");
    }

    private static List<ReasonDto> DeserializeReasons(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<ReasonDto>();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var code = r.TryGetProperty("code", out var c) ? c.GetString() : null;
                var desc = r.TryGetProperty("description", out var d) ? d.GetString() : null;
                if (!string.IsNullOrWhiteSpace(code))
                    list.Add(new ReasonDto(code, desc));
            }
            return list;
        }
        catch
        {
            return new();
        }
    }

    private static string SanitizeForFilename(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return clean.Length > 80 ? clean[..80] : clean;
    }

    public sealed record ReasonDto(string code, string? description);
}
