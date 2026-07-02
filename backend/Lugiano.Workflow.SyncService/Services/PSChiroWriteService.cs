using Dapper;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services;

// Single-purpose writeback into PSChiro for portal-authored chart-note
// corrections. The 3-table insert recipe was validated 6/11/2026 against the
// Fakee Test patient — confirmed ChiroTouch renders the resulting note
// identically to native ones, provided:
//   - NoteDate matches an existing Appointment for the patient (UI is
//     appointment-anchored)
//   - SPtr/OPtr/APtr/PPtr/SecondaryDoctorID set EXPLICITLY to 0
//     (NULL defaults are filtered out by the UI)
//   - Signature carries a real ImageBase64 image (copied from the doctor's
//     existing stored signature)
//
// Read separately about the schema discoveries in the convo / project memory.
public interface IPSChiroWriteService
{
    bool IsConfigured { get; }

    // Inserts a chart note tied to the original failing note's date + doctor.
    // Returns the new PSChiro ChartNotes.ID so the caller can store it back
    // on our DoctorNote row.
    Task<WriteCorrectionResult> WriteCorrectionChartNoteAsync(
        int patientId,
        int doctorId,
        DateTime noteDate,
        string plainText,
        CancellationToken ct = default);

    // Updates the body of an existing ChartNote in place — used when the
    // doctor's correction is a fix of an existing note (not a new note).
    // Preserves the original ChartNotes row entirely (ID, date, doctor,
    // signature, appointment linkage); only ChartText.TextBody is rewritten.
    // Returns the existing ChartTextPtr so the caller can audit-log it.
    Task<int> UpdateChartNoteBodyAsync(
        int chartNoteId,
        string plainText,
        CancellationToken ct = default);
}

public sealed record WriteCorrectionResult(int ChartNoteId, int ChartTextPtr);

public sealed class PSChiroWriteService : IPSChiroWriteService
{
    private readonly ISourceDbWriteConnectionFactory _writeDb;
    private readonly ILogger<PSChiroWriteService> _logger;

    public PSChiroWriteService(ISourceDbWriteConnectionFactory writeDb, ILogger<PSChiroWriteService> logger)
    {
        _writeDb = writeDb;
        _logger = logger;
    }

    public bool IsConfigured => _writeDb.IsConfigured;

