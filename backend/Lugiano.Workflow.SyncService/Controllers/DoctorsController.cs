using Lugiano.Workflow.SyncService.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Controllers;

[ApiController]
[Route("doctors")]
public sealed class DoctorsController : ControllerBase
{
    private readonly IDbContextFactory<WorkflowDbContext> _factory;

    public DoctorsController(IDbContextFactory<WorkflowDbContext> factory) => _factory = factory;

    // PATCH /doctors/{id} — update the doctor's saved notification email.
    // Used by the kickback modal's "save this email as default" toggle, and
    // by a future doctors admin page if we add one.
    [HttpPatch("{id:int}")]
    public async Task<IActionResult> UpdateDoctor(int id, [FromBody] DoctorPatchBody body)
    {
        if (body.Email is not null && body.Email.Length > 254)
            return BadRequest(new { error = "Email is too long (max 254 chars)." });

        await using var db = await _factory.CreateDbContextAsync();
        var doctor = await db.Doctors.FirstOrDefaultAsync(d => d.Id == id);
        if (doctor is null) return NotFound();

        var changed = false;
        if (body.Email is not null)
        {
            doctor.Email = string.IsNullOrWhiteSpace(body.Email) ? null : body.Email.Trim();
            changed = true;
        }
        if (changed)
        {
            doctor.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        return Ok(new
        {
            id = doctor.Id,
            chiroTouchDoctorId = doctor.ChiroTouchDoctorId,
            fullName = doctor.FullName,
            email = doctor.Email,
        });
    }
}

public sealed class DoctorPatchBody
{
    public string? Email { get; set; }
}
