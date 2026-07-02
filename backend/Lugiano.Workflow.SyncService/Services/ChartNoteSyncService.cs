using System.Text.Json;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.ChiroTouch.Models;
using Lugiano.Workflow.SyncService.Services.Scrubbing;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Lugiano.Workflow.SyncService.Util;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services;

// Orchestration only: reads via IChartNoteReadQueries (ChiroTouch, read-only),
// writes via the workflow services (EF). No raw SQL here.
public sealed class ChartNoteSyncService
{
    private readonly IChartNoteReadQueries _reads;
    private readonly IPatientStatusQueries _status;
    private readonly WorkflowCaseService _cases;
    private readonly SyncStateService _syncState;
    private readonly CorrectionRequestService _corrections;
    private readonly ScrubOrchestrator _scrubs;
    private readonly ILogger<ChartNoteSyncService> _logger;

    public ChartNoteSyncService(
        IChartNoteReadQueries reads,
        IPatientStatusQueries status,
        WorkflowCaseService cases,
        SyncStateService syncState,
        CorrectionRequestService corrections,
        ScrubOrchestrator scrubs,
        ILogger<ChartNoteSyncService> logger)
    {
        _reads = reads;
        _status = status;
        _cases = cases;
        _syncState = syncState;
        _corrections = corrections;
        _scrubs = scrubs;
        _logger = logger;
    }

    public async Task<SyncResult> ProcessAsync(CancellationToken ct)
    {
        if (!_reads.IsConfigured)
        {
            _logger.LogInformation("ChartNotes: ChiroTouch connection not configured; skipping source poll.");
            return SyncResult.Empty;
        }

        long lastSeen = await _syncState.GetLastSeenAsync(SyncKeys.LastSeenChartNoteId);
        var rows = await _reads.GetNewChartNotesAsync(lastSeen);

        if (rows.Count == 0)
            return SyncResult.Empty;

        _logger.LogInformation("ChartNotes: {Count} new note(s) since ID {LastSeen}.",
            rows.Count, lastSeen);

        int casesTouched = 0;
        int eventsCreated = 0;
        long maxProcessedId = lastSeen;

        foreach (var (note, patient) in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Reconcile the WHOLE patient, not just this note: pull current
                // insurance + notes truth (real dates) so the case reflects reality.
                var status = await _status.GetStatusAsync(note.PatientId);
                int caseId = await _cases.CreateOrUpdateCaseAsync(
                    note.PatientId, patient.FirstName, patient.LastName,
                    hasInsurance: status.HasInsurance,
                    insuranceAddedAt: status.InsuranceEffectiveDate,
                    hasNotes: status.HasNotes,
                    doctorNotesReceivedAt: status.LatestNoteDate);
                casesTouched++;

                // First encounter with this patient → backfill their full
                // historical note set before the trigger note. Idempotent via
                // existence check; later polls skip this block.
                if (!await _cases.PatientHasAnyDoctorNoteAsync(note.PatientId))
                {
                    var history = await _reads.GetAllChartNotesForPatientAsync(note.PatientId);
                    if (history.Count > 0)
                    {
                        _logger.LogInformation(
                            "ChartNotes: first encounter with patient {PatientId} — backfilling {Count} historical note(s).",
                            note.PatientId, history.Count);
                        foreach (var (h, _) in history)
                        {
                            ct.ThrowIfCancellationRequested();
                            await EnsureDoctorNoteSavedAsync(caseId, h);
                        }
                    }
                }

                // Trigger note itself. No-op if the backfill above already saved it.
                await EnsureDoctorNoteSavedAsync(caseId, note);

                if (!await _cases.WorkflowEventExistsAsync(SourceTables.ChartNotes, note.Id))
                {
                    await _cases.InsertWorkflowEventAsync(new WorkflowEvent
                    {
                        WorkflowCaseId = caseId,
                        PatientId = note.PatientId,
                        EventType = EventTypes.DoctorNoteReceived,
                        SourceSystem = SourceSystems.PSChiro,
                        SourceTable = SourceTables.ChartNotes,
                        SourceRecordId = note.Id,
                        EventDataJson = JsonSerializer.Serialize(note)
                    });
                    eventsCreated++;
                    _logger.LogInformation(
                        "ChartNotes: case {CaseId} (patient {PatientId}) reconciled, event DoctorNoteReceived (note {NoteId}).",
                        caseId, note.PatientId, note.Id);
                }

                // Auto-resolve any open kickback for this patient: a new note
                // arriving is the doctor's response (whether it's a fresh chart
                // entry or a corrected one).
                await _corrections.ResolveOpenForPatientAsync(note.PatientId, ct);

                // Auto-scrub policy: every new DoctorNote gets its own scrub
                // (per-note model — older verdicts don't gate newer ones). The
                // gate only enforces idempotency so a re-run doesn't double-scrub.
                // Scrub errors are logged but never break the sync cycle.
                try
                {
                    var doctorNoteId = await _cases.GetDoctorNoteIdByChartNoteIdAsync(note.Id);
                    if (doctorNoteId.HasValue
                        && await _scrubs.ShouldAutoScrubAsync(doctorNoteId.Value, ct))
                    {
                        var result = await _scrubs.RunForNoteAsync(doctorNoteId.Value, ct);
                        _logger.LogInformation(
                            "ChartNotes: auto-scrubbed note {NoteId} (case {CaseId}, patient {PatientId}) -> {Verdict}.",
                            doctorNoteId.Value, caseId, note.PatientId, result.Verdict);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ChartNotes: auto-scrub failed for note {NoteId} (case {CaseId}); continuing sync.",
                        note.Id, caseId);
                }

                if (note.Id > maxProcessedId)
                    maxProcessedId = note.Id;
                await _syncState.SetLastSeenAsync(SyncKeys.LastSeenChartNoteId, maxProcessedId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ChartNotes: failed processing note {NoteId} (patient {PatientId}); stopping this cycle to preserve ordering.",
                    note.Id, note.PatientId);
                break;
            }
        }

