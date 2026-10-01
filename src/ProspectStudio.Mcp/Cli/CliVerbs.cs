using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core;
using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Reference;
using ProspectStudio.Core.Status;
using ProspectStudio.Infrastructure.Reference;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Infrastructure.Workspace;
using ProspectStudio.Mcp.Errors;
using ProspectStudio.Mcp.Hosting;

namespace ProspectStudio.Mcp.Cli;

/// <summary>
/// CLI verbs run instead of the MCP server, so they may use stdout: the JSON-RPC transport that
/// owns stdout is never started on this path.
/// </summary>
public static class CliVerbs
{
    public static async Task<int> RunAsync(string[] args, PsOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);

        return args[0].ToLowerInvariant() switch
        {
            "doctor" => Doctor(options),
            "setup" => await SetupAsync(args, options, cancellationToken).ConfigureAwait(false),
            "help" or "--help" or "-h" or "/?" => Help(0),
            _ => Unknown(args[0]),
        };
    }

    /// <summary>
    /// One-time preparation: brings the database up to date and creates the workspace folders
    /// (implementation-plan C2, POC-2, POC-3). <c>--seed-fixtures</c> is the flag C1 deferred until
    /// this verb existed; <c>PS_SEED_FIXTURES=1</c> does the same.
    /// </summary>
    private static async Task<int> SetupAsync(string[] args, PsOptions options, CancellationToken cancellationToken)
    {
        if (!SetupRequest.TryParse(args, out var request, out var problem))
        {
            Console.Error.WriteLine(problem);
            return Help(64);
        }

        Console.WriteLine($"prospect-studio {ProductVersion.Current} setup");
        Console.WriteLine();

        // The same registration the server uses, so the verb cannot drift from it: it was already wired
        // twice once, and the second copy is the one that gets forgotten.
        await using var services = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddProspectStudioStorage(options.DatabasePath)
            .AddProspectStudioReferenceData(options)
            .BuildServiceProvider();

        var applied = await DatabaseInitializer.MigrateAsync(services, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Database       : {options.DatabasePath} ({applied} migration(s) applied)");

        var seedFixtures = request.SeedFixtures || options.SeedFixtures;
        var workspace = await StartupTasks
            .EnsureWorkspaceAsync(options, cancellationToken, seedFixtures)
            .ConfigureAwait(false);
        Console.WriteLine(
            $"Workspace      : {options.Home} ({workspace.CreatedFolders.Count} of "
            + $"{WorkspaceBootstrapper.FolderNames.Count} folder(s) created, "
            + $"{workspace.SeededFiles} brand-kit file(s) seeded)");

        Console.WriteLine($"Reference data : {options.ReferenceDataDirectory}");
        await PrepareReferenceDataAsync(services, request.Force, cancellationToken).ConfigureAwait(false);

        Console.WriteLine(
            $"States         : {(request.States.Count == 0 ? "none requested" : string.Join(", ", request.States))}"
            + " (place data per state is prepared by a later step)");

        Console.WriteLine();
        Console.WriteLine("Setup complete.");
        return 0;
    }

    /// <summary>
    /// Runs the reference-data steps and prints one line each, saying plainly which were skipped: an
    /// operator running <c>setup</c> again needs to see that nothing was downloaded twice (POC-2).
    /// </summary>
    private static async Task PrepareReferenceDataAsync(
        IServiceProvider services,
        bool force,
        CancellationToken cancellationToken)
    {
        var result = await services.GetRequiredService<ReferenceDataPreparer>()
            .PrepareAsync(force, PrintStep, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"  manifest     : {result.ManifestPath}");
    }

    private static Task PrintStep(ReferenceStepResult step, double progress, CancellationToken cancellationToken)
    {
        var rows = step.Rows > 0 ? $" ({step.Rows} row(s))" : string.Empty;
        var outcome = step.Skipped
            ? $"skipped, {step.File} is already prepared{rows}"
            : $"prepared {step.File}{rows}";

        Console.WriteLine($"  {step.Step,-12} : {outcome}");
        return Task.CompletedTask;
    }

    /// <summary>The arguments of <c>setup</c>: <c>--states TX[,OK]</c>, <c>--force</c>, <c>--seed-fixtures</c>.</summary>
    private sealed record SetupRequest(IReadOnlyList<string> States, bool Force, bool SeedFixtures)
    {
        public static bool TryParse(string[] args, out SetupRequest request, out string? problem)
        {
            var states = new List<string>();
            var force = false;
            var seedFixtures = false;
            problem = null;

            for (var index = 1; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument.ToLowerInvariant())
                {
                    case "--states":
                        while (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            states.AddRange(args[++index]
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Select(state => state.ToUpperInvariant()));
                        }

                        break;

                    case "--force":
                        force = true;
                        break;

                    case "--seed-fixtures":
                        seedFixtures = true;
                        break;

                    default:
                        problem = $"Unknown setup option '{argument}'.";
                        break;
                }

                if (problem is not null)
                {
                    request = new SetupRequest([], false, false);
                    return false;
                }
            }

            request = new SetupRequest(states, force, seedFixtures);
            return true;
        }
    }

    private static int Doctor(PsOptions options)
    {
        var reference = new ReferenceDataFiles(options.ReferenceDataDirectory);
        var status = new StatusService(options, reference).GetStatus();

        Console.WriteLine($"prospect-studio {status.Version}");
        Console.WriteLine();
        Console.WriteLine("Paths");
        Console.WriteLine($"  workspace (PROSPECT_STUDIO_HOME) : {options.Home}");
        Console.WriteLine($"  data (PROSPECT_STUDIO_DATA)      : {options.Data}");
        Console.WriteLine($"  database                         : {options.DatabasePath}");
        Console.WriteLine($"  logs                             : {options.LogsDirectory}");
        Console.WriteLine($"  reference data                   : {options.ReferenceDataDirectory}");
        Console.WriteLine($"  overture extracts                : {options.OvertureDirectory}");
        Console.WriteLine($"  cache                            : {options.CacheDirectory}");
        Console.WriteLine();
        Console.WriteLine("Options");
        Console.WriteLine($"  tracking base url : {options.TrackingBaseUrl}");
        Console.WriteLine($"  offer prefix      : {options.OfferPrefix ?? $"(unset, brand kit or {PsOptionsFactory.FallbackOfferPrefix})"}");
        Console.WriteLine($"  user agent        : {options.UserAgent}");
        Console.WriteLine($"  overture release  : {options.OvertureRelease}");
        Console.WriteLine($"  cbp year          : {options.CbpYear?.ToString() ?? "(discover latest)"}");
        Console.WriteLine();
        Console.WriteLine("Keys configured");
        Console.WriteLine($"  census     : {status.Keys.Census}");
        Console.WriteLine($"  openai     : {status.Keys.OpenAi}");
        Console.WriteLine($"  gemini     : {status.Keys.Gemini}");
        Console.WriteLine($"  googleMaps : {status.Keys.GoogleMaps}");
        Console.WriteLine($"  hubspot    : {status.Keys.HubSpot}");
        Console.WriteLine();
        Console.WriteLine("Readiness");
        Console.WriteLine($"  {JsonSerializer.Serialize(status.Ready, ToolResults.Json)}");
        if (reference.MissingFiles.Count > 0)
        {
            Console.WriteLine($"  reference data missing: {string.Join(", ", reference.MissingFiles)}");
        }

        Console.WriteLine();
        Console.WriteLine("DuckDB extensions (installed into the local cache if missing; needs network once)");
        foreach (var extension in new[] { "spatial", "httpfs" })
        {
            Console.WriteLine($"  {extension,-8} : {InstallDuckDbExtension(extension)}");
        }

        Console.WriteLine();
        Console.WriteLine($"Workspace folder exists : {Directory.Exists(options.Home)}");
        Console.WriteLine($"Data folder exists      : {Directory.Exists(options.Data)}");
        Console.WriteLine($"Logs folder exists      : {Directory.Exists(options.LogsDirectory)}");

        if (status.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Warnings");
            foreach (var warning in status.Warnings)
            {
                Console.WriteLine($"  - {warning}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Installs a DuckDB extension into the local cache so later offline runs - including the test
    /// suite, which may never reach the network - can just <c>LOAD</c> it. A failure here is reported
    /// rather than fatal: <c>doctor</c> is a diagnostic.
    /// </summary>
    private static string InstallDuckDbExtension(string extension)
    {
        try
        {
            DuckDbSpatial.InstallExtensions(extension);
            return "installed and loaded";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"not available ({exception.Message.ReplaceLineEndings(" ")})";
        }
    }

    private static int Help(int exitCode)
    {
        var writer = exitCode == 0 ? Console.Out : Console.Error;
        writer.WriteLine($"prospect-studio {ProductVersion.Current}");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  ProspectStudio.Mcp             Run the MCP server on stdio (no arguments)");
        writer.WriteLine("  ProspectStudio.Mcp doctor      Print resolved paths, options and readiness, and");
        writer.WriteLine("                                 install the DuckDB extensions (needs network once)");
        writer.WriteLine("  ProspectStudio.Mcp setup       Migrate the database, create the workspace folders");
        writer.WriteLine("                                 and prepare the reference data (downloads from");
        writer.WriteLine("                                 census.gov unless it is already prepared)");
        writer.WriteLine("  ProspectStudio.Mcp help        Show this help");
        writer.WriteLine();
        writer.WriteLine("setup options:");
        writer.WriteLine("  --states TX[,OK]   States the campaign covers (reference data itself is national)");
        writer.WriteLine("  --force            Redo steps that are already done");
        writer.WriteLine("  --seed-fixtures    Copy the fixture brand kit into an empty Brand Kit folder");
        return exitCode;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"Unknown verb '{verb}'.");
        return Help(64);
    }
}
