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

    // Current source-of-truth "head" for a set of chart notes: the LIVE SOAPPtr
    // and the CN signed time. The whole staleness story turns on SOAPPtr —
    // ChiroTouch repoints it when a note is edited/finalized, but the note's ID
    // (our sync + idempotency key) never changes. Comparing our stored SoapPtr
    // to this current value is how we detect a note that changed under us.
    // SignedAt is the MIN CN SigTimestamp (ChiroTouch's "Signed: …" clock).
    // Notes absent from ChiroTouch are simply omitted from the result.
    Task<IReadOnlyDictionary<int, ChartNoteHead>> GetNoteHeadsAsync(IEnumerable<int> chartNoteIds);

    // CN signatures whose timestamp is strictly greater than the cursor — the
    // incremental "a note was just signed or re-signed" feed. Every note has
    // exactly one CN row, updated in place on re-sign, so this catches both a
    // first signing and a later re-sign. Ordered oldest-first; the caller
    // advances its cursor to the newest timestamp it processed.
    Task<IReadOnlyList<SignatureChange>> GetSignaturesChangedSinceAsync(DateTime since);

    // Newest CN signature time in ChiroTouch — used to seed the signature
    // cursor on first run so we don't replay the entire historical backlog
    // (that's the retroactive sweep's job).
    Task<DateTime?> GetMaxSignatureTimeAsync();
}

// One changed CN signature: the note it belongs to and when it was signed.
public sealed record SignatureChange(int ChartNoteId, DateTime SignedAt);

// Live change-detection fields for one chart note (see GetNoteHeadsAsync).
public sealed record ChartNoteHead(int ChartNoteId, int? SoapPtr, DateTime? SignedAt);

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

    public async Task<IReadOnlyDictionary<int, ChartNoteHead>> GetNoteHeadsAsync(IEnumerable<int> chartNoteIds)
    {
        var ids = chartNoteIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, ChartNoteHead>();

        await using var conn = _sourceDb.Create();
        // LEFT JOIN the earliest CN signature so unsigned notes still return a
        // row (SignedAt null). No SOAPPtr<>0 filter here: a note that's been
        // blanked back to a placeholder is itself a change we want to see.
        const string sql =
            """
            SELECT cn.ID AS ChartNoteId,
                   cn.SOAPPtr AS SoapPtr,
                   sig.SignedAt AS SignedAt
            FROM   dbo.ChartNotes cn
            OUTER APPLY (
                SELECT MIN(s.SigTimestamp) AS SignedAt
                FROM   dbo.Signatures s
                WHERE  s.SigType = 'CN' AND s.SigTypeID = cn.ID
            ) sig
            WHERE  cn.ID IN @ids;
            """;
        var rows = await conn.QueryAsync<ChartNoteHead>(sql, new { ids });
        return rows.ToDictionary(r => r.ChartNoteId);
    }

    public async Task<IReadOnlyList<SignatureChange>> GetSignaturesChangedSinceAsync(DateTime since)
    {
        await using var conn = _sourceDb.Create();
        const string sql =
            """
            SELECT s.SigTypeID AS ChartNoteId, s.SigTimestamp AS SignedAt
            FROM   dbo.Signatures s
            WHERE  s.SigType = 'CN' AND s.SigTimestamp > @since
            ORDER BY s.SigTimestamp ASC;
            """;
        var rows = await conn.QueryAsync<SignatureChange>(sql, new { since });
        return rows.ToList();
    }

    public async Task<DateTime?> GetMaxSignatureTimeAsync()
    {
        await using var conn = _sourceDb.Create();
        return await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT MAX(SigTimestamp) FROM dbo.Signatures WHERE SigType = 'CN';");
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
