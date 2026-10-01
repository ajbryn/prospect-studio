using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Jobs;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Infrastructure.Workspace;
using Serilog;

namespace ProspectStudio.Mcp.Hosting;

/// <summary>
/// What has to be true before the first tool call: the database is migrated, the workspace folders
/// exist and the job runner has recovered whatever the last process left behind (technical-design
/// §5.3, §8, POC-3). Runs before the stdio transport starts, so no tool can see a half-prepared
/// workspace.
/// </summary>
public static class StartupTasks
{
    /// <summary>Where the fixture brand kit ships next to the server binary.</summary>
    public const string BundledFixturesFolder = "fixtures";

    public static async Task RunAsync(IServiceProvider services, PsOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var applied = await DatabaseInitializer.MigrateAsync(services, cancellationToken).ConfigureAwait(false);
        Log.Information(
            "Database ready at {DatabasePath} ({Applied} migration(s) applied)",
            options.DatabasePath,
            applied);

        var workspace = await EnsureWorkspaceAsync(options, cancellationToken).ConfigureAwait(false);
        Log.Information(
            "Workspace ready at {Home} ({Created} folder(s) created, {Seeded} brand-kit file(s) seeded)",
            options.Home,
            workspace.CreatedFolders.Count,
            workspace.SeededFiles);

        // Nothing is running when the process begins and the queue lives in memory, so every job the
        // last run left queued or running is stale (technical-design §8).
        var interrupted = await services.GetRequiredService<JobRunner>()
            .StartAsync(cancellationToken)
            .ConfigureAwait(false);
        Log.Information(
            "Job runner started ({Interrupted} unfinished job(s) marked interrupted)",
            interrupted);
    }

    /// <summary>
    /// Creates the workspace folders and, when asked, seeds the fixture brand kit. Shared with the
    /// <c>setup</c> verb so both do exactly the same thing.
    /// </summary>
    public static Task<WorkspaceBootstrapResult> EnsureWorkspaceAsync(
        PsOptions options,
        CancellationToken cancellationToken,
        bool? seedFixtures = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var bootstrapper = new WorkspaceBootstrapper(options.Home, FixtureBrandKitDirectory());
        return bootstrapper.EnsureAsync(seedFixtures ?? options.SeedFixtures, cancellationToken);
    }

    private static string? FixtureBrandKitDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, BundledFixturesFolder, "brand-kit");
        return Directory.Exists(path) ? path : null;
    }
}
