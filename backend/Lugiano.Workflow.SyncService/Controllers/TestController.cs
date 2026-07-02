using Lugiano.Workflow.SyncService.Services;
using Lugiano.Workflow.SyncService.Util;
using Lugiano.Workflow.SyncService.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lugiano.Workflow.SyncService.Controllers;

// Demo-only helpers — repeatable test flows against the Fakee Test patient
// (2765) without manual SQL. [AllowAnonymous] is intentional (hit from curl
// during dev). SECURITY: gate behind auth/env flag and delete [AllowAnonymous]
// before shipping to a real environment.
[AllowAnonymous]
[ApiController]
[Route("test")]
public sealed class TestController : ControllerBase
{
    private readonly IPSChiroWriteService _pschiroWrite;
    private readonly IDbContextFactory<WorkflowDbContext> _dbFactory;
    private readonly ChartNoteSyncService _chartNoteSync;
    private readonly ILogger<TestController> _logger;

    public TestController(
        IPSChiroWriteService pschiroWrite,
        IDbContextFactory<WorkflowDbContext> dbFactory,
        ChartNoteSyncService chartNoteSync,
        ILogger<TestController> logger)
    {
        _pschiroWrite = pschiroWrite;
        _dbFactory = dbFactory;
        _chartNoteSync = chartNoteSync;
        _logger = logger;
    }

