using System.Text.Json;
using ProspectStudio.Core;
using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Status;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Cli;

/// <summary>
/// CLI verbs run instead of the MCP server, so they may use stdout: the JSON-RPC transport that
/// owns stdout is never started on this path.
/// </summary>
public static class CliVerbs
{
    public static int Run(string[] args, PsOptions options)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);

        return args[0].ToLowerInvariant() switch
        {
            "doctor" => Doctor(options),
            "help" or "--help" or "-h" or "/?" => Help(0),
            _ => Unknown(args[0]),
        };
    }

    private static int Doctor(PsOptions options)
    {
        var status = new StatusService(options).GetStatus();

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

    private static int Help(int exitCode)
    {
        var writer = exitCode == 0 ? Console.Out : Console.Error;
        writer.WriteLine($"prospect-studio {ProductVersion.Current}");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  ProspectStudio.Mcp             Run the MCP server on stdio (no arguments)");
        writer.WriteLine("  ProspectStudio.Mcp doctor      Print resolved paths, options and readiness");
        writer.WriteLine("  ProspectStudio.Mcp help        Show this help");
        return exitCode;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"Unknown verb '{verb}'.");
        return Help(64);
    }
}
