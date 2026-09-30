using ProspectStudio.Core.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ProspectStudio.Mcp.Hosting;

public static class LoggingSetup
{
    public static Logger Create(PsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            // stdout is reserved for the MCP protocol, so the console sink writes every level to stderr.
            .WriteTo.Console(
                standardErrorFromLevel: LogEventLevel.Verbose,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(options.LogsDirectory, "prospect-studio-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:o} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
