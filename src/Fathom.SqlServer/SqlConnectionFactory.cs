using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Fathom.SqlServer;

/// <summary>
/// Opens connections for export runs. Multiple Active Result Sets is forced on regardless of
/// what the configured connection string says: the query engine opens several simultaneous
/// readers per run (one per entity level) against the same session's staged temp tables, and
/// that pattern requires MARS.
/// </summary>
public sealed class SqlConnectionFactory(IOptions<FathomOptions> options)
{
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = options.Value.ConnectionString
            ?? throw new InvalidOperationException("Fathom:ConnectionString is not configured.");
        var builder = new SqlConnectionStringBuilder(connectionString) { MultipleActiveResultSets = true };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