    public async Task<WriteCorrectionResult> WriteCorrectionChartNoteAsync(
        int patientId,
        int doctorId,
        DateTime noteDate,
        string plainText,
        CancellationToken ct = default)
    {
        // Minimal RTF is proven sufficient for ChiroTouch's TX_RTF32 renderer;
        // the fuller native preamble is template-generated, not parser-required.
        var rtfBody = BuildRtf(plainText);
        // Normalize to midnight: ChiroTouch's UI is date-anchored and real notes
        // store NoteDate as midnight, so any time component diverges.
        var anchoredDate = noteDate.Date;

        await using var conn = _writeDb.Create();
        await conn.OpenAsync(ct);

        // Pre-fetch the doctor's stored signature OUTSIDE the transaction. A
        // doctor who never signed would otherwise insert NULL ImageBase64 —
        // the validation failure mode where ChiroTouch hides the note. Fail
        // loud instead.
        var signatureImage = await conn.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT TOP 1 ImageBase64
            FROM   dbo.Signatures
            WHERE  SigType = 'CN'
              AND  DoctorID = @doctorId
              AND  ImageBase64 IS NOT NULL
            ORDER BY SigTimestamp DESC;
            """,
            new { doctorId });

        if (string.IsNullOrEmpty(signatureImage))
            throw new InvalidOperationException(
                $"Cannot write back: doctor {doctorId} has no stored signature image in PSChiro. " +
                "ChiroTouch hides notes whose signature has NULL ImageBase64, so the writeback " +
                "would create an invisible chart note. Have the doctor sign at least one note in " +
                "ChiroTouch before correcting via portal.");

        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // 1. RTF chunk
            var newPtr = await conn.QuerySingleAsync<int>(
                "INSERT INTO dbo.ChartText (TextBody, NextPtr) VALUES (@body, 0); SELECT CAST(SCOPE_IDENTITY() AS int);",
                new { body = rtfBody },
                transaction: tx);

            // 2. ChartNote — every nullable pointer/secondary column set
            //    explicitly to 0 (NULL is filtered by ChiroTouch's UI).
            var noteId = await conn.QuerySingleAsync<int>(
                """
                INSERT INTO dbo.ChartNotes
                       (PatientID, DoctorID, NoteDate, SOAPPtr, SPtr, OPtr, APtr, PPtr, SecondaryDoctorID, Status)
                VALUES (@patientId, @doctorId, @noteDate, @ptr, 0, 0, 0, 0, 0, 0);
                SELECT CAST(SCOPE_IDENTITY() AS int);
                """,
                new { patientId, doctorId, noteDate = anchoredDate, ptr = newPtr },
                transaction: tx);

            // 3. Signature with the pre-fetched real image. PatientID set so
            //    the row matches what ChiroTouch's UI expects on a properly
            //    signed CN row.
            var sigRows = await conn.ExecuteAsync(
                """
                INSERT INTO dbo.Signatures
                       (SigType, SigTypeID, SigTimestamp, DoctorID, Base64, ImageFormat, ImageBase64, PatientID)
                VALUES ('CN', @noteId, GETDATE(), @doctorId, '', '2', @signatureImage, @patientId);
                """,
                new { noteId, doctorId, signatureImage, patientId },
                transaction: tx);

            if (sigRows != 1)
                throw new InvalidOperationException(
                    $"Signature insert affected {sigRows} rows for doctor {doctorId} — expected 1.");

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "PSChiro writeback: patient {PatientId}, doctor {DoctorId}, date {NoteDate:yyyy-MM-dd} -> ChartNotes.ID {ChartNoteId} (ChartText.Ptr {ChartTextPtr}).",
                patientId, doctorId, noteDate, noteId, newPtr);

            return new WriteCorrectionResult(ChartNoteId: noteId, ChartTextPtr: newPtr);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<int> UpdateChartNoteBodyAsync(
        int chartNoteId,
        string plainText,
        CancellationToken ct = default)
    {
        // Safety story (verified 6/25/2026 against prod PSChiro): ChartText.Ptr
        // is the PRIMARY KEY so UPDATE WHERE Ptr=X hits exactly one row, and no
        // SOAPPtr is shared across ChartNotes in prod (each ChartText owned by
        // one note). Transaction anyway so the 0-row case (row deleted between
        // SELECT and UPDATE) rolls back cleanly.
        var rtfBody = BuildRtf(plainText);

        await using var conn = _writeDb.Create();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // Resolve the ChartText pointer for the note's body. The note row
            // is untouched — we only rewrite the dbo.ChartText row it points at.
            var ptr = await conn.QuerySingleOrDefaultAsync<int?>(
                "SELECT SOAPPtr FROM dbo.ChartNotes WHERE ID = @chartNoteId;",
                new { chartNoteId }, transaction: tx);
            if (ptr is null || ptr.Value == 0)
                throw new InvalidOperationException(
                    $"ChartNote {chartNoteId} not found or has no SOAPPtr — cannot update body.");

            var rows = await conn.ExecuteAsync(
                "UPDATE dbo.ChartText SET TextBody = @body WHERE Ptr = @ptr;",
                new { body = rtfBody, ptr = ptr.Value }, transaction: tx);
            if (rows != 1)
                throw new InvalidOperationException(
                    $"ChartText UPDATE for Ptr {ptr.Value} affected {rows} rows — expected 1.");

            await tx.CommitAsync(ct);
            _logger.LogInformation(
                "PSChiro chart-note body updated in place: ChartNotes.ID {ChartNoteId} -> ChartText.Ptr {Ptr}.",
                chartNoteId, ptr.Value);
            return ptr.Value;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static string BuildRtf(string plainText)
    {
        // Minimal RTF envelope proven to render in ChiroTouch's TX_RTF32 UI.
        // Escape backslashes and braces per RTF spec, then wrap.
        var escaped = (plainText ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("{", "\\{")
            .Replace("}", "\\}")
            .Replace("\r\n", "\\par ")
            .Replace("\n", "\\par ");

        // varchar(4096) on disk; keep meaningful safety margin
        const int maxBody = 3800;
        if (escaped.Length > maxBody) escaped = escaped[..maxBody] + "...[truncated]";

        return "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Calibri;}}\\f0\\fs22 " + escaped + "\\par}";
    }
}
