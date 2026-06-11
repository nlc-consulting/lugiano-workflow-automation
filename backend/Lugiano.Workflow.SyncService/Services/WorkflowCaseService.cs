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

    // Resolve the DoctorNote.Id for a given PSChiro ChartNote.Id. Used by the
    // sync auto-scrub trigger which knows the chart-note ID and needs to hand
    // the orchestrator our internal note ID.
    public async Task<int?> GetDoctorNoteIdByChartNoteIdAsync(int chartNoteId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.DoctorNotes
            .Where(x => x.ChartNoteId == chartNoteId)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync();
    }

    // Look up a DoctorNote by our internal Id. Used by the portal-correction
    // submit endpoint to find the original failing note so its date + doctor
    // can be reused for the PSChiro writeback.
    public async Task<DoctorNote?> GetDoctorNoteByIdAsync(int doctorNoteId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.DoctorNotes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == doctorNoteId);
    }

    // After a successful PSChiro writeback, link our portal-authored DoctorNote
    // to the newly-created PSChiro ChartNote.Id so the relationship is
    // bidirectional and future sync runs don't try to re-import the writeback.
    public async Task LinkPortalNoteToChartNoteAsync(int doctorNoteId, int chartNoteId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.DoctorNotes.FirstOrDefaultAsync(x => x.Id == doctorNoteId);
        if (row is null) return;
        row.ChartNoteId = chartNoteId;
        await db.SaveChangesAsync();
    }

    // True when we already have at least one DoctorNote for this patient.
    // Used by the sync service to decide whether to trigger a full history
    // backfill on first encounter.
    public async Task<bool> PatientHasAnyDoctorNoteAsync(int patientId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.DoctorNotes.AnyAsync(x => x.PatientId == patientId);
    }

    public async Task InsertDoctorNoteAsync(DoctorNote note)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        // Dedupe only applies to ChiroTouch-sourced notes (ChartNoteId set).
        // Portal-authored corrections (ChartNoteId == null) are always new.
        if (note.ChartNoteId.HasValue)
        {
            bool exists = await db.DoctorNotes.AnyAsync(x => x.ChartNoteId == note.ChartNoteId);
            if (exists) return;
        }
        note.CreatedAt = DateTime.UtcNow;
        db.DoctorNotes.Add(note);
        await db.SaveChangesAsync();
    }

    // Inserts a doctor-authored correction note made through the portal (no
    // ChiroTouch ChartNote). Returns the new DoctorNote.Id so the caller can
    // trigger an immediate re-scrub on it.
    public async Task<int> InsertPortalAuthoredNoteAsync(
        int workflowCaseId, int patientId, int? doctorId, string text, DateTime? noteDate = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var note = new DoctorNote
        {
            WorkflowCaseId = workflowCaseId,
            PatientId = patientId,
            ChartNoteId = null,
            DoctorId = doctorId,
            NoteDate = noteDate ?? DateTime.UtcNow,
            SoapPtr = null,
            RawRtf = null,
            PlainText = text,
            CreatedAt = DateTime.UtcNow,
        };
        db.DoctorNotes.Add(note);
        await db.SaveChangesAsync();
        return note.Id;
    }
}
