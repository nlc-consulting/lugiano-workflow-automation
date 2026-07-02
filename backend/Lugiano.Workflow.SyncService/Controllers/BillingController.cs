using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Services;
using Microsoft.AspNetCore.Mvc;

namespace Lugiano.Workflow.SyncService.Controllers;

// Production billing actions. Marks every unbilled service charge on a visit
// as billed — manual stand-in for the post-fax-delivery hook. NOT a test
// affordance: same code path fires automatically once the fax webhook lands.
[ApiController]
[Route("billing")]
public sealed class BillingController : ControllerBase
{
    private readonly BillChargesService _billing;
    private readonly IPatientDetailQueries _detail;

    public BillingController(BillChargesService billing, IPatientDetailQueries detail)
    {
        _billing = billing;
        _detail = detail;
    }

    // GET /billing/visit/preview?patientId=X&appointmentId=Y
    // Read-only: returns the unbilled service charges + total that POST /billing/visit
    // would mark billed. Powers the "Bill now" confirmation dialog.
    [HttpGet("visit/preview")]
    public async Task<IActionResult> PreviewVisit([FromQuery] int patientId, [FromQuery] int appointmentId)
    {
        if (patientId <= 0 || appointmentId <= 0)
            return BadRequest(new { error = "patientId and appointmentId are required." });
        if (!_detail.IsConfigured)
            return Ok(new { count = 0, total = 0m, charges = Array.Empty<object>() });

        var charges = await _detail.GetUnbilledChargesForVisitAsync(patientId, appointmentId);
        return Ok(new
        {
            count = charges.Count,
            total = charges.Sum(c => c.Amount),
            charges = charges.Select(c => new
            {
                id = c.Id,
                code = c.Code,
                description = c.Description,
                amount = c.Amount,
            }),
        });
    }

    // POST /billing/visit?patientId=X&appointmentId=Y
    // INSERTs BilledCharges rows for every unbilled service Transaction on
    // the appointment. Mirrors CT's paper-claim flow exactly (no ClaimLines
    // linkage — matches 84% of CT prod rows). Atomic; rollback on error.
    [HttpPost("visit")]
    public async Task<IActionResult> BillVisit(
        [FromQuery] int patientId,
        [FromQuery] int appointmentId,
        CancellationToken ct)
    {
        if (patientId <= 0 || appointmentId <= 0)
            return BadRequest(new { error = "patientId and appointmentId are required." });
        if (!_billing.IsConfigured)
            return BadRequest(new { error = "PSChiro write account is not configured (set ChiroTouchWrite connection string)." });

        try
        {
            var result = await _billing.BillVisitAsync(patientId, appointmentId, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