    // POST /test/inject-note — inserts a thin chart note into PSChiro for the
    // Fakee Test patient (or override). Same validated 3-table recipe (ChartText
    // + ChartNotes + Signatures) as the portal-correction writeback. Defaults:
    //   patientId = 2765 (Fakee Test)
    //   doctorId  = 142  (Joel Kerak, DC, NB — has stored signature)
    //   noteDate  = today (must match an existing Appointment to render in CT)
    //   text      = intentionally thin -> fails scrub
    // Next sync tick auto-scrubs it; the failing verdict surfaces it in Doctor
    // View for the kickback demo.
    [HttpPost("inject-note")]
    public async Task<IActionResult> InjectNote([FromBody] InjectNoteRequest? req, CancellationToken ct)
    {
        if (!_pschiroWrite.IsConfigured)
            return BadRequest(new { error = "PSChiro write account is not configured (set ChiroTouchWrite connection string)." });

        var patientId = req?.PatientId ?? 2765;
        var doctorId = req?.DoctorId ?? 142;
        var noteDate = (req?.NoteDate ?? DateTime.Today).Date;
        // Default text (per Nick's spec): identifiable as a test note,
        // intentionally thin so it fails scrub. Safe to delete from CT history.
        var text = string.IsNullOrWhiteSpace(req?.Text)
            ? $"Test note ({noteDate:M/d/yyyy}) inserted by Lugiano portal automation on {DateTime.Now:yyyy-MM-dd HH:mm}. No clinical content; safe to delete."
            : req!.Text!.Trim();

        try
        {
            var result = await _pschiroWrite.WriteCorrectionChartNoteAsync(
                patientId: patientId,
                doctorId: doctorId,
                noteDate: noteDate,
                plainText: text,
                ct: ct);

            _logger.LogInformation(
                "Test inject-note: patient {PatientId} doctor {DoctorId} date {NoteDate:yyyy-MM-dd} -> ChartNotes.ID {ChartNoteId}.",
                patientId, doctorId, noteDate, result.ChartNoteId);

            return Ok(new
            {
                patientId,
                doctorId,
                noteDate = noteDate.ToString("yyyy-MM-dd"),
                chartNoteId = result.ChartNoteId,
                chartTextPtr = result.ChartTextPtr,
                hint = "Wait for next sync tick (~30s) — note will appear in Doctor View if scrub fails.",
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public sealed record InjectNoteRequest(int? PatientId, int? DoctorId, DateTime? NoteDate, string? Text);

    // POST /test/reprocess-plaintext — re-runs RtfConverter.ToPlainText over
    // every DoctorNote's RawRtf and updates PlainText. Run after changing the
    // extraction logic (e.g. letterhead-strip on 2026-07-02) to apply it to
    // historical notes without re-syncing from PSChiro.
    //   ?patientId=N  — only this patient (test-first before full backfill)
    //   ?dryRun=true  — count what WOULD change without writing
    // Returns { total, changed, unchanged, nullRaw, sample }.
    [HttpPost("reprocess-plaintext")]
    public async Task<IActionResult> ReprocessPlaintext(
        [FromQuery] int? patientId = null,
        [FromQuery] bool dryRun = false,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.DoctorNotes.AsQueryable();
        if (patientId is int pid)
            query = query.Where(n => n.PatientId == pid);

        var notes = await query
            .Select(n => new { n.Id, n.ChartNoteId, n.PatientId, n.RawRtf, n.PlainText })
            .ToListAsync(ct);

        int changed = 0, unchanged = 0, nullRaw = 0;
        var sample = new List<object>();

        foreach (var note in notes)
        {
            if (string.IsNullOrWhiteSpace(note.RawRtf)) { nullRaw++; continue; }
            var recomputed = RtfConverter.ToPlainText(note.RawRtf);
            if (recomputed == note.PlainText) { unchanged++; continue; }
            changed++;
            if (sample.Count < 5)
            {
                sample.Add(new
                {
                    doctorNoteId = note.Id,
                    chartNoteId = note.ChartNoteId,
                    patientId = note.PatientId,
                    oldLen = note.PlainText?.Length ?? 0,
                    newLen = recomputed?.Length ?? 0,
                    oldHead = note.PlainText is null ? null : note.PlainText[..Math.Min(120, note.PlainText.Length)],
                    newHead = recomputed is null ? null : recomputed[..Math.Min(120, recomputed.Length)],
                });
            }
            if (!dryRun)
            {
                var entity = await db.DoctorNotes.FirstAsync(n => n.Id == note.Id, ct);
                entity.PlainText = recomputed;
            }
        }

        if (!dryRun && changed > 0)
            await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "reprocess-plaintext: total={Total} changed={Changed} unchanged={Unchanged} nullRaw={NullRaw} dryRun={DryRun} patientFilter={Pid}",
            notes.Count, changed, unchanged, nullRaw, dryRun, patientId?.ToString() ?? "(all)");

        return Ok(new
        {
            total = notes.Count,
            changed,
            unchanged,
            nullRaw,
            dryRun,
            patientFilter = patientId,
            sample,
        });
    }

    // POST /test/backfill-historical-notes — pulls every ChartNote for a patient
    // (or all workflow patients) from ChiroTouch and inserts any missing from our
    // DoctorNote table. Fixes the "note stuck at 'not synced yet'" state for
    // patients whose history predated when our sync started tracking them.
    //   ?patientId=N  — one patient (first-pass test subject: 18288 / Kadesha
    //                   Smith / AccountNo 404706)
    //   (no filter)   — EVERY WorkflowCase patient (whole-practice run)
    // Idempotent: re-running inserts nothing; never touches existing rows.
    [HttpPost("backfill-historical-notes")]
    public async Task<IActionResult> BackfillHistoricalNotes(
        [FromQuery] int? patientId = null,
        CancellationToken ct = default)
    {
        if (patientId is int pid)
        {
            var (existing, inserted) = await _chartNoteSync.BackfillHistoricalNotesForPatientAsync(pid, ct);
            return Ok(new
            {
                mode = "single-patient",
                patientId = pid,
                existing,
                inserted,
                totalInDbNow = existing + inserted,
            });
        }

        var (patientsScanned, totalInserted) =
            await _chartNoteSync.BackfillHistoricalNotesAllPatientsAsync(ct: ct);
        return Ok(new
        {
            mode = "all-patients",
            patientsScanned,
            totalInserted,
        });
    }

    // POST /test/reconcile-notes — the RETROACTIVE fix. Compares every stored
    // DoctorNote's SoapPtr against ChiroTouch's current SOAPPtr and refreshes
    // the ones that drifted (notes the doctor edited/finalized after we first
    // captured them — RawRtf/PlainText/SignedAt were frozen). Also re-reconciles
    // each touched patient's case, so a note that got signed after capture
    // flips the case out of "AwaitingDoctorNotes". Optional ?patientId scopes it.
    //
    // Idempotent: a second run refreshes nothing (pointers now match). Safe to
    // re-run. This is the one-time cleanup for the pre-existing stale backlog;
    // going forward the signature-cursor trigger keeps notes current per cycle.
    [HttpPost("reconcile-notes")]
    public async Task<IActionResult> ReconcileNotes(
        [FromQuery] int? patientId = null,
        [FromQuery] bool rescrub = false)
    {
        // Detach from the request's cancellation token: this is a long admin
        // sweep and a client read-timeout/disconnect must NOT abort it mid-run.
        var ct = CancellationToken.None;

        if (patientId is int pid)
        {
            var refreshed = await _chartNoteSync.ReconcileNotesForPatientAsync(pid, rescrub, ct);
            return Ok(new { mode = "single-patient", patientId = pid, rescrub, notesRefreshed = refreshed });
        }

        // Practice-wide sweep can run for minutes (thousands of notes). Fire it
        // in the background and return immediately so the caller never times
        // out; watch the service log for "Reconcile:" lines and the final count.
        _ = Task.Run(async () =>
        {
            try
            {
                var (checkedCount, refreshedCount) = await _chartNoteSync.ReconcileAllNotesAsync(rescrub, ct);
                _logger.LogInformation(
                    "Reconcile sweep complete: {Checked} notes checked, {Refreshed} refreshed (rescrub={Rescrub}).",
                    checkedCount, refreshedCount, rescrub);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconcile sweep failed.");
            }
        });
        return Accepted(new { mode = "all-patients", status = "started", rescrub });
    }
}
