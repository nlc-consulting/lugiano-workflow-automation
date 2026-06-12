using Lugiano.Workflow.SyncService.Services;
using Microsoft.AspNetCore.Mvc;

namespace Lugiano.Workflow.SyncService.Controllers;

// Preview-only EOB posting. Reads an uploaded EOB Line Items workbook,
// matches each line against PSChiro Transactions, returns proposed updates.
// NO write-back. The Apply path is task-tracked separately and needs
// broader lugiano_rw permissions (UPDATE on Transactions columns).
[ApiController]
[Route("eob")]
public sealed class EobController : ControllerBase
{
    private readonly EobPreviewService _preview;

    public EobController(EobPreviewService preview) => _preview = preview;

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
}
