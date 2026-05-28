using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Services;

public sealed class WorkflowCaseService
{
    private readonly IDbContextFactory<WorkflowDbContext> _dbFactory;

    public WorkflowCaseService(IDbContextFactory<WorkflowDbContext> dbFactory) => _dbFactory = dbFactory;

    // Creates the case for a patient if absent, otherwise advances its state.
    // Optionally stamps a flow's "first recorded" date (only set once, preserving first-seen).
    // Returns the WorkflowCase.Id. One case per patient (unique index on PatientId).
    public async Task<int> CreateOrUpdateCaseAsync(
        int patientId, string? firstName, string? lastName, string newState,
        DateTime? insuranceAddedAt = null, DateTime? doctorNotesReceivedAt = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        var existing = await db.WorkflowCases.FirstOrDefaultAsync(x => x.PatientId == patientId);
        if (existing is null)
        {
            var created = new WorkflowCase
            {
                PatientId = patientId,
                FirstName = firstName,
                LastName = lastName,
                CurrentState = newState,
                CreatedAt = now,
                UpdatedAt = now,
                InsuranceAddedAt = insuranceAddedAt,
                DoctorNotesReceivedAt = doctorNotesReceivedAt,
            };
            db.WorkflowCases.Add(created);
            await db.SaveChangesAsync();
            return created.Id;
        }

        existing.CurrentState = newState;
        existing.FirstName = firstName ?? existing.FirstName;
        existing.LastName = lastName ?? existing.LastName;
        // Stamp each flow only the first time we see it.
        existing.InsuranceAddedAt ??= insuranceAddedAt;
        existing.DoctorNotesReceivedAt ??= doctorNotesReceivedAt;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync();
        return existing.Id;
    }

    // Records/updates the PIP verification date (editable; defaults to now on first verify).
    // Returns the WorkflowCase.Id, creating a minimal case if none exists.
    public async Task<int> SetPipVerifiedAsync(int patientId, DateTime verifiedDate)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        var existing = await db.WorkflowCases.FirstOrDefaultAsync(x => x.PatientId == patientId);
        if (existing is null)
        {
            existing = new WorkflowCase
            {
                PatientId = patientId,
                CurrentState = WorkflowStates.AwaitingDoctorNotes,
                CreatedAt = now,
                UpdatedAt = now,
                PipVerifiedAt = verifiedDate,
            };
            db.WorkflowCases.Add(existing);
            await db.SaveChangesAsync();
            return existing.Id;
        }

        existing.PipVerifiedAt = verifiedDate;
        if (existing.CurrentState == WorkflowStates.AwaitingPipVerification)
            existing.CurrentState = WorkflowStates.AwaitingDoctorNotes;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync();
        return existing.Id;
    }

    public async Task<bool> WorkflowEventExistsAsync(string sourceTable, long sourceRecordId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkflowEvents
            .AnyAsync(x => x.SourceTable == sourceTable && x.SourceRecordId == sourceRecordId);
    }

    public async Task InsertWorkflowEventAsync(WorkflowEvent ev)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        // Guard against a race: the unique index is the real enforcement.
        bool exists = await db.WorkflowEvents
            .AnyAsync(x => x.SourceTable == ev.SourceTable && x.SourceRecordId == ev.SourceRecordId);
        if (exists)
            return;

        ev.CreatedAt = DateTime.UtcNow;
        db.WorkflowEvents.Add(ev);
        await db.SaveChangesAsync();
    }

    public async Task<bool> DoctorNoteExistsAsync(int chartNoteId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.DoctorNotes.AnyAsync(x => x.ChartNoteId == chartNoteId);
    }

    public async Task InsertDoctorNoteAsync(DoctorNote note)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        bool exists = await db.DoctorNotes.AnyAsync(x => x.ChartNoteId == note.ChartNoteId);
        if (exists)
            return;

        note.CreatedAt = DateTime.UtcNow;
        db.DoctorNotes.Add(note);
        await db.SaveChangesAsync();
    }
}
