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
    DateTime? CurInjuryDate,
    // PSChiro AccountNo — the human-facing patient ID the team uses (distinct
    // from the internal PK). Shown bold in the portal header to match CT records.
    int? AccountNo);

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
    int VisitsSameDay,
    // When the note was signed (CN signature in dbo.Signatures). Null for the
    // rare unsigned note. ChiroTouch shows this as the "Signed: …" line.
    DateTime? SignedAt);

public sealed record VisitDiagnosisRow(int AppointmentId, string Code, string? Description);

// One unbilled service charge on a visit — what "Bill now" would mark billed.
public sealed record UnbilledChargeRow(int Id, string? Code, string? Description, decimal Amount);

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

// A visit where charges were billed but no corresponding ChartNote exists.
// Used by the missing-note detection surfaced on the workflow dashboard.
public sealed record MissingNoteVisitRow(
    int AppointmentId,
    DateTime VisitDate,
    int DoctorId,
    string? DoctorName,
    int ChargeCount);

public interface IPatientDetailQueries
{
    bool IsConfigured { get; }
    Task<PatientDemographics?> GetDemographicsAsync(int patientId);
    Task<IReadOnlyList<InsurancePolicyRow>> GetPoliciesAsync(int patientId);
    Task<IReadOnlyList<ChartNoteRow>> GetRecentNotesAsync(int patientId, int take);
    Task<IReadOnlyList<ChargeRow>> GetChargesForVisitsAsync(IEnumerable<int> appointmentIds);
    // Every billable service charge on a calendar date for one patient,
    // regardless of Appointment. Scrubber needs the WHOLE day's bill — PSChiro
    // can split one claim across two Appointments (e.g. E/M + treatment).
    Task<IReadOnlyList<ChargeRow>> GetChargesForPatientOnDateAsync(int patientId, DateTime date);
    // Count of billable (patient, date, doctor) tuples that have Service
    // charges billed but NO matching ChartNote. Keyed by PatientID for batching.
    Task<IReadOnlyDictionary<int, int>> GetMissingNoteVisitCountsAsync(IEnumerable<int> patientIds);
    // Detail rows for one patient — the (date, doctor) pairs that have charges
    // but no note. Drives the patient detail page's drill-in.
    Task<IReadOnlyList<MissingNoteVisitRow>> GetMissingNoteVisitsForPatientAsync(int patientId);
    // Read-only preview of the charges "Bill now" would mark billed for a visit —
    // the same unbilled-service-charge set BillChargesService.BillVisitAsync writes.
    Task<IReadOnlyList<UnbilledChargeRow>> GetUnbilledChargesForVisitAsync(int patientId, int appointmentId);
    // Per-visit diagnosis sets, keyed by appointment, Seq-ordered. Used by the
    // portal detail view to mirror ChiroTouch's per-note DX panel.
    Task<IReadOnlyList<VisitDiagnosisRow>> GetDiagnosesForVisitsAsync(IEnumerable<int> appointmentIds);
    // Signed timestamp (CN signature) per chart note id — the real "when it came
    // in" clock, since ChartNotes.NoteDate is a date only (always midnight).
    Task<IReadOnlyDictionary<int, DateTime>> GetSignedTimesAsync(IEnumerable<int> chartNoteIds);
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
    // Insurance balance per patient — total still owed. Computed as
    // SUM(charges) - SUM(payments) - SUM(adjustments) = full account balance.
    // Exact for Auto/PIP (patient owes nothing); for self-pay/post-EOB residuals
    // it also includes the patient side — proper split tracked via task #54.
    Task<IReadOnlyDictionary<int, decimal>> GetInsuranceBalancesAsync(
        IEnumerable<int> patientIds);
    // PSChiro account numbers keyed by Patients.ID. Surfaced on the workflow
    // dashboard because the team identifies patients by AccountNo, not the
    // internal PK. Patients with NULL AccountNo are simply absent from the map.
    Task<IReadOnlyDictionary<int, int>> GetAccountNumbersAsync(
        IEnumerable<int> patientIds);
    // Canonical office label per patient, resolved from the patient's primary
    // provider's facility street (see OfficeResolver). Keyed by Patients.ID.
    Task<IReadOnlyDictionary<int, string>> GetOfficesAsync(
        IEnumerable<int> patientIds);
    // Visits with at least one unbilled service charge. Scopes the scrubber's
    // note bundle + DX list to exactly what's about to bill. Date-only so the
    // caller can match against DoctorNote.NoteDate.Date for note linkage.
    Task<IReadOnlyList<UnbilledVisit>> GetUnbilledVisitsAsync(int patientId);
    // All visits on or after sinceDate — fallback scrub scope when there are no
    // unbilled visits (charges not entered yet, or case caught up). Pass
    // CurInjuryDate to anchor to the active case window.
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
                   p.CurInjuryDate AS CurInjuryDate,
                   p.AccountNo     AS AccountNo
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

