using Lugiano.Workflow.SyncService.Services;
using Microsoft.AspNetCore.Mvc;

namespace Lugiano.Workflow.SyncService.Controllers;

// Production billing actions. Today: a single endpoint that marks every
// unbilled service charge on a visit as billed (the manual stand-in for the
// post-fax-delivery hook that will eventually auto-fire). This is NOT a
// test affordance — same code path will run automatically once the fax
// confirmation webhook lands.
[ApiController]
[Route("billing")]
public sealed class BillingController : ControllerBase
{
    private readonly BillChargesService _billing;

    public BillingController(BillChargesService billing) => _billing = billing;

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
