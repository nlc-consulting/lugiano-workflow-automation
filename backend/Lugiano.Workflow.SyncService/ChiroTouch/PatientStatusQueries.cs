using Dapper;

namespace Lugiano.Workflow.SyncService.ChiroTouch;

// The full, current billing-readiness truth for one patient, read straight from
// ChiroTouch. Used to RECONCILE a case whenever any trigger fires — so a note
// trigger also picks up insurance the patient had since intake, and vice versa.
public sealed record PatientStatus(
    bool HasInsurance,
    DateTime? InsuranceEffectiveDate,
    bool HasNotes,
    DateTime? LatestNoteDate);

public interface IPatientStatusQueries
{
    bool IsConfigured { get; }
    Task<PatientStatus> GetStatusAsync(int patientId);
}

// READ-ONLY reconciliation reads from ChiroTouch (lugiano_ro). SELECT only.
public sealed class PatientStatusQueries : IPatientStatusQueries
{
    private readonly ISourceDbConnectionFactory _sourceDb;

    public PatientStatusQueries(ISourceDbConnectionFactory sourceDb) => _sourceDb = sourceDb;

    public bool IsConfigured => _sourceDb.IsConfigured;

    // Insurance is typically entered at intake (early/low IDs); notes come later.
    // So we don't look at "recently added" rows — we aggregate the whole patient:
    //   - earliest non-hidden policy EffectiveDate = when coverage truly began
    //   - latest ChartNote NoteDate = most recent clinical activity
    public async Task<PatientStatus> GetStatusAsync(int patientId)
    {
        await using var conn = _sourceDb.Create();
        const string sql =
            """
            SELECT
                (SELECT COUNT(1)        FROM dbo.InsPolicies WHERE PatientID = @patientId AND Hidden = 0) AS PolicyCount,
                (SELECT MIN(EffectiveDate) FROM dbo.InsPolicies WHERE PatientID = @patientId AND Hidden = 0) AS InsuranceEffectiveDate,
                (SELECT COUNT(1)        FROM dbo.ChartNotes  WHERE PatientID = @patientId) AS NoteCount,
                (SELECT MAX(NoteDate)   FROM dbo.ChartNotes  WHERE PatientID = @patientId) AS LatestNoteDate;
            """;

        var row = await conn.QuerySingleAsync<StatusRow>(sql, new { patientId });
        return new PatientStatus(
            HasInsurance: row.PolicyCount > 0,
            InsuranceEffectiveDate: row.InsuranceEffectiveDate,
            HasNotes: row.NoteCount > 0,
            LatestNoteDate: row.LatestNoteDate);
    }

    // Dapper materialization target (counts come back as ints, not bools).
    private sealed record StatusRow(
        int PolicyCount,
        DateTime? InsuranceEffectiveDate,
        int NoteCount,
        DateTime? LatestNoteDate);
}
