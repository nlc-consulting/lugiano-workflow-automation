using System.Globalization;
using Lugiano.Workflow.SyncService.Services;
using Microsoft.AspNetCore.Mvc;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Lugiano.Workflow.SyncService.Controllers;

// Insurance Payment Tracer — AR follow-up report endpoint. Mirrors CT's
// Insurance Payment Tracer (Claim History → Charges Billed But Not Paid →
// Tracer). Two endpoints:
//   GET /tracer/batches?patientId=X      → JSON list of bill-date batches
//   GET /tracer/preview?patientId=X
//        &billDates=YYYY-MM-DD,YYYY-...  → multi-batch PDF
[ApiController]
[Route("tracer")]
public sealed class TracerController : ControllerBase
{
    private readonly TracerPreviewService _tracer;
    private readonly HcfaPreviewService _hcfa;

    public TracerController(TracerPreviewService tracer, HcfaPreviewService hcfa)
    {
        _tracer = tracer;
        _hcfa = hcfa;
    }

    [HttpGet("batches")]
    public async Task<IActionResult> Batches([FromQuery] int patientId, CancellationToken ct)
    {
        if (patientId <= 0) return BadRequest(new { error = "patientId is required." });
        var rows = await _tracer.ListBatchesAsync(patientId, ct);
        return Ok(rows.Select(r => new
        {
            id = r.BilledDate.ToString("yyyy-MM-dd"), // stable row key for react-admin
            patientId,
            billedDate = r.BilledDate.ToString("yyyy-MM-dd"),
            insPolId = r.InsPolId,
            payerName = r.PayerName,
            lineCount = r.LineCount,
            totalAmount = r.TotalAmount,
        }));
    }

    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        [FromQuery] int patientId,
        [FromQuery] string? billDates = null,
        // When true (default), each tracer batch is followed by the HCFA(s)
        // for the appointment(s) it covers — rendered in fax mode (form
        // overlay on) so the whole packet faxes as a complete, self-contained
        // claim follow-up. Set false to send the tracer alone.
        [FromQuery] bool includeHcfa = true,
        CancellationToken ct = default)
    {
        if (patientId <= 0) return BadRequest(new { error = "patientId is required." });

        // Parse comma-separated dates: ?billDates=2026-05-26,2026-04-15
        var dates = (billDates ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : (DateTime?)null)
            .Where(d => d.HasValue).Select(d => d!.Value).ToList();
        if (dates.Count == 0)
            return BadRequest(new { error = "billDates (comma-separated YYYY-MM-DD) is required." });

        // Pre-fetch all batches + their HCFA data UP FRONT — Document.Create's
        // builder callback is synchronous, so all DB I/O has to complete
        // before we start composing the PDF.
        var bundles = new List<(TracerData Batch, List<HcfaData> Hcfas)>();
        foreach (var d in dates)
        {
            var batch = await _tracer.GetBatchAsync(patientId, d, ct);
            if (batch is null) continue;

            var hcfas = new List<HcfaData>();
            if (includeHcfa)
            {
                var apptIds = await _tracer.GetAppointmentIdsForBatchAsync(patientId, d, ct);
                foreach (var apptId in apptIds)
                {
                    var hd = await _hcfa.GetDataAsync(patientId, apptId, ct);
                    if (hd is not null) hcfas.Add(hd);
                }
            }
            bundles.Add((batch, hcfas));
        }
        if (bundles.Count == 0)
            return NotFound(new { error = "No unpaid billed charges found for the given dates." });

        QuestPDF.Settings.License = LicenseType.Community;
        var pdf = Document.Create(doc =>
        {
            foreach (var (batch, hcfas) in bundles)
            {
                _tracer.AddBatchPageToDocument(doc, batch);
                foreach (var hd in hcfas)
                    _hcfa.AddPagesToDocument(doc, hd, fax: true);
            }
        }).GeneratePdf();

        var suffix = includeHcfa ? "-with-hcfa" : "";
        var name = $"tracer-{patientId}-{DateTime.Today:yyyyMMdd}{suffix}.pdf";
        return File(pdf, "application/pdf", name);
    }
}
