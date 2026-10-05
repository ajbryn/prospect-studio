using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Jobs;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// The one place that wires up storage, so the server and the tests configure the database the same
/// way (technical-design §5.3).
/// </summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <c>AddDbContextFactory&lt;ProspectDbContext&gt;</c> for the database file at
    /// <paramref name="databasePath"/>, together with the connection interceptor that runs
    /// <c>PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;</c> on open, and
    /// the stores that read and write it.
    /// </summary>
    public static IServiceCollection AddProspectStudioStorage(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

        services.AddDbContextFactory<ProspectDbContext>(options => options
            .UseSqlite(connectionString)
            .AddInterceptors(new SqlitePragmaInterceptor()));

        services.AddSingleton<ICampaignStore, EfCampaignStore>();
        services.AddSingleton<IJobStore, EfJobStore>();
        services.AddSingleton<ICandidateStore, EfCandidateStore>();
        return services;
    }
}
