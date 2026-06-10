using System.Text;
using Dapper;
using Lugiano.Workflow.SyncService.ChiroTouch.Models;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.ChiroTouch;

public interface IChartNoteReadQueries
{
    // Whether the ChiroTouch connection string is configured.
    bool IsConfigured { get; }

    // New chart notes (joined to the patient) with ID greater than the cursor.
    Task<IReadOnlyList<(SourceChartNote Note, SourcePatient Patient)>> GetNewChartNotesAsync(
        long lastSeenId);

    // Every chart note for one patient, oldest first. Used to backfill the
    // full history when we encounter a patient for the first time.
    Task<IReadOnlyList<(SourceChartNote Note, SourcePatient Patient)>> GetAllChartNotesForPatientAsync(
        int patientId);

    // Reconstruct a note's RTF by walking ChartText: SOAPPtr -> Ptr, following
    // NextPtr until it is 0, concatenating each TextBody chunk.
    Task<string?> GetNoteRtfAsync(int soapPtr);
}

// All ChiroTouch chart-note SQL lives here — READ-ONLY (SELECT only).
public sealed class ChartNoteReadQueries : IChartNoteReadQueries
{
    // Guard against a malformed/circular NextPtr chain.
    private const int MaxChunks = 1000;

    private readonly ISourceDbConnectionFactory _sourceDb;
    private readonly ILogger<ChartNoteReadQueries> _logger;

    public ChartNoteReadQueries(
        ISourceDbConnectionFactory sourceDb,
        ILogger<ChartNoteReadQueries> logger)
    {
        _sourceDb = sourceDb;
        _logger = logger;
    }

    public bool IsConfigured => _sourceDb.IsConfigured;

    public async Task<IReadOnlyList<(SourceChartNote Note, SourcePatient Patient)>> GetNewChartNotesAsync(
        long lastSeenId)
    {
        await using var conn = _sourceDb.Create();
        const string sql =
            """
            SELECT  cn.ID        AS Id,
                    cn.PatientID AS PatientId,
                    cn.DoctorID  AS DoctorId,
                    cn.NoteDate  AS NoteDate,
                    cn.SOAPPtr   AS SoapPtr,
                    cn.Status    AS Status,
                    p.ID         AS Id,
                    p.FirstName  AS FirstName,
                    p.LastName   AS LastName
            FROM    dbo.ChartNotes cn
            JOIN    dbo.Patients p ON p.ID = cn.PatientID
            WHERE   cn.ID > @lastSeenId
              AND   cn.SOAPPtr <> 0   -- skip empty placeholder notes; ChiroTouch UI hides these
            ORDER BY cn.ID ASC;
            """;

        var rows = await conn.QueryAsync<SourceChartNote, SourcePatient, (SourceChartNote, SourcePatient)>(
            sql,
            (note, patient) => (note, patient),
            new { lastSeenId },
            splitOn: "Id");

        return rows.ToList();
    }

    public async Task<IReadOnlyList<(SourceChartNote Note, SourcePatient Patient)>> GetAllChartNotesForPatientAsync(
        int patientId)
    {
        await using var conn = _sourceDb.Create();
        const string sql =
            """
            SELECT  cn.ID        AS Id,
                    cn.PatientID AS PatientId,
                    cn.DoctorID  AS DoctorId,
                    cn.NoteDate  AS NoteDate,
                    cn.SOAPPtr   AS SoapPtr,
                    cn.Status    AS Status,
                    p.ID         AS Id,
                    p.FirstName  AS FirstName,
                    p.LastName   AS LastName
            FROM    dbo.ChartNotes cn
            JOIN    dbo.Patients p ON p.ID = cn.PatientID
            WHERE   cn.PatientID = @patientId
              AND   cn.SOAPPtr <> 0   -- skip empty placeholder notes; ChiroTouch UI hides these
            ORDER BY cn.ID ASC;
            """;

        var rows = await conn.QueryAsync<SourceChartNote, SourcePatient, (SourceChartNote, SourcePatient)>(
            sql,
            (note, patient) => (note, patient),
            new { patientId },
            splitOn: "Id");

        return rows.ToList();
    }

    public async Task<string?> GetNoteRtfAsync(int soapPtr)
    {
        await using var conn = _sourceDb.Create();
        var sb = new StringBuilder();
        int ptr = soapPtr;
        int guard = 0;

        while (ptr != 0 && guard++ < MaxChunks)
        {
            // QueryFirst (not QuerySingle): a Ptr can resolve to >1 row in the chain;
            // taking the first avoids throwing mid-walk.
            var chunk = await conn.QueryFirstOrDefaultAsync<SourceChartText>(
                "SELECT Ptr AS Ptr, TextBody AS TextBody, NextPtr AS NextPtr FROM dbo.ChartText WHERE Ptr = @ptr;",
                new { ptr });

            if (chunk is null)
                break;

            sb.Append(chunk.TextBody);
            ptr = chunk.NextPtr;
        }

        if (guard >= MaxChunks)
            _logger.LogWarning("ChartText chain from Ptr {SoapPtr} hit the {Max}-chunk guard.",
                soapPtr, MaxChunks);

        return sb.Length > 0 ? sb.ToString() : null;
    }
}
