using Lugiano.Workflow.SyncService.Services;
using Lugiano.Workflow.SyncService.Services.Fax;
using Lugiano.Workflow.SyncService.Services.Pdf;
using Microsoft.AspNetCore.Mvc;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Lugiano.Workflow.SyncService.Controllers;

// Phase 1 — read-only HCFA-1500 preview. Renders one visit's charges as a
// CMS-1500-shaped PDF. Does NOT mark the claim printed/sent in CT — that's
// Phase 2 (writeback).
[ApiController]
[Route("hcfa")]
public sealed class HcfaController : ControllerBase
{
    private readonly HcfaPreviewService _hcfa;
    private readonly FaxService _fax;

    public HcfaController(HcfaPreviewService hcfa, FaxService fax)
    {
        _hcfa = hcfa;
        _fax = fax;
    }

    // GET /hcfa/preview?patientId=X&appointmentId=Y[&mode=mail|fax][&calibrate=true][&dx=N&dy=N]
    //   mode=mail (default) — data only, for pre-printed red CMS-1500 forms
    //     mailed out. Original behavior.
    //   mode=fax — same data positioning plus a grayscale CMS-1500 (02-12) form
    //     image composited as the background, so the carrier gets a complete-
    //     looking form. Mail and fax share the same coordinate system.
    //   calibrate=true — overlay tiny grey "box id" labels above each value
    //     (e.g. "2", "5-street", "24D-CPT-0") to see what landed where.
    //   dx, dy — global offsets in PDF points (1pt = 1/72") applied to every
    //     field, for printer-specific calibration: print, measure the offset,
    //     set dx/dy to compensate (every billing product uses two numbers per
    //     printer). Persist once dialed; for now pass in the URL.
    //     Example: &dx=-4&dy=6 shifts everything 4pt left and 6pt down.
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        [FromQuery] int patientId,
        [FromQuery] int appointmentId,
        [FromQuery] string? mode = null,
        [FromQuery] bool calibrate = false,
        [FromQuery] float dx = 0,
        [FromQuery] float dy = 0,
        CancellationToken ct = default)
    {
        if (patientId <= 0 || appointmentId <= 0)
            return BadRequest(new { error = "patientId and appointmentId are required." });

        var fax = string.Equals(mode, "fax", StringComparison.OrdinalIgnoreCase);

        var data = await _hcfa.GetDataAsync(patientId, appointmentId, ct);
        if (data is null)
            return NotFound(new { error = "No service charges found for that appointment." });

        byte[] pdf;
        if (fax)
        {
            // Fax preview uses the EXACT same compose path as SendHcfaAsync —
            // BuildHcfaFaxPdfAsync produces the bytes that would go to Documo,
            // cover sheet and all. With no fax on file the cover still renders
            // "[NO FAX ON FILE]" so the biller sees the gap; Send guards on the
            // null fax before calling Documo.
            var built = await _fax.BuildHcfaFaxPdfAsync(
                patientId, appointmentId, calibrate, dx, dy, ct);
            pdf = built.Pdf;
        }
        else
        {
            pdf = _hcfa.RenderPdf(data, calibrate, dx, dy, fax);
        }

        var suffix = (fax ? "-fax" : "") + (calibrate ? "-calibrate" : "");
        var name = $"hcfa-{patientId}-{appointmentId}{suffix}.pdf";
        return File(pdf, "application/pdf", name);
    }
}
