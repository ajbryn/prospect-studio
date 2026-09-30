using ProspectStudio.Core.Configuration;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Infrastructure.Workspace;
using Serilog;

namespace ProspectStudio.Mcp.Hosting;

/// <summary>
/// What has to be true before the first tool call: the database is migrated and the workspace folders
/// exist (technical-design §5.3, POC-3). Runs before the stdio transport starts, so no tool can see a
/// half-prepared workspace.
/// </summary>
public static class StartupTasks
{
    /// <summary>Where the fixture brand kit ships next to the server binary.</summary>
    public const string BundledFixturesFolder = "fixtures";

    public static async Task RunAsync(IServiceProvider services, PsOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var applied = await DatabaseInitializer.MigrateAsync(services, cancellationToken).ConfigureAwait(false);
        Log.Information(
            "Database ready at {DatabasePath} ({Applied} migration(s) applied)",
            options.DatabasePath,
            applied);

        var bootstrapper = new WorkspaceBootstrapper(options.Home, FixtureBrandKitDirectory());
        var workspace = await bootstrapper.EnsureAsync(options.SeedFixtures, cancellationToken).ConfigureAwait(false);
        Log.Information(
            "Workspace ready at {Home} ({Created} folder(s) created, {Seeded} brand-kit file(s) seeded)",
            options.Home,
            workspace.CreatedFolders.Count,
            workspace.SeededFiles);
    }

    private static string? FixtureBrandKitDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, BundledFixturesFolder, "brand-kit");
        return Directory.Exists(path) ? path : null;
    }
}
