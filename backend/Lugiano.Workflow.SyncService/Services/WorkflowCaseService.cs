using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Services;

public sealed class WorkflowCaseService
{
    private readonly IDbContextFactory<WorkflowDbContext> _dbFactory;

    public WorkflowCaseService(IDbContextFactory<WorkflowDbContext> dbFactory) => _dbFactory = dbFactory;

    // Creates or RECONCILES a patient's case from the full ChiroTouch truth.
    // Every trigger passes the reconciled status (insurance + notes + real dates),
    // so a note trigger also captures insurance present since intake and vice versa.
    // Stamps reflect current reality (set, not first-seen); state is always derived.
    // PIP is portal-driven and preserved across reconciliation.
    // Returns the WorkflowCase.Id. One case per patient (unique index on PatientId).
    public async Task<int> CreateOrUpdateCaseAsync(
        int patientId, string? firstName, string? lastName,
        bool hasInsurance, DateTime? insuranceAddedAt,
        bool hasNotes, DateTime? doctorNotesReceivedAt)
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
                CurrentState = new BillingReadiness(
                    Insurance: hasInsurance, Pip: false, Notes: hasNotes).DerivedState,
                CreatedAt = now,
                UpdatedAt = now,
                InsuranceAddedAt = hasInsurance ? insuranceAddedAt : null,
                DoctorNotesReceivedAt = hasNotes ? doctorNotesReceivedAt : null,
            };
            db.WorkflowCases.Add(created);
            await db.SaveChangesAsync();
            return created.Id;
        }

        existing.FirstName = firstName ?? existing.FirstName;
        existing.LastName = lastName ?? existing.LastName;
        // Reflect current ChiroTouch truth (don't preserve a stale "missing" flag).
        existing.InsuranceAddedAt = hasInsurance ? insuranceAddedAt : null;
        existing.DoctorNotesReceivedAt = hasNotes ? doctorNotesReceivedAt : null;
        existing.CurrentState = new BillingReadiness(
            Insurance: hasInsurance,
            Pip: existing.PipVerifiedAt != null,
            Notes: hasNotes).DerivedState;
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
                // No reconciled case yet: insurance/notes unknown until the next poll.
                CurrentState = new BillingReadiness(
                    Insurance: false, Pip: true, Notes: false).DerivedState,
                CreatedAt = now,
                UpdatedAt = now,
                PipVerifiedAt = verifiedDate,
            };
            db.WorkflowCases.Add(existing);
            await db.SaveChangesAsync();
            return existing.Id;
        }

        existing.PipVerifiedAt = verifiedDate;
        existing.CurrentState = new BillingReadiness(
            Insurance: existing.InsuranceAddedAt != null,
            Pip: true,
            Notes: existing.DoctorNotesReceivedAt != null).DerivedState;
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
