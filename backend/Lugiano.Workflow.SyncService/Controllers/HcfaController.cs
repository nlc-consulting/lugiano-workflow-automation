using Lugiano.Workflow.SyncService.Services;
using Microsoft.AspNetCore.Mvc;

namespace Lugiano.Workflow.SyncService.Controllers;

// Phase 1 — read-only HCFA-1500 preview. Renders one visit's charges as a
// CMS-1500-shaped PDF. Does NOT mark the claim as printed/sent in CT yet —
// that's Phase 2 (writeback path).
[ApiController]
[Route("hcfa")]
public sealed class HcfaController : ControllerBase
{
    private readonly HcfaPreviewService _hcfa;

    public HcfaController(HcfaPreviewService hcfa) => _hcfa = hcfa;

    // GET /hcfa/preview?patientId=X&appointmentId=Y[&calibrate=true][&dx=N&dy=N]
    //   calibrate=true — overlay tiny grey "box id" labels above each value
    //     (e.g. "2", "5-street", "24D-CPT-0") so you can see what landed where.
    //   dx, dy — global offsets in PDF points (1pt = 1/72") applied to every
    //     field. Use to dial in printer-specific calibration: print, measure
    //     how far the data is off the boxes, set dx/dy to compensate. Standard
    //     CMS-1500 alignment pattern — every billing product (Kareo, AdvanceMD,
    //     ChiroTouch) uses two numbers per printer. Persist the working values
    //     once dialed; for now pass in the URL.
    //     Example: &dx=-4&dy=6 shifts everything 4pt left and 6pt down.
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        [FromQuery] int patientId,
        [FromQuery] int appointmentId,
        [FromQuery] bool calibrate = false,
        [FromQuery] float dx = 0,
        [FromQuery] float dy = 0,
        CancellationToken ct = default)
    {
        if (patientId <= 0 || appointmentId <= 0)
            return BadRequest(new { error = "patientId and appointmentId are required." });

        var data = await _hcfa.GetDataAsync(patientId, appointmentId, ct);
        if (data is null)
            return NotFound(new { error = "No service charges found for that appointment." });

        var pdf = _hcfa.RenderPdf(data, calibrate, dx, dy);
        var name = $"hcfa-{patientId}-{appointmentId}{(calibrate ? "-calibrate" : "")}.pdf";
        return File(pdf, "application/pdf", name);
    }
}
