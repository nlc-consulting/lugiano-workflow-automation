using Dapper;

namespace Lugiano.Workflow.SyncService.ChiroTouch;

public sealed record PatientDemographics(
    int PatientId,
    string? FirstName,
    string? MiddleName,
    string? LastName,
    string? Sex,
    string? Address,
    string? City,
    string? State,
    string? Zip,
    string? PrimaryDoctor);

public sealed record InsurancePolicyRow(
    int Id,
    string? Insurer,
    string? CoverageType,
    DateTime? EffectiveDate,
    DateTime? TerminationDate);

public sealed record ChartNoteRow(
    int Id,
    DateTime? NoteDate,
    string? Doctor,
    int? Status,
    int? SoapPtr);

public interface IPatientDetailQueries
{
    bool IsConfigured { get; }
    Task<PatientDemographics?> GetDemographicsAsync(int patientId);
    Task<IReadOnlyList<InsurancePolicyRow>> GetPoliciesAsync(int patientId);
    Task<IReadOnlyList<ChartNoteRow>> GetRecentNotesAsync(int patientId, int take);
}

// READ-ONLY patient detail reads from ChiroTouch (lugiano_ro). SELECT only.
public sealed class PatientDetailQueries : IPatientDetailQueries
{
    private readonly ISourceDbConnectionFactory _sourceDb;

    public PatientDetailQueries(ISourceDbConnectionFactory sourceDb) => _sourceDb = sourceDb;

    public bool IsConfigured => _sourceDb.IsConfigured;

    public async Task<PatientDemographics?> GetDemographicsAsync(int patientId)
    {
        await using var conn = _sourceDb.Create();
        const string sql =
            """
            SELECT p.ID        AS PatientId,
                   p.FirstName AS FirstName,
                   p.MiddleName AS MiddleName,
                   p.LastName  AS LastName,
                   p.Sex       AS Sex,
                   p.Address   AS Address,
                   p.City      AS City,
                   p.State     AS State,
                   p.Zip       AS Zip,
                   d.FullName  AS PrimaryDoctor
            FROM   dbo.Patients p
            LEFT JOIN dbo.Doctors d ON d.ID = p.DoctorID
            WHERE  p.ID = @patientId;
            """;
        return await conn.QuerySingleOrDefaultAsync<PatientDemographics>(sql, new { patientId });
    }

    public async Task<IReadOnlyList<InsurancePolicyRow>> GetPoliciesAsync(int patientId)
    {
        await using var conn = _sourceDb.Create();
        const string sql =
            """
            SELECT ID              AS Id,
                   InsCoName       AS Insurer,
                   CoverageType    AS CoverageType,
                   EffectiveDate   AS EffectiveDate,
                   TerminationDate AS TerminationDate
            FROM   dbo.InsPolicies
            WHERE  PatientID = @patientId AND Hidden = 0
            ORDER BY EffectiveDate DESC, ID DESC;
            """;
        return (await conn.QueryAsync<InsurancePolicyRow>(sql, new { patientId })).ToList();
    }

    public async Task<IReadOnlyList<ChartNoteRow>> GetRecentNotesAsync(int patientId, int take)
    {
        await using var conn = _sourceDb.Create();
        const string sql =
            """
            SELECT TOP (@take)
                   cn.ID       AS Id,
                   cn.NoteDate AS NoteDate,
                   d.FullName  AS Doctor,
                   cn.Status   AS Status,
                   cn.SOAPPtr  AS SoapPtr
            FROM   dbo.ChartNotes cn
            LEFT JOIN dbo.Doctors d ON d.ID = cn.DoctorID
            WHERE  cn.PatientID = @patientId
            ORDER BY cn.NoteDate DESC, cn.ID DESC;
            """;
        return (await conn.QueryAsync<ChartNoteRow>(sql, new { patientId, take })).ToList();
    }
}
