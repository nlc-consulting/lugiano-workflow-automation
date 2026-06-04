using Microsoft.Data.SqlClient;

namespace Lugiano.Workflow.SyncService.ChiroTouch;

public interface ISourceDbConnectionFactory
{
    // True when the ChiroTouch connection string is present in configuration.
    bool IsConfigured { get; }

    // Opens a connection to the ChiroTouch (PSChiro) database.
    // This database is READ-ONLY — only SELECT statements are ever issued against it.
    SqlConnection Create();
}

public sealed class SourceDbConnectionFactory : ISourceDbConnectionFactory
{
    private readonly IConfiguration _configuration;

    public SourceDbConnectionFactory(IConfiguration configuration) => _configuration = configuration;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration.GetConnectionString("ChiroTouch"));

    public SqlConnection Create()
    {
        // PROTOTYPE: connection string (including the lugiano_ro password) lives
        // in appsettings.Development.json today. Production must source it from a
        // secrets store (Azure Key Vault / AWS Secrets Manager / env vars) — same
        // applies to the WorkflowAutomation connection string in Program.cs.
        // Resolved lazily so the service still starts and runs migrations when
        // ChiroTouch isn't configured; only throws if a poll actually needs it.
        var connectionString = _configuration.GetConnectionString("ChiroTouch");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Connection string 'ChiroTouch' is not configured.");

        return new SqlConnection(connectionString);
    }
}
