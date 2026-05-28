using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Services;

public sealed class SyncStateService
{
    private readonly IDbContextFactory<WorkflowDbContext> _dbFactory;

    public SyncStateService(IDbContextFactory<WorkflowDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<long> GetLastSeenAsync(string syncKey)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.SyncStates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SyncKey == syncKey);
        return row?.LastSeenId ?? 0;
    }

    public async Task SetLastSeenAsync(string syncKey, long lastSeenId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.SyncStates.FirstOrDefaultAsync(x => x.SyncKey == syncKey);
        if (row is null)
        {
            db.SyncStates.Add(new SyncState
            {
                SyncKey = syncKey,
                LastSeenId = lastSeenId,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            row.LastSeenId = lastSeenId;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<SyncState>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.SyncStates.AsNoTracking()
            .OrderBy(x => x.SyncKey)
            .ToListAsync();
    }
}
