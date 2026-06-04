using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services;

// Populates the portal's Doctor table from ChiroTouch. Full import runs at
// startup so reviewers see every doctor immediately (no "we don't have this
// doctor yet" race when the first kickback fires). EnsureDoctorAsync is the
// per-note safety net for doctors added to ChiroTouch between restarts.
//
// Email seeding is one-way: PSChiro -> us. Once a portal user sets an email,
// later imports leave it alone (reviewer edits win).
public sealed class DoctorSyncService
{
    private readonly IDoctorReadQueries _reads;
    private readonly IDbContextFactory<WorkflowDbContext> _dbFactory;
    private readonly ILogger<DoctorSyncService> _logger;

    public DoctorSyncService(
        IDoctorReadQueries reads,
        IDbContextFactory<WorkflowDbContext> dbFactory,
        ILogger<DoctorSyncService> logger)
    {
        _reads = reads;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    // Imports every PSChiro doctor. Inserts new rows; for existing rows,
    // refreshes ChiroTouch-sourced fields and preserves any portal-set email.
    public async Task<int> ImportAllAsync(CancellationToken ct = default)
    {
        if (!_reads.IsConfigured)
        {
            _logger.LogInformation("Doctor sync skipped: ChiroTouch is not configured.");
            return 0;
        }

        var rows = await _reads.GetAllDoctorsAsync();
        if (rows.Count == 0) return 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Doctors.ToDictionaryAsync(d => d.ChiroTouchDoctorId, ct);
        var now = DateTime.UtcNow;
        int added = 0, updated = 0;

        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (existing.TryGetValue(r.Id, out var d))
            {
                d.FullName = r.FullName;
                d.Credentials = FormatCredentials(r.Credentials1, r.Credentials2);
                d.Npi = NullIfEmpty(r.Npi);
                d.IsActive = !r.Inactive;
                // Only seed the email if we don't already have one — reviewer
                // edits in the portal must not get trampled by a later import.
                d.Email ??= NullIfEmpty(r.EmailAddress);
                d.UpdatedAt = now;
                updated++;
            }
            else
            {
                db.Doctors.Add(new Doctor
                {
                    ChiroTouchDoctorId = r.Id,
                    FullName = r.FullName,
                    Credentials = FormatCredentials(r.Credentials1, r.Credentials2),
                    Npi = NullIfEmpty(r.Npi),
                    Email = NullIfEmpty(r.EmailAddress),
                    IsActive = !r.Inactive,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                added++;
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Doctor sync: {Added} added, {Updated} updated, {Total} total in our DB.",
            added, updated, existing.Count + added);
        return added + updated;
    }

    private static string? FormatCredentials(string? c1, string? c2)
    {
        var parts = new[] { c1, c2 }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim());
        var joined = string.Join(", ", parts);
        return string.IsNullOrEmpty(joined) ? null : joined;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
}
