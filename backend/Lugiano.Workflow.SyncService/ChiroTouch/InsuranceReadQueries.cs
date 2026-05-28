using Dapper;
using Lugiano.Workflow.SyncService.ChiroTouch.Models;

namespace Lugiano.Workflow.SyncService.ChiroTouch;

public interface IInsuranceReadQueries
{
    // Whether the ChiroTouch connection string is configured.
    bool IsConfigured { get; }

    // New insurance policies (joined to the patient) with ID greater than the cursor.
    Task<IReadOnlyList<(SourceInsurancePolicy Policy, SourcePatient Patient)>> GetNewPoliciesAsync(
        long lastSeenId);
}

// All ChiroTouch insurance SQL lives here — READ-ONLY (SELECT only).
public sealed class InsuranceReadQueries : IInsuranceReadQueries
{
    private readonly ISourceDbConnectionFactory _sourceDb;

    public InsuranceReadQueries(ISourceDbConnectionFactory sourceDb) => _sourceDb = sourceDb;

    public bool IsConfigured => _sourceDb.IsConfigured;

    public async Task<IReadOnlyList<(SourceInsurancePolicy Policy, SourcePatient Patient)>> GetNewPoliciesAsync(
        long lastSeenId)
    {
        await using var conn = _sourceDb.Create();
        const string sql =
            """
            SELECT  ip.ID              AS Id,
                    ip.PatientID       AS PatientId,
                    ip.CoverageType    AS CoverageType,
                    ip.InsCoName       AS InsCoName,
                    ip.EffectiveDate   AS EffectiveDate,
                    ip.TerminationDate AS TerminationDate,
                    ip.Hidden          AS Hidden,
                    p.ID               AS Id,
                    p.FirstName        AS FirstName,
                    p.LastName         AS LastName
            FROM    dbo.InsPolicies ip
            JOIN    dbo.Patients p ON p.ID = ip.PatientID
            WHERE   ip.ID > @lastSeenId
              AND   ip.Hidden = 0
            ORDER BY ip.ID ASC;
            """;

        var rows = await conn.QueryAsync<SourceInsurancePolicy, SourcePatient, (SourceInsurancePolicy, SourcePatient)>(
            sql,
            (policy, patient) => (policy, patient),
            new { lastSeenId },
            splitOn: "Id");

        return rows.ToList();
    }
}
