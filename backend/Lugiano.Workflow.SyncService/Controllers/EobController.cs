using Lugiano.Workflow.SyncService.Services;
using Microsoft.AspNetCore.Mvc;

namespace Lugiano.Workflow.SyncService.Controllers;

// Preview-only EOB posting. Reads an uploaded EOB Line Items workbook, matches
// each line against PSChiro Transactions, returns proposed updates. NO write-
// back. The Apply path is task-tracked separately and needs broader lugiano_rw
// permissions (UPDATE on Transactions columns).
[ApiController]
[Route("eob")]
public sealed class EobController : ControllerBase
{
    private readonly EobPreviewService _preview;
    private readonly EobPostingService _posting;

    public EobController(EobPreviewService preview, EobPostingService posting)
    {
        _preview = preview;
        _posting = posting;
    }

    // POST /eob/preview — multipart/form-data, field name "file" (.xlsx).
    [HttpPost("preview")]
    [RequestSizeLimit(20 * 1024 * 1024)] // 20MB cap — EOB workbooks are tiny in practice
    public async Task<IActionResult> Preview(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Upload an EOB Line Items .xlsx as field 'file'." });

        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "File must be .xlsx." });

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _preview.PreviewAsync(stream, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // GET /eob/lookup-patient?accountNo=N — manual override path. Operator types
    // a known ChiroTouch AccountNo; returns the matching patient (name + DOB) for
    // confirmation, or 404. Frontend confirms before invoking resolve-line.
    [HttpGet("lookup-patient")]
    public async Task<IActionResult> LookupPatient(
        [FromQuery] int accountNo,
        CancellationToken ct)
    {
        if (accountNo <= 0)
            return BadRequest(new { error = "accountNo is required and must be > 0." });
        var hit = await _preview.LookupPatientByAccountNoAsync(accountNo, ct);
        if (hit is null)
            return NotFound(new { error = $"No PSChiro patient found with AccountNo = {accountNo}." });
        return Ok(hit);
    }

    // POST /eob/resolve-line — re-runs the per-line match against a chosen
    // patient (portal's fuzzy-suggestion chip on unmatched lines). Returns the
    // new bucket (matched / ambiguous / still unmatched). No writes — pure read.
    [HttpPost("resolve-line")]
    public async Task<IActionResult> ResolveLine(
        [FromBody] ResolveLineRequest req,
        CancellationToken ct)
    {
        if (req?.Line is null || req.PatientId <= 0)
            return BadRequest(new { error = "Body must include `line` and `patientId`." });
        var result = await _preview.ResolveLineWithPatientAsync(req.Line, req.PatientId, ct);
        return Ok(result);
    }

    public sealed record ResolveLineRequest(EobLine Line, int PatientId);

    // POST /eob/apply — same xlsx as /preview, but writes to PSChiro. Re-runs
    // the match server-side (never trusts a client-supplied update list), applies
    // unambiguous matches in one transaction, returns a per-line report
    // (applied/skipped/ambiguous/unmatched).
    [HttpPost("apply")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> Apply(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Upload an EOB Line Items .xlsx as field 'file'." });
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "File must be .xlsx." });
        if (!_posting.IsConfigured)
            return BadRequest(new { error = "PSChiro write account is not configured (set ChiroTouchWrite connection string)." });

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _posting.ApplyAsync(stream, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
