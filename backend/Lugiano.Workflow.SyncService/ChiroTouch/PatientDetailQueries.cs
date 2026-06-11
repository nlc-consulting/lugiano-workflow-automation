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
    string? PrimaryDoctor,
    // Patient-level case marker from PSChiro. Free-text values like "Auto",
    // "Auto 2" (second case), "Slip/Fall", "WC", etc. — matches the "(Auto)"
    // tag shown next to the patient name in ChiroTouch's UI.
    string? CaseType,
    // Accident / current-injury date for the active case. Source-of-truth for
    // "when did this case start" when surfacing the case context to reviewers.
    DateTime? CurInjuryDate);

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

public sealed record VisitDiagnosisRow(int AppointmentId, string Code, string? Description);

public sealed record PatientDiagnosisRow(string Code, string? Description);

public sealed record PatientListRow(
    int Id,
    string? FirstName,
    string? LastName,
    int? AccountNo,
    string? Sex,
    string? City,
    string? State,
    string? PrimaryDoctor);

// Per-patient roll-up of service charges that haven't been billed yet (no
// BilledCharges row exists for the underlying Transaction). The dashboard's
// third billing-readiness gate.
public sealed record OutstandingChargesSummary(
    int Count,
    decimal TotalAmount,
    DateTime? OldestChargeDate);

// One unbilled visit for a patient. AppointmentId is the PSChiro Appointment.ID;
// VisitDate is the date-only portion of ScheduleDateTime, used to match ChartNotes
// (which have no AppointmentID column) by PatientID + same calendar date.
public sealed record UnbilledVisit(int AppointmentId, DateTime VisitDate);

public interface IPatientDetailQueries
{
    bool IsConfigured { get; }
    Task<PatientDemographics?> GetDemographicsAsync(int patientId);
    Task<IReadOnlyList<InsurancePolicyRow>> GetPoliciesAsync(int patientId);
    Task<IReadOnlyList<ChartNoteRow>> GetRecentNotesAsync(int patientId, int take);
    Task<IReadOnlyList<ChargeRow>> GetChargesForVisitsAsync(IEnumerable<int> appointmentIds);
    // Per-visit diagnosis sets, keyed by appointment, Seq-ordered. Used by the
    // portal detail view to mirror ChiroTouch's per-note DX panel.
    Task<IReadOnlyList<VisitDiagnosisRow>> GetDiagnosesForVisitsAsync(IEnumerable<int> appointmentIds);
    // Patient-wide diagnosis union (every distinct DX code carried on any of
    // the patient's appointments). Used by the AI scrubber so it evaluates the
    // body of notes against the full diagnosis list, not just one visit's.
    Task<IReadOnlyList<PatientDiagnosisRow>> GetPatientDiagnosesAsync(int patientId);
    // Paginated patient lookup. q is matched as a prefix against FirstName/LastName/
    // AccountNo/ID so the search uses indexes; empty q returns the newest patients.
    Task<(IReadOnlyList<PatientListRow> Rows, int Total)> SearchPatientsAsync(
        string? q, bool includeInactive, int skip, int take);
    // Outstanding-charges roll-up for a batch of patients (used by the case
    // dashboard). Returns one entry per patient with at least one unbilled
    // charge; patients absent from the dictionary have nothing outstanding.
    Task<IReadOnlyDictionary<int, OutstandingChargesSummary>> GetOutstandingChargesAsync(
        IEnumerable<int> patientIds);
    // Visits (Appointments) for a single patient where at least one service
    // charge has not yet been billed. Used by the scrubber to scope the note
    // bundle and DX list to exactly what's about to bill — no patient-wide
    // overreach, no human-curated subset. Returns date-only so the caller can
    // match against DoctorNote.NoteDate.Date for note linkage.
    Task<IReadOnlyList<UnbilledVisit>> GetUnbilledVisitsAsync(int patientId);
    // All visits for a patient on or after the given date. Used as a fallback
    // scrub scope when there are no unbilled visits (charges not entered yet,
    // or case fully caught up) — we still want to scrub the current case's
    // documentation against its DX. Pass CurInjuryDate to anchor to the
    // active case window.
    Task<IReadOnlyList<UnbilledVisit>> GetVisitsSinceAsync(int patientId, DateTime sinceDate);
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
            SELECT p.ID            AS PatientId,
                   p.FirstName     AS FirstName,
                   p.MiddleName    AS MiddleName,
                   p.LastName      AS LastName,
                   p.Sex           AS Sex,
                   p.Address       AS Address,
                   p.City          AS City,
                   p.State         AS State,
                   p.Zip           AS Zip,
                   d.FullName      AS PrimaryDoctor,
                   p.CaseType      AS CaseType,
                   p.CurInjuryDate AS CurInjuryDate
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
        // typically NULL, so join through Appointments. DISTINCT collapses
        // duplicates a patient carries across multiple visits.
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

