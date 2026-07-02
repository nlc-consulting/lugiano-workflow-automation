using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Services;

// Minimal projection of a chart-sourced DoctorNote used for staleness checks
// (our stored SoapPtr vs ChiroTouch's current SOAPPtr).
public sealed record DoctorNoteRef(int Id, int PatientId, int ChartNoteId, int? SoapPtr, DateTime? SignedAt);

public sealed class WorkflowCaseService
{
    private readonly IDbContextFactory<WorkflowDbContext> _dbFactory;

    public WorkflowCaseService(IDbContextFactory<WorkflowDbContext> dbFactory) => _dbFactory = dbFactory;

    // Creates or RECONCILES a patient's case from full ChiroTouch truth: every
    // trigger passes reconciled status (insurance + notes + real dates), so a
    // note trigger also captures insurance seen since intake and vice versa.
    // Stamps reflect current reality (set, not first-seen); state is derived.
    // PIP is portal-driven and preserved across reconciliation. Returns the
    // WorkflowCase.Id. One case per patient (unique index on PatientId).
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

    // Resolve the WorkflowCase.Id for a patient, or null if no case exists.
    // Used by the historical-notes backfill to attach imported notes to the
    // right case row.
    public async Task<int?> GetCaseIdByPatientIdAsync(int patientId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowCases
            .Where(c => c.PatientId == patientId)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync(ct);
    }

    // Every distinct patient ID currently in WorkflowCase. Used by the
    // batch historical-notes backfill to walk the whole practice.
    public async Task<IReadOnlyList<int>> GetAllPatientIdsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowCases
            .OrderBy(c => c.Id)
            .Select(c => c.PatientId)
            .ToListAsync(ct);
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

    // Refresh a chart-sourced DoctorNote's content in place after ChiroTouch
    // changed it under us (the doctor edited/finalized the note — SOAPPtr got
    // repointed, or a signature landed/moved). This is the write half of the
    // "note edited" trigger we never had: our original insert froze RawRtf/
    // PlainText at first capture and nothing ever refreshed them.
    //
    // No-op returning null when the row is missing or nothing actually changed
    // (idempotent — safe to call on every sweep). Returns the DoctorNote.Id on a
    // real update so the caller can re-fire the scrub against the fresh text.
    public async Task<int?> UpdateDoctorNoteContentAsync(
        int chartNoteId, int? soapPtr, string? rawRtf, string? plainText, DateTime? signedAt)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.DoctorNotes.FirstOrDefaultAsync(x => x.ChartNoteId == chartNoteId);
        if (row is null) return null;

        bool changed = row.SoapPtr != soapPtr
            || row.RawRtf != rawRtf
            || row.SignedAt != signedAt;
        if (!changed) return null;

        row.SoapPtr = soapPtr;
        row.RawRtf = rawRtf;
        row.PlainText = plainText;
        row.SignedAt = signedAt;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return row.Id;
    }

    // Metadata-only update: persist just the ChiroTouch signed time (and stamp
    // UpdatedAt) when a note was (re-)signed but its content pointer is
    // unchanged. Cheap path used by the reconcile sweep so a sign-only change
    // (and the one-time SignedAt backfill) doesn't re-read RTF or re-scrub.
    public async Task UpdateDoctorNoteSignedAtAsync(int chartNoteId, DateTime? signedAt)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.DoctorNotes.FirstOrDefaultAsync(x => x.ChartNoteId == chartNoteId);
        if (row is null || row.SignedAt == signedAt) return;
        row.SignedAt = signedAt;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    // Lightweight refs for every chart-sourced DoctorNote (optionally scoped to
    // one patient), for staleness detection: compare each row's SoapPtr against
    // ChiroTouch's current SOAPPtr to find notes that drifted since capture.
    public async Task<IReadOnlyList<DoctorNoteRef>> GetChartSourcedNoteRefsAsync(
        int? patientId = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.DoctorNotes.AsNoTracking().Where(x => x.ChartNoteId != null);
        if (patientId is int pid) q = q.Where(x => x.PatientId == pid);
        return await q
            .Select(x => new DoctorNoteRef(x.Id, x.PatientId, x.ChartNoteId!.Value, x.SoapPtr, x.SignedAt))
            .ToListAsync(ct);
    }

    // Refs for a specific set of ChartNote IDs — the targeted lookup the
    // signature-cursor trigger uses to reconcile only the notes that were just
    // (re-)signed, instead of sweeping the whole table each 30s poll.
    public async Task<IReadOnlyList<DoctorNoteRef>> GetChartSourcedNoteRefsByChartNoteIdsAsync(
        IEnumerable<int> chartNoteIds, CancellationToken ct = default)
    {
        var ids = chartNoteIds.Distinct().ToList();
        if (ids.Count == 0) return Array.Empty<DoctorNoteRef>();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DoctorNotes.AsNoTracking()
            .Where(x => x.ChartNoteId != null && ids.Contains(x.ChartNoteId.Value))
            .Select(x => new DoctorNoteRef(x.Id, x.PatientId, x.ChartNoteId!.Value, x.SoapPtr, x.SignedAt))
            .ToListAsync(ct);
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
            // Sticky marker — stays true even after the PSChiro writeback sets
            // ChartNoteId. Routes future failing scrubs to Human Review.
            IsPortalAuthored = true,
        };
        db.DoctorNotes.Add(note);
        await db.SaveChangesAsync();
        return note.Id;
    }

    // Manual scrub override — writes a new ScrubResult with the chosen verdict
    // and a marker in FindingsJson noting it was a human override. Latest
    // wins on the rollup, so this flips a failing note to passing (or vice
    // versa) without re-running Claude.
    public async Task<ScrubResult> OverrideScrubVerdictAsync(
        int doctorNoteId, string newVerdict, string? overriddenBy, string? reason)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var note = await db.DoctorNotes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == doctorNoteId)
            ?? throw new InvalidOperationException($"DoctorNote {doctorNoteId} not found.");

        var findings = new
        {
            verdict = newVerdict,
            overall_confidence = 100,
            summary = $"Manual override by {overriddenBy ?? "operator"}: {reason ?? "no reason given"}.",
            sections = new { },
            diagnosis_alignment = new { score = 100, issues = Array.Empty<object>() },
            charge_alignment = new { score = 100, issues = Array.Empty<object>() },
            issues = Array.Empty<object>(),
            manual_override = true,
            overridden_by = overriddenBy,
            reason,
        };

        var result = new ScrubResult
        {
            DoctorNoteId = note.Id,
            WorkflowCaseId = note.WorkflowCaseId,
            Verdict = newVerdict,
            OverallConfidence = 100,
            Summary = $"Manual override: {reason ?? "no reason given"}",
            FindingsJson = System.Text.Json.JsonSerializer.Serialize(findings),
            RawResponseJson = null,
            ModelUsed = "manual-override",
            PromptVersion = "manual",
            RanAt = DateTime.UtcNow,
        };
        db.ScrubResults.Add(result);
        await db.SaveChangesAsync();
        return result;
    }
}
