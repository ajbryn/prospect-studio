using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// Applies the per-connection settings from technical-design §5.3: write-ahead logging so a job and a
/// tool call can read while the other writes, a busy timeout instead of an immediate
/// <c>SQLITE_BUSY</c>, and enforced foreign keys (SQLite leaves them off by default).
/// </summary>
/// <remarks>
/// This is the one place the provider-neutrality rule in CLAUDE.md allows SQLite-specific SQL: swapping
/// the provider means dropping this interceptor, nothing else.
/// </remarks>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private static readonly string[] Pragmas =
    [
        "PRAGMA journal_mode=WAL;",
        "PRAGMA busy_timeout=5000;",
        "PRAGMA foreign_keys=ON;",
    ];

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);

        foreach (var pragma in Pragmas)
        {
            using var command = connection.CreateCommand();
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }

        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        foreach (var pragma in Pragmas)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = pragma;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }
}
