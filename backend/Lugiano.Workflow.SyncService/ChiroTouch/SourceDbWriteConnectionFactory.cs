using Microsoft.Data.SqlClient;

namespace Lugiano.Workflow.SyncService.ChiroTouch;

// Write-capable connection factory for PSChiro. Deliberately SEPARATE from
// ISourceDbConnectionFactory so:
//   - 99% of code keeps using the read-only factory (default-deny writes)
//   - Greppable: only IPSChiroWriteService takes this dep
//   - SQL Server-side, the lugiano_rw account has INSERT only on ChartText /
//     ChartNotes / Signatures (no UPDATE/DELETE/ALTER) — minimal blast radius
//     if anything misuses this factory
public interface ISourceDbWriteConnectionFactory
{
    bool IsConfigured { get; }
    SqlConnection Create();
}

public sealed class SourceDbWriteConnectionFactory : ISourceDbWriteConnectionFactory
{
    private readonly IConfiguration _configuration;

    public SourceDbWriteConnectionFactory(IConfiguration configuration) => _configuration = configuration;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration.GetConnectionString("ChiroTouchWrite"));

    public SqlConnection Create()
    {
        // PROTOTYPE: connection string lives in appsettings.Development.json
        // alongside the read-only one. Production must source from a secrets
        // store (see PROD-TODO #36).
        var connectionString = _configuration.GetConnectionString("ChiroTouchWrite");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Connection string 'ChiroTouchWrite' is not configured.");

        return new SqlConnection(connectionString);
    }
}
