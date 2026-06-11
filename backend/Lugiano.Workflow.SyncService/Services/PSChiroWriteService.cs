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
        // Wrap plain text in minimal RTF — proven sufficient for ChiroTouch's
        // TX_RTF32 renderer (the fuller font/color/style preamble in native
        // notes is template-generated but not required by the parser).
        // Cap at 4000 chars for safety (varchar(4096) field).
        var rtfBody = BuildRtf(plainText);

        await using var conn = _writeDb.Create();
        await conn.OpenAsync(ct);
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
                new { patientId, doctorId, noteDate, ptr = newPtr },
                transaction: tx);

            // 3. Signature — copy the doctor's real stored ImageBase64 so
            //    ChiroTouch renders the note as properly signed. Sub-select
            //    is intentional: the lugiano_rw account has SELECT on the
            //    schema, so it can pull the existing image without us having
            //    to round-trip it through the app.
            var sigRows = await conn.ExecuteAsync(
                """
                INSERT INTO dbo.Signatures
                       (SigType, SigTypeID, SigTimestamp, DoctorID, Base64, ImageFormat, ImageBase64, PatientID)
                SELECT 'CN', @noteId, GETDATE(), @doctorId, '', '2',
                       (SELECT TOP 1 ImageBase64
                        FROM   dbo.Signatures
                        WHERE  SigType = 'CN'
                          AND  DoctorID = @doctorId
                          AND  ImageBase64 IS NOT NULL
                        ORDER BY SigTimestamp DESC),
                       @patientId;
                """,
                new { noteId, doctorId, patientId },
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

    private static string BuildRtf(string plainText)
    {
        // Minimal RTF envelope — proven to render in ChiroTouch's TX_RTF32 UI.
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
