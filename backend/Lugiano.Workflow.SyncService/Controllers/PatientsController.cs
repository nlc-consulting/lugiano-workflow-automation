using System.Text.Json;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Microsoft.AspNetCore.Mvc;

namespace Lugiano.Workflow.SyncService.Controllers;

[ApiController]
[Route("patients")]
public sealed class PatientsController : ControllerBase
{
    private readonly IPatientDetailQueries _detail;

    public PatientsController(IPatientDetailQueries detail) => _detail = detail;

    // GET /patients — paginated lookup against live ChiroTouch.
    // ra-data-simple-rest: ?range=[start,end] + ?filter={"q":"..."} -> Content-Range header.
    // Clicks open /cases/{id}/show, which works for any PatientID (not just those with a WorkflowCase).
    [HttpGet]
    public async Task<IActionResult> GetPatients(
        [FromQuery] string? range,
        [FromQuery] string? filter)
    {
        if (!_detail.IsConfigured) return Ok(Array.Empty<object>());

        var (skip, take) = ParseRange(range);
        if (take <= 0 || take > 200) take = 25;

        var (q, includeInactive) = ParseFilter(filter);

        var (rows, total) = await _detail.SearchPatientsAsync(q, includeInactive, skip, take);

        var dto = rows.Select(r => new
        {
            id = r.Id,
            patientId = r.Id,
            firstName = r.FirstName,
            lastName = r.LastName,
            accountNo = r.AccountNo,
            sex = r.Sex,
            city = r.City,
            state = r.State,
            primaryDoctor = r.PrimaryDoctor,
        }).ToList();

        var last = total == 0 ? 0 : Math.Min(skip + dto.Count - 1, total - 1);
        Response.Headers["Content-Range"] = $"patients {skip}-{last}/{total}";
        return Ok(dto);
    }

    private static (int skip, int take) ParseRange(string? range)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(range))
            {
                var a = JsonSerializer.Deserialize<int[]>(range);
                if (a is { Length: 2 }) return (a[0], a[1] - a[0] + 1);
            }
        }
        catch { /* fall through */ }
        return (0, 25);
    }

    private static (string? q, bool includeInactive) ParseFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return (null, false);
        try
        {
            using var doc = JsonDocument.Parse(filter);
            var root = doc.RootElement;
            string? q = root.TryGetProperty("q", out var qEl) && qEl.ValueKind == JsonValueKind.String
                ? qEl.GetString()
                : null;
            bool includeInactive =
                root.TryGetProperty("includeInactive", out var iEl) &&
                iEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                iEl.GetBoolean();
            return (q, includeInactive);
        }
        catch
        {
            return (null, false);
        }
    }
}
