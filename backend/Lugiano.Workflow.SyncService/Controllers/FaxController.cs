using System.Globalization;
using Lugiano.Workflow.SyncService.Services.Fax;
using Microsoft.AspNetCore.Mvc;

namespace Lugiano.Workflow.SyncService.Controllers;

// Manual fax-send endpoints. Both render the same PDF as the preview
// endpoints, then hand it to Documo. Driven by the portal's "Fax now"
// buttons; auto-fax-on-state-transition is a future epic — until then a
// human always pulls the trigger.
[ApiController]
[Route("fax")]
public sealed class FaxController : ControllerBase
{
    private readonly FaxService _fax;

    public FaxController(FaxService fax) => _fax = fax;

    // POST /fax/hcfa?patientId=X&appointmentId=Y
    [HttpPost("hcfa")]
    public async Task<IActionResult> Hcfa(
        [FromQuery] int patientId,
        [FromQuery] int appointmentId,
        CancellationToken ct)
    {
        if (patientId <= 0 || appointmentId <= 0)
            return BadRequest(new { error = "patientId and appointmentId are required." });
        if (!_fax.IsConfigured)
            return BadRequest(new { error = "Documo is not configured (set Documo:ApiKey)." });

        try
        {
            var result = await _fax.SendHcfaAsync(patientId, appointmentId, ct);
            return Ok(new { sent = 1, results = new[] { result } });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // POST /fax/tracer?patientId=X&billDates=YYYY-MM-DD,...&includeHcfa=true
    [HttpPost("tracer")]
    public async Task<IActionResult> Tracer(
        [FromQuery] int patientId,
        [FromQuery] string? billDates = null,
        [FromQuery] bool includeHcfa = true,
        CancellationToken ct = default)
    {
        if (patientId <= 0)
            return BadRequest(new { error = "patientId is required." });
        if (!_fax.IsConfigured)
            return BadRequest(new { error = "Documo is not configured (set Documo:ApiKey)." });

        var dates = (billDates ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : (DateTime?)null)
            .Where(d => d.HasValue).Select(d => d!.Value).ToList();
        if (dates.Count == 0)
            return BadRequest(new { error = "billDates (comma-separated YYYY-MM-DD) is required." });

        try
        {
            var results = await _fax.SendTracerAsync(patientId, dates, includeHcfa, ct);
            return Ok(new { sent = results.Count, results });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