    public async Task<IReadOnlyList<VisitDiagnosisRow>> GetDiagnosesForVisitsAsync(IEnumerable<int> appointmentIds)
    {
        var ids = appointmentIds.Distinct().ToList();
        if (ids.Count == 0) return Array.Empty<VisitDiagnosisRow>();

        await using var conn = _sourceDb.Create();
        // Diagnoses are keyed by AppointmentID, mirroring ChiroTouch's chart-note
        // DX panel: each visit carries its own ordered set and Seq is the on-screen
        // priority order. Scoping to the note's matched visit — rather than the
        // patient-wide union — is what keeps the list identical to ChiroTouch and
        // free of codes carried only by other visits (e.g. a subsequent-encounter
        // sprain entered on a different day).
        const string sql =
            """
            SELECT d.AppointmentID AS AppointmentId,
                   d.Code          AS Code,
                   d.Description    AS Description
            FROM   dbo.Diagnoses d
            WHERE  d.AppointmentID IN @ids
            ORDER BY d.AppointmentID, d.Seq;
            """;
        return (await conn.QueryAsync<VisitDiagnosisRow>(sql, new { ids })).ToList();
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
                    -- Doctor identity beats everything else. NoteDate is often
                    -- date-only (midnight), so minute-distance would otherwise
                    -- pick the earliest visit even when a later visit was the
                    -- one the doctor wrote the note for.
                    CASE WHEN a.DoctorID = cn.DoctorID THEN 0 ELSE 1 END,
                    CASE WHEN a.CheckOutDateTime IS NOT NULL THEN 0 ELSE 1 END,
                    CASE WHEN a.CheckInDateTime  IS NOT NULL THEN 0 ELSE 1 END,
                    ABS(DATEDIFF(MINUTE, a.ScheduleDateTime, cn.NoteDate))
            ) v
            WHERE  cn.PatientID = @patientId
              AND  cn.SOAPPtr <> 0   -- skip empty placeholder notes; ChiroTouch UI hides these
            ORDER BY cn.NoteDate DESC, cn.ID DESC;
            """;
        return (await conn.QueryAsync<ChartNoteRow>(sql, new { patientId, take })).ToList();
    }

    public async Task<(IReadOnlyList<PatientListRow> Rows, int Total)> SearchPatientsAsync(
        string? q, bool includeInactive, int skip, int take)
    {
        await using var conn = _sourceDb.Create();
        var hasQuery = !string.IsNullOrWhiteSpace(q);
        var prefix = hasQuery ? q!.Trim() + "%" : null;

        // Prefix LIKE keeps the search index-friendly on FirstName/LastName/AccountNo.
        // ID is matched as a string prefix so "27" finds 2700-2799 — handy when the
        // user is looking up by partial PatientID.
        const string whereClause = """
            WHERE (@includeInactive = 1 OR p.InActive = 0)
              AND (
                @prefix IS NULL
                OR p.FirstName LIKE @prefix
                OR p.LastName  LIKE @prefix
                OR CAST(p.AccountNo AS varchar(20)) LIKE @prefix
                OR CAST(p.ID AS varchar(20)) LIKE @prefix
              )
            """;

        var totalSql = $"SELECT COUNT(*) FROM dbo.Patients p {whereClause};";
        var total = await conn.ExecuteScalarAsync<int>(totalSql,
            new { prefix, includeInactive = includeInactive ? 1 : 0 });

        // SQL Server 2008-compat paging via ROW_NUMBER (OFFSET/FETCH needs compat 110+).
        var pageSql = $"""
            SELECT Id, FirstName, LastName, AccountNo, Sex, City, State, PrimaryDoctor
            FROM (
              SELECT ROW_NUMBER() OVER (ORDER BY p.ID DESC) AS rn,
                     p.ID        AS Id,
                     p.FirstName AS FirstName,
                     p.LastName  AS LastName,
                     p.AccountNo AS AccountNo,
                     p.Sex       AS Sex,
                     p.City      AS City,
                     p.State     AS State,
                     d.FullName  AS PrimaryDoctor
              FROM   dbo.Patients p
              LEFT JOIN dbo.Doctors d ON d.ID = p.DoctorID
              {whereClause}
            ) x
            WHERE rn > @skip AND rn <= @skip + @take
            ORDER BY rn;
            """;

        var rows = (await conn.QueryAsync<PatientListRow>(pageSql,
            new { prefix, includeInactive = includeInactive ? 1 : 0, skip, take })).ToList();

        return (rows, total);
    }

    public async Task<IReadOnlyDictionary<int, OutstandingChargesSummary>> GetOutstandingChargesAsync(
        IEnumerable<int> patientIds)
    {
        var ids = patientIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<int, OutstandingChargesSummary>();

        await using var conn = _sourceDb.Create();
        // A charge is outstanding when its underlying service Transaction has
        // no BilledCharges row. The NOT EXISTS keeps the join cheap on the
        // indexed BilledCharges.ChargeTranID. Filtering Appointments by
        // PatientID first bounds the scan to this page of cases.
        const string sql =
            """
            SELECT a.PatientID AS PatientId,
                   t.TranAmt   AS Amount,
                   t.TranDate  AS TranDate
            FROM   dbo.Transactions t
            JOIN   dbo.Appointments a ON a.ID = t.ApptID
            WHERE  t.TranType = 'C'
              AND  t.TranSubType = 'SV'
              AND  a.PatientID IN @ids
              AND  NOT EXISTS (
                     SELECT 1 FROM dbo.BilledCharges bc
                     WHERE  bc.ChargeTranID = t.ID
                   );
            """;

        var rows = await conn.QueryAsync<(int PatientId, decimal Amount, DateTime? TranDate)>(sql, new { ids });

        return rows
            .GroupBy(r => r.PatientId)
            .ToDictionary(
                g => g.Key,
                g => new OutstandingChargesSummary(
                    Count: g.Count(),
                    TotalAmount: g.Sum(r => r.Amount),
                    OldestChargeDate: g.Min(r => r.TranDate)));
    }

    public async Task<IReadOnlyList<UnbilledVisit>> GetUnbilledVisitsAsync(int patientId)
    {
        await using var conn = _sourceDb.Create();
        // A visit is "unbilled" if it carries at least one CPT service charge
        // whose Transaction row has no BilledCharges entry. DISTINCT collapses
        // duplicate appointment IDs when a visit has multiple unbilled charges.
        const string sql =
            """
            SELECT DISTINCT
                   a.ID AS AppointmentId,
                   CAST(a.ScheduleDateTime AS date) AS VisitDate
            FROM   dbo.Transactions t
            JOIN   dbo.Appointments a ON a.ID = t.ApptID
            WHERE  t.TranType = 'C'
              AND  t.TranSubType = 'SV'
              AND  a.PatientID = @patientId
              AND  NOT EXISTS (
                     SELECT 1 FROM dbo.BilledCharges bc
                     WHERE  bc.ChargeTranID = t.ID
                   )
            ORDER BY VisitDate DESC;
            """;
        return (await conn.QueryAsync<UnbilledVisit>(sql, new { patientId })).ToList();
    }

    public async Task<IReadOnlyList<UnbilledVisit>> GetVisitsSinceAsync(int patientId, DateTime sinceDate)
    {
        await using var conn = _sourceDb.Create();
        // All visits (any billing state) for the patient on or after sinceDate.
        // Reuses the UnbilledVisit shape — only the AppointmentId + VisitDate
        // matter for downstream scope lookups.
        const string sql =
            """
            SELECT DISTINCT
                   a.ID AS AppointmentId,
                   CAST(a.ScheduleDateTime AS date) AS VisitDate
            FROM   dbo.Appointments a
            WHERE  a.PatientID = @patientId
              AND  CAST(a.ScheduleDateTime AS date) >= CAST(@sinceDate AS date)
            ORDER BY VisitDate DESC;
            """;
        return (await conn.QueryAsync<UnbilledVisit>(sql, new { patientId, sinceDate })).ToList();
    }
}
