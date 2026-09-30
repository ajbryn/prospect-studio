using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// Brings the database up to date. Called at server start and by the <c>setup</c> verb
/// (technical-design §5.3), so EF Core stays out of the MCP layer.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>Applies every pending migration and returns how many were applied.</summary>
    public static async Task<int> MigrateAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        var factory = services.GetRequiredService<IDbContextFactory<ProspectDbContext>>();
        await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var count = pending.Count();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }
}
