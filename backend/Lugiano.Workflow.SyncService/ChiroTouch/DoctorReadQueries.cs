using Dapper;

namespace Lugiano.Workflow.SyncService.ChiroTouch;

// Raw PSChiro doctor row. Credentials1/2 are joined into a single string by
// the sync service; Inactive is a bit we flip into our IsActive convention.
public sealed record DoctorRow(
    int Id,
    string? FullName,
    string? Credentials1,
    string? Credentials2,
    string? Npi,
    string? EmailAddress,
    bool Inactive);

public interface IDoctorReadQueries
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<DoctorRow>> GetAllDoctorsAsync();
}

// READ-ONLY doctor catalog reads from ChiroTouch (lugiano_ro). The Doctors
// table is small (~338 rows total) — a full pull is cheap and friendly.
public sealed class DoctorReadQueries : IDoctorReadQueries
{
    private readonly ISourceDbConnectionFactory _sourceDb;

    public DoctorReadQueries(ISourceDbConnectionFactory sourceDb) => _sourceDb = sourceDb;

    public bool IsConfigured => _sourceDb.IsConfigured;

    public async Task<IReadOnlyList<DoctorRow>> GetAllDoctorsAsync()
    {
        await using var conn = _sourceDb.Create();
        const string sql =
            """
            SELECT ID            AS Id,
                   FullName      AS FullName,
                   Credentials1  AS Credentials1,
                   Credentials2  AS Credentials2,
                   NPI           AS Npi,
                   EmailAddress  AS EmailAddress,
                   Inactive      AS Inactive
            FROM   dbo.Doctors
            ORDER BY FullName;
            """;
        return (await conn.QueryAsync<DoctorRow>(sql)).ToList();
    }
}