    public async Task<IReadOnlyDictionary<int, DateTime>> GetSignedTimesAsync(IEnumerable<int> chartNoteIds)
    {
        var ids = chartNoteIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, DateTime>();

        await using var conn = _sourceDb.Create();
        // Earliest CN signature per note = the signed-off time. SigTimestamp
        // carries the real clock; NoteDate is midnight-only.
        var rows = await conn.QueryAsync<(int ChartNoteId, DateTime SignedAt)>(
            """
            SELECT s.SigTypeID AS ChartNoteId, MIN(s.SigTimestamp) AS SignedAt
            FROM   dbo.Signatures s
            WHERE  s.SigType = 'CN'
              AND  s.SigTypeID IN @ids
              AND  s.SigTimestamp IS NOT NULL
            GROUP BY s.SigTypeID;
            """,
            new { ids });
        return rows.ToDictionary(r => r.ChartNoteId, r => r.SignedAt);
    }

    public async Task<IReadOnlyList<VisitDiagnosisRow>> GetDiagnosesForVisitsAsync(IEnumerable<int> appointmentIds)
    {
        var ids = appointmentIds.Distinct().ToList();
        if (ids.Count == 0) return Array.Empty<VisitDiagnosisRow>();

        await using var conn = _sourceDb.Create();
        // Diagnoses are keyed by AppointmentID; Seq is ChiroTouch's on-screen
        // priority order. Scoping to the matched visit (not the patient-wide
        // union) keeps the list identical to ChiroTouch's per-note DX panel.
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

    public async Task<IReadOnlyDictionary<int, int>> GetMissingNoteVisitCountsAsync(IEnumerable<int> patientIds)
    {
        // Batch count of DISTINCT (patient, visit-date, doctor) tuples that have
        // service charges billed but no matching ChartNote — dashboard decorates
        // every row in one round-trip.
        var ids = patientIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, int>();

        await using var conn = _sourceDb.Create();
        const string sql =
            """
            WITH BillableVisits AS (
                SELECT DISTINCT
                       a.PatientID,
                       CAST(a.ScheduleDateTime AS date) AS VisitDate,
                       a.DoctorID
                FROM   dbo.Appointments a
                JOIN   dbo.Transactions t ON t.ApptID = a.ID
                WHERE  a.PatientID IN @ids
                  AND  t.TranType    = 'C'
                  AND  t.TranSubType = 'SV'
                  AND  t.Code IS NOT NULL AND t.Code <> ''
            ),
            NoteCoverage AS (
                SELECT DISTINCT
                       cn.PatientID,
                       CAST(cn.NoteDate AS date) AS NoteDate,
                       cn.DoctorID
                FROM   dbo.ChartNotes cn
                WHERE  cn.PatientID IN @ids
                  AND  cn.SOAPPtr <> 0
            )
            SELECT bv.PatientID AS PatientId, COUNT(*) AS MissingCount
            FROM   BillableVisits bv
            LEFT JOIN NoteCoverage nc
                   ON nc.PatientID = bv.PatientID
                  AND nc.NoteDate  = bv.VisitDate
                  AND nc.DoctorID  = bv.DoctorID
            WHERE  nc.PatientID IS NULL
            GROUP BY bv.PatientID;
            """;
        var rows = await conn.QueryAsync<(int PatientId, int MissingCount)>(sql, new { ids });
        return rows.ToDictionary(r => r.PatientId, r => r.MissingCount);
    }

    public async Task<IReadOnlyList<MissingNoteVisitRow>> GetMissingNoteVisitsForPatientAsync(int patientId)
    {
        // Per-visit detail: which billed Appointments have no matching ChartNote
        // by that same doctor on that date. Clickable gap list for the biller.
        await using var conn = _sourceDb.Create();
        const string sql =
            """
            WITH BillableVisits AS (
                SELECT a.ID              AS AppointmentId,
                       a.PatientID,
                       CAST(a.ScheduleDateTime AS date) AS VisitDate,
                       a.DoctorID,
                       COUNT(*)          AS ChargeCount
                FROM   dbo.Appointments a
                JOIN   dbo.Transactions t ON t.ApptID = a.ID
                WHERE  a.PatientID = @patientId
                  AND  t.TranType    = 'C'
                  AND  t.TranSubType = 'SV'
                  AND  t.Code IS NOT NULL AND t.Code <> ''
                GROUP BY a.ID, a.PatientID, CAST(a.ScheduleDateTime AS date), a.DoctorID
            ),
            NoteCoverage AS (
                SELECT DISTINCT
                       cn.PatientID,
                       CAST(cn.NoteDate AS date) AS NoteDate,
                       cn.DoctorID
                FROM   dbo.ChartNotes cn
                WHERE  cn.PatientID = @patientId
                  AND  cn.SOAPPtr <> 0
            )
            SELECT bv.AppointmentId AS AppointmentId,
                   bv.VisitDate     AS VisitDate,
                   bv.DoctorID      AS DoctorId,
                   COALESCE(d.LastName + ISNULL(', ' + d.FirstName, ''), CAST(bv.DoctorID AS varchar(10))) AS DoctorName,
                   bv.ChargeCount   AS ChargeCount
            FROM   BillableVisits bv
            LEFT JOIN NoteCoverage nc
                   ON nc.PatientID = bv.PatientID
                  AND nc.NoteDate  = bv.VisitDate
                  AND nc.DoctorID  = bv.DoctorID
            LEFT JOIN dbo.Doctors d ON d.ID = bv.DoctorID
            WHERE  nc.PatientID IS NULL
            ORDER BY bv.VisitDate DESC, bv.DoctorID;
            """;
        return (await conn.QueryAsync<MissingNoteVisitRow>(sql, new { patientId })).ToList();
    }

    public async Task<IReadOnlyList<ChargeRow>> GetChargesForPatientOnDateAsync(int patientId, DateTime date)
    {
        // Widened variant of GetChargesForVisitsAsync: same TranType='C'/
        // TranSubType='SV' filter and FOR XML PATH ChargeDxs aggregation, but
        // keyed by patient + calendar date instead of appointment IDs.
        var dayStart = date.Date;
        var dayEnd = dayStart.AddDays(1);
        await using var conn = _sourceDb.Create();
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
            WHERE  t.PatID       = @patientId
              AND  t.TranDate   >= @dayStart
              AND  t.TranDate   <  @dayEnd
              AND  t.TranType    = 'C'
              AND  t.TranSubType = 'SV'
              AND  t.Code IS NOT NULL AND t.Code <> ''
            ORDER BY t.TranDate DESC, t.ID;
            """;
        return (await conn.QueryAsync<ChargeRow>(sql, new { patientId, dayStart, dayEnd })).ToList();
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

    public async Task<IReadOnlyList<UnbilledChargeRow>> GetUnbilledChargesForVisitAsync(int patientId, int appointmentId)
    {
        await using var conn = _sourceDb.Create();
        // Mirrors BillChargesService's unbilled filter exactly (LEFT JOIN
        // BilledCharges + IS NULL) so the confirm dialog shows precisely what the
        // POST will bill. Read-only — no writes.
        const string sql =
            """
            SELECT t.ID          AS Id,
                   t.Code        AS Code,
                   t.Description  AS Description,
                   t.TranAmt      AS Amount
            FROM   dbo.Transactions t
            LEFT JOIN dbo.BilledCharges bc ON bc.ChargeTranID = t.ID
            WHERE  t.PatID       = @patientId
              AND  t.ApptID      = @appointmentId
              AND  t.TranType    = 'C'
              AND  t.TranSubType = 'SV'
              AND  bc.ID IS NULL
            ORDER BY t.ID;
            """;
        return (await conn.QueryAsync<UnbilledChargeRow>(sql, new { patientId, appointmentId })).ToList();
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
                        AND CAST(a2.ScheduleDateTime AS date) = CAST(cn.NoteDate AS date)) AS VisitsSameDay,
                   (SELECT MIN(s.SigTimestamp) FROM dbo.Signatures s
                      WHERE s.SigType = 'CN' AND s.SigTypeID = cn.ID) AS SignedAt
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
        // A charge is outstanding when its service Transaction has no
        // BilledCharges row. NOT EXISTS rides the indexed
        // BilledCharges.ChargeTranID; the PatientID filter bounds the scan.
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

    public async Task<IReadOnlyDictionary<int, decimal>> GetInsuranceBalancesAsync(
        IEnumerable<int> patientIds)
    {
        var ids = patientIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<int, decimal>();

        await using var conn = _sourceDb.Create();
        // Each charge row tracks paid + adjusted amounts directly on its
        // record. Per-charge balance = TranAmt minus everything that's been
        // applied. Uses Transactions.PatID directly (NOT Appointments.PatientID
        // via JOIN) — avoids dropping rows without an ApptID. Validated
        // against ChiroTouch ledger footer for Shawn Willoughby: this returns
        // $88,515 vs CT's $88,370 (~0.16% off — one re-exam line, likely
        // special CT handling — chase via task #54).
        const string sql =
            """
            SELECT PatID AS PatientId,
                   COALESCE(SUM(
                     TranAmt
                     - ISNULL(PriPaidAmt,        0)
                     - ISNULL(SecPaidAmt,        0)
                     - ISNULL(PatPaidAmt,        0)
                     - ISNULL(WOAmt,             0)
                     - ISNULL(DiscountAmt,       0)
                     - ISNULL(PatientAdjustment, 0)
                     - ISNULL(PatientOther,      0)
                   ), 0) AS Balance
            FROM   dbo.Transactions
            WHERE  PatID IN @ids
              AND  TranType    = 'C'
              AND  TranSubType = 'SV'
            GROUP BY PatID;
            """;

        var rows = await conn.QueryAsync<(int PatientId, decimal Balance)>(sql, new { ids });
        return rows.ToDictionary(r => r.PatientId, r => r.Balance);
    }

    public async Task<IReadOnlyDictionary<int, int>> GetAccountNumbersAsync(
        IEnumerable<int> patientIds)
    {
        var ids = patientIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, int>();

        await using var conn = _sourceDb.Create();
        var rows = await conn.QueryAsync<(int PatientId, int? AccountNo)>(
            "SELECT ID AS PatientId, AccountNo FROM dbo.Patients WHERE ID IN @ids;",
            new { ids });
        return rows.Where(r => r.AccountNo.HasValue)
                   .ToDictionary(r => r.PatientId, r => r.AccountNo!.Value);
    }

    public async Task<IReadOnlyDictionary<int, string>> GetOfficesAsync(IEnumerable<int> patientIds)
    {
        var ids = patientIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, string>();

        await using var conn = _sourceDb.Create();
        // Office is encoded in the provider's credential suffix (e.g. "…, DC, CC"
        // = Center City), so we attribute via the patient's primary provider name
        // (Patients.DoctorID → Doctors.FullName) and decode with OfficeResolver.
        var rows = await conn.QueryAsync<(int PatientId, string? ProviderName)>(
            """
            SELECT p.ID AS PatientId, d.FullName AS ProviderName
            FROM   dbo.Patients p
            LEFT JOIN dbo.Doctors d ON d.ID = p.DoctorID
            WHERE  p.ID IN @ids;
            """,
            new { ids });
        return rows.ToDictionary(r => r.PatientId, r => OfficeResolver.Resolve(r.ProviderName));
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
        // All visits (any billing state) on or after sinceDate. Reuses the
        // UnbilledVisit shape — only AppointmentId + VisitDate matter downstream.
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
