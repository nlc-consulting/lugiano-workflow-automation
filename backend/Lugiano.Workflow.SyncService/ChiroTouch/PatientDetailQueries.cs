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

public sealed record ChargeRow(
    int Id,
    int? AppointmentId,
    DateTime? Date,
    string? Code,
    string? Description,
    decimal Amount,
    string? Modifier1,
    string? Modifier2,
    // Comma-separated ICD codes billed against this charge, ordered by pointer
    // sequence. The alignment between this and the matching note's narrative is
    // the AI scrubber's central check.
    string? Diagnoses);

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

public sealed record PatientDiagnosisRow(string Code, string? Description);

public interface IPatientDetailQueries
{
    bool IsConfigured { get; }
    Task<PatientDemographics?> GetDemographicsAsync(int patientId);
    Task<IReadOnlyList<InsurancePolicyRow>> GetPoliciesAsync(int patientId);
    Task<IReadOnlyList<ChartNoteRow>> GetRecentNotesAsync(int patientId, int take);
    Task<IReadOnlyList<ChargeRow>> GetChargesForVisitsAsync(IEnumerable<int> appointmentIds);
    Task<IReadOnlyList<PatientDiagnosisRow>> GetPatientDiagnosesAsync(int patientId);
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

    public async Task<IReadOnlyList<PatientDiagnosisRow>> GetPatientDiagnosesAsync(int patientId)
    {
        await using var conn = _sourceDb.Create();
        // Diagnoses rows are keyed by AppointmentID; PatientID on the row is
        // typically NULL. Join through Appointments to get the patient's full
        // active diagnosis set. DISTINCT collapses duplicates across visits.
        const string sql =
            """
            SELECT DISTINCT d.Code AS Code, d.Description AS Description
            FROM   dbo.Diagnoses d
            JOIN   dbo.Appointments a ON a.ID = d.AppointmentID
            WHERE  a.PatientID = @patientId
            ORDER BY d.Code;
            """;
        return (await conn.QueryAsync<PatientDiagnosisRow>(sql, new { patientId })).ToList();
    }

    public async Task<IReadOnlyList<ChargeRow>> GetChargesForVisitsAsync(IEnumerable<int> appointmentIds)
    {
        var ids = appointmentIds.Distinct().ToList();
        if (ids.Count == 0) return Array.Empty<ChargeRow>();

        await using var conn = _sourceDb.Create();
        // Service charges only (TranType='C', TranSubType='SV') — Transactions is a
        // mixed ledger that also holds payments. ChargeDxs.ChargeItemID is actually
        // Transactions.ID (misleadingly named on the source side). The FOR XML PATH
        // trick is the SQL Server 2008-compat way to inline-aggregate the per-charge
        // diagnosis list (PSChiro runs at compat level 100, so STRING_AGG isn't
        // available even on the 2017 instance).
        const string sql =
            """
            SELECT t.ID          AS Id,
                   t.ApptID      AS AppointmentId,
                   t.TranDate    AS Date,
                   t.Code        AS Code,
                   t.Description AS Description,
                   t.TranAmt     AS Amount,
                   cd.M1         AS Modifier1,
                   cd.M2         AS Modifier2,
                   STUFF((SELECT ', ' + dx.DxCode
                          FROM   dbo.ChargeDxs dx
                          WHERE  dx.ChargeItemID = t.ID
                          ORDER BY dx.Seq
                          FOR XML PATH('')), 1, 2, '') AS Diagnoses
            FROM   dbo.Transactions t
            LEFT JOIN dbo.ChargeDetails cd ON cd.ChargeTranID = t.ID
            WHERE  t.ApptID IN @ids
              AND  t.TranType    = 'C'
              AND  t.TranSubType = 'SV'
            ORDER BY t.TranDate DESC, t.ID;
            """;
        return (await conn.QueryAsync<ChargeRow>(sql, new { ids })).ToList();
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
