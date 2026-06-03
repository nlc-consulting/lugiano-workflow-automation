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
    int? SoapPtr,
    // Visit linkage: ChartNotes has no AppointmentID, so we match by
    // (PatientID, same calendar date). VisitsSameDay surfaces ambiguity
    // (>1 = uncertain match, 0 = no matching appointment found).
    int? VisitId,
    DateTime? VisitTime,
    DateTime? VisitCheckIn,
    DateTime? VisitCheckOut,
    string? VisitDoctor,
    int VisitsSameDay);

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
        // Notes are joined to their visit by (PatientID, same calendar date).
        // OUTER APPLY picks the most likely match when more than one appointment
        // sits on the same day (preferring checked-out > checked-in > closest
        // scheduled time). VisitsSameDay reports the candidate count so the UI
        // can flag ambiguous (>1) or missing (=0) matches.
        const string sql =
            """
            SELECT TOP (@take)
                   cn.ID       AS Id,
                   cn.NoteDate AS NoteDate,
                   d.FullName  AS Doctor,
                   cn.Status   AS Status,
                   cn.SOAPPtr  AS SoapPtr,
                   v.AppointmentID    AS VisitId,
                   v.ScheduleDateTime AS VisitTime,
                   v.CheckInDateTime  AS VisitCheckIn,
                   v.CheckOutDateTime AS VisitCheckOut,
                   v.VisitDoctor      AS VisitDoctor,
                   (SELECT COUNT(*) FROM dbo.Appointments a2
                      WHERE a2.PatientID = cn.PatientID
                        AND CAST(a2.ScheduleDateTime AS date) = CAST(cn.NoteDate AS date)) AS VisitsSameDay
            FROM   dbo.ChartNotes cn
            LEFT JOIN dbo.Doctors d ON d.ID = cn.DoctorID
            OUTER APPLY (
                SELECT TOP 1
                       a.ID                AS AppointmentID,
                       a.ScheduleDateTime,
                       a.CheckInDateTime,
                       a.CheckOutDateTime,
                       ad.FullName         AS VisitDoctor
                FROM   dbo.Appointments a
                LEFT JOIN dbo.Doctors ad ON ad.ID = a.DoctorID
                WHERE  a.PatientID = cn.PatientID
                  AND  CAST(a.ScheduleDateTime AS date) = CAST(cn.NoteDate AS date)
                ORDER BY
                    CASE WHEN a.CheckOutDateTime IS NOT NULL THEN 0 ELSE 1 END,
                    CASE WHEN a.CheckInDateTime  IS NOT NULL THEN 0 ELSE 1 END,
                    ABS(DATEDIFF(MINUTE, a.ScheduleDateTime, cn.NoteDate))
            ) v
            WHERE  cn.PatientID = @patientId
            ORDER BY cn.NoteDate DESC, cn.ID DESC;
            """;
        return (await conn.QueryAsync<ChartNoteRow>(sql, new { patientId, take })).ToList();
    }
}