        return new SyncResult(rows.Count, casesTouched, eventsCreated);
    }

    // One-shot historical backfill for one patient: inserts every non-empty
    // ChartNote not already in our DoctorNote table. Idempotent. Fills the gap
    // for patients captured AFTER some history existed — the last-seen-id cursor
    // only sees notes with ID > cursor, so pre-cursor history never syncs.
    // Returns (existingCount, newlyInserted).
    public async Task<(int existing, int inserted)> BackfillHistoricalNotesForPatientAsync(int patientId, CancellationToken ct = default)
    {
        if (!_reads.IsConfigured) return (0, 0);

        // Need the WorkflowCase Id to associate new DoctorNote rows. Skip if the
        // patient isn't in workflow yet (captured on next natural sync).
        var caseId = await _cases.GetCaseIdByPatientIdAsync(patientId, ct);
        if (caseId is null)
        {
            _logger.LogInformation(
                "BackfillHistoricalNotesForPatient: patient {PatientId} not in WorkflowCase yet — skipping.",
                patientId);
            return (0, 0);
        }

        var history = await _reads.GetAllChartNotesForPatientAsync(patientId);
        int inserted = 0;
        int existing = 0;
        foreach (var (h, _) in history)
        {
            ct.ThrowIfCancellationRequested();
            if (await _cases.DoctorNoteExistsAsync(h.Id))
            {
                existing++;
                continue;
            }
            await EnsureDoctorNoteSavedAsync(caseId.Value, h);
            inserted++;
        }

        _logger.LogInformation(
            "BackfillHistoricalNotesForPatient: patient {PatientId} — {Total} CT notes, {Existing} already synced, {Inserted} newly inserted.",
            patientId, history.Count, existing, inserted);
        return (existing, inserted);
    }

    // Batch variant — walks every patient in WorkflowCase and runs the per-
    // patient backfill. For a first-time cleanup across the whole practice.
    // Optional patientIds filter lets you scope the run.
    public async Task<(int patientsScanned, int totalInserted)> BackfillHistoricalNotesAllPatientsAsync(
        IEnumerable<int>? patientIdFilter = null,
        CancellationToken ct = default)
    {
        var allPatientIds = await _cases.GetAllPatientIdsAsync(ct);
        var target = patientIdFilter is null
            ? allPatientIds
            : allPatientIds.Intersect(patientIdFilter).ToList();

        int totalInserted = 0;
        foreach (var pid in target)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (_, inserted) = await BackfillHistoricalNotesForPatientAsync(pid, ct);
                totalInserted += inserted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "BackfillHistoricalNotesAllPatients: patient {PatientId} failed; continuing with remaining patients.",
                    pid);
            }
        }
        return (target.Count(), totalInserted);
    }

    // Reconcile the notes we've ALREADY stored for one patient against
    // ChiroTouch's current truth — the "note edited" path our sync never had.
    // A note's ID (our sync + idempotency key) is fixed at creation, so once a
    // note is captured, later edits (doctor finishes the SOAP → SOAPPtr
    // repointed, or a signature lands) were invisible to us and the stored
    // RawRtf/PlainText stayed frozen at first capture. This finds the drifted
    // rows and refreshes them. Returns the count refreshed.
    public async Task<int> ReconcileNotesForPatientAsync(
        int patientId, bool rescrub = true, CancellationToken ct = default)
    {
        if (!_reads.IsConfigured) return 0;
        var refs = await _cases.GetChartSourcedNoteRefsAsync(patientId, ct);
        return await ReconcileNoteRefsAsync(refs, rescrub, ct);
    }

    // Practice-wide sweep — the retroactive fix for the notes that drifted
    // before this trigger existed, and the periodic backstop for edits that
    // change content without moving the signature timestamp. rescrub defaults
    // OFF here: a full sweep can touch many notes, and re-scrubbing every
    // changed one would fire a large batch of Claude calls — the content/signed
    // refresh is the retroactive goal; verdicts refresh via the going-forward
    // trigger or a deliberate scrub pass. Returns (notesChecked, notesRefreshed).
    public async Task<(int notesChecked, int notesRefreshed)> ReconcileAllNotesAsync(
        bool rescrub = false, CancellationToken ct = default)
    {
        if (!_reads.IsConfigured) return (0, 0);
        var refs = await _cases.GetChartSourcedNoteRefsAsync(null, ct);
        var refreshed = await ReconcileNoteRefsAsync(refs, rescrub, ct);
        return (refs.Count, refreshed);
    }

    // Incremental "note edited / re-signed" trigger — run every poll cycle.
    // Reads CN signatures that changed since our cursor and reconciles ONLY
    // those notes (cheap: no full-table sweep). On first run it seeds the cursor
    // to ChiroTouch's newest signature time without replaying history — the
    // pre-existing backlog is the retroactive sweep's job. Returns notes refreshed.
    public async Task<int> ReconcileRecentlySignedAsync(CancellationToken ct = default)
    {
        if (!_reads.IsConfigured) return 0;

        long cursorTicks = await _syncState.GetLastSeenAsync(SyncKeys.LastSeenSignatureTicks);
        if (cursorTicks == 0)
        {
            var max = await _reads.GetMaxSignatureTimeAsync();
            if (max.HasValue)
                await _syncState.SetLastSeenAsync(SyncKeys.LastSeenSignatureTicks, max.Value.Ticks);
            _logger.LogInformation(
                "Signature cursor seeded to {Max}; historical backlog handled by the reconcile sweep.", max);
            return 0;
        }

        var since = new DateTime(cursorTicks);
        var changes = await _reads.GetSignaturesChangedSinceAsync(since);
        if (changes.Count == 0) return 0;

        // Reconcile only notes we already store; brand-new notes are captured by
        // the ID-cursor pass in ProcessAsync.
        var refs = await _cases.GetChartSourcedNoteRefsByChartNoteIdsAsync(
            changes.Select(c => c.ChartNoteId), ct);
        // Real-time edits: re-scrub the fresh text (few notes per cycle).
        int refreshed = await ReconcileNoteRefsAsync(refs, rescrub: true, ct);

        long newCursor = changes.Max(c => c.SignedAt.Ticks);
        await _syncState.SetLastSeenAsync(SyncKeys.LastSeenSignatureTicks, newCursor);

        _logger.LogInformation(
            "Signatures: {Changed} changed since {Since}; {Refreshed} stored note(s) refreshed.",
            changes.Count, since, refreshed);
        return refreshed;
    }

    // Core reconcile: compare each stored note's SoapPtr/SignedAt against
    // ChiroTouch's live head (batched). Two kinds of drift:
    //   • SOAPPtr repointed  → the note's TEXT changed. Refresh RawRtf/PlainText,
    //     re-reconcile the patient's case, and (when rescrub) re-scrub the fresh
    //     text since the prior verdict is now stale.
    //   • only SignedAt moved → metadata-only (first/re-sign, or the one-time
    //     SignedAt backfill for pre-existing rows). Persist the signed time
    //     cheaply — no RTF re-read, no re-scrub, no case reconcile.
    // Returns the count of TEXT refreshes (the meaningful ones).
    private async Task<int> ReconcileNoteRefsAsync(
        IReadOnlyList<DoctorNoteRef> refs, bool rescrub, CancellationToken ct)
    {
        if (refs.Count == 0) return 0;
        int refreshed = 0;

        foreach (var chunk in refs.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var heads = await _reads.GetNoteHeadsAsync(chunk.Select(r => r.ChartNoteId));
            var patientsToReconcile = new HashSet<int>();

            foreach (var r in chunk)
            {
                ct.ThrowIfCancellationRequested();
                // Note gone from ChiroTouch (rare) — leave our copy as-is.
                if (!heads.TryGetValue(r.ChartNoteId, out var head)) continue;

                bool textChanged = head.SoapPtr != r.SoapPtr; // pointer repoint == edited content
                bool signChanged = head.SignedAt != r.SignedAt;
                if (!textChanged && !signChanged) continue;

                if (!textChanged)
                {
                    // Metadata-only: persist the signed time without touching
                    // content or scrubs (also the SignedAt backfill path).
                    await _cases.UpdateDoctorNoteSignedAtAsync(r.ChartNoteId, head.SignedAt);
                    continue;
                }

                // Text changed — re-read the RTF from the CURRENT pointer
                // (r.SoapPtr may now be an orphaned/superseded chain).
                string? rawRtf = null;
                try
                {
                    if (head.SoapPtr is int ptr and not 0)
                        rawRtf = await _reads.GetNoteRtfAsync(ptr);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Reconcile: RTF re-read failed for note {NoteId}; storing without text.",
                        r.ChartNoteId);
                }

                var updatedId = await _cases.UpdateDoctorNoteContentAsync(
                    r.ChartNoteId, head.SoapPtr, rawRtf, RtfConverter.ToPlainText(rawRtf), head.SignedAt);
                if (updatedId is not int noteId) continue;

                refreshed++;
                patientsToReconcile.Add(r.PatientId);
                _logger.LogInformation(
                    "Reconcile: note {ChartNoteId} (patient {PatientId}) refreshed — SoapPtr {Old}->{New}, signed {Signed}.",
                    r.ChartNoteId, r.PatientId, r.SoapPtr, head.SoapPtr, head.SignedAt);

                // Fresh text → prior scrub is stale. Force a re-scrub (bypasses
                // ShouldAutoScrub's idempotency gate, which only stops
                // DOUBLE-scrubbing unchanged notes), honoring the AutoScrub
                // switch. Skipped for bulk backfills that opt out. Never breaks
                // the sweep.
                if (rescrub)
                {
                    try
                    {
                        if (_scrubs.AutoScrubEnabled)
                            await _scrubs.RunForNoteAsync(noteId, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Reconcile: re-scrub failed for note {NoteId} (case reconcile continues).", noteId);
                    }
                }
            }

            foreach (var pid in patientsToReconcile)
            {
                ct.ThrowIfCancellationRequested();
                await ReconcileCaseAsync(pid);
            }
        }

        return refreshed;
    }

    // Re-derive a case from full ChiroTouch truth (insurance + notes + real
    // dates). Passing null names preserves the existing case's names.
    private async Task ReconcileCaseAsync(int patientId)
    {
        var status = await _status.GetStatusAsync(patientId);
        await _cases.CreateOrUpdateCaseAsync(
            patientId, firstName: null, lastName: null,
            hasInsurance: status.HasInsurance, insuranceAddedAt: status.InsuranceEffectiveDate,
            hasNotes: status.HasNotes, doctorNotesReceivedAt: status.LatestNoteDate);
    }

    // Inserts a DoctorNote row for a single source ChartNote, reconstructing
    // the RTF and plain text along the way. Idempotent — exits cleanly if the
    // ChartNoteId is already in our table. One bad RTF chain logs and stores
    // null text rather than breaking the whole sync.
    private async Task EnsureDoctorNoteSavedAsync(int caseId, SourceChartNote note)
    {
        if (await _cases.DoctorNoteExistsAsync(note.Id)) return;

        string? rawRtf = null;
        try
        {
            if (note.SoapPtr is int ptr and not 0)
                rawRtf = await _reads.GetNoteRtfAsync(ptr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ChartNotes: RTF reconstruction failed for note {NoteId}; storing without text.",
                note.Id);
        }

        // Capture the signed time (CT "Signed: …" clock) at insert so it's part
        // of the persisted record from day one, not a live read at display time.
        DateTime? signedAt = null;
        try
        {
            var heads = await _reads.GetNoteHeadsAsync(new[] { note.Id });
            if (heads.TryGetValue(note.Id, out var head)) signedAt = head.SignedAt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ChartNotes: signed-time read failed for note {NoteId}; storing without it.", note.Id);
        }

        await _cases.InsertDoctorNoteAsync(new DoctorNote
        {
            WorkflowCaseId = caseId,
            PatientId = note.PatientId,
            ChartNoteId = note.Id,
            DoctorId = note.DoctorId,
            NoteDate = note.NoteDate,
            SoapPtr = note.SoapPtr,
            RawRtf = rawRtf,
            PlainText = RtfConverter.ToPlainText(rawRtf),
            SignedAt = signedAt,
        });
    }
}
