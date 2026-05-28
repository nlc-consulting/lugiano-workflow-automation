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
        // Resolved lazily: the ChiroTouch connection string is intentionally not
        // configured yet, so the service can start and run migrations without it.
        // This throws only if a poll actually tries to read the source DB.
        var connectionString = _configuration.GetConnectionString("ChiroTouch");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Connection string 'ChiroTouch' is not configured.");

        return new SqlConnection(connectionString);
    }
}
