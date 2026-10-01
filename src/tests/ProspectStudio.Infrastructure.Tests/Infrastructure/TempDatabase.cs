using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Infrastructure.Storage;

namespace ProspectStudio.Infrastructure.Tests.Infrastructure;

/// <summary>
/// A real SQLite database in a temp file, wired up through the production registration
/// (<see cref="StorageServiceCollectionExtensions.AddProspectStudioStorage"/>) so these tests
/// exercise the same options, interceptors and migrations assembly the server uses. Never the EF
/// InMemory provider (CLAUDE.md).
/// </summary>
internal sealed class TempDatabase : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private TempDatabase(ServiceProvider services, string databasePath)
    {
        _services = services;
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    public IDbContextFactory<ProspectDbContext> Factory => _services.GetRequiredService<IDbContextFactory<ProspectDbContext>>();

    /// <summary>
    /// Everything <see cref="StorageServiceCollectionExtensions.AddProspectStudioStorage"/> registered,
    /// so a test can take a store through the same registration the server uses instead of naming the
    /// EF implementation type.
    /// </summary>
    public IServiceProvider Services => _services;

    /// <summary>Opens (or re-opens) the database file in <paramref name="directory"/>.</summary>
    public static TempDatabase In(string directory)
    {
        var databasePath = Path.Combine(directory, "prospect.db");
        var services = new ServiceCollection()
            .AddProspectStudioStorage(databasePath)
            .BuildServiceProvider();

        return new TempDatabase(services, databasePath);
    }

    public async Task<ProspectDbContext> CreateContextAsync(CancellationToken cancellationToken = default) =>
        await Factory.CreateDbContextAsync(cancellationToken);

    /// <summary>Applies all migrations and returns the context that did it.</summary>
    public async Task<ProspectDbContext> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var context = await CreateContextAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
        return context;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();

        // The provider pools connections, which would keep the file handle open and stop the temp
        // folder from being deleted. Teardown only - production code never touches the pool.
        SqliteConnection.ClearAllPools();
    }
}
