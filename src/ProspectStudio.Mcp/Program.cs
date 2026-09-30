using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ProspectStudio.Core;
using ProspectStudio.Core.Status;
using ProspectStudio.Infrastructure.Config;
using ProspectStudio.Mcp.Cli;
using ProspectStudio.Mcp.Errors;
using ProspectStudio.Mcp.Hosting;
using Serilog;

var options = EnvironmentOptions.EnsureDataDirectories(EnvironmentOptions.Load());
Log.Logger = LoggingSetup.Create(options);

try
{
    if (args.Length > 0)
    {
        return CliVerbs.Run(args, options);
    }

    // Defence in depth for the hard rule: the stdio transport writes JSON-RPC to the standard
    // output stream itself, so redirecting the console writer sends any stray write to stderr.
    Console.SetOut(Console.Error);

    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(Log.Logger);

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<StatusService>();

    builder.Services
        .AddMcpServer(server => server.ServerInfo = new Implementation
        {
            Name = "prospect-studio",
            Title = "Prospect Studio",
            Version = ProductVersion.Current,
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithProspectStudioToolFilter();

    Log.Information(
        "prospect-studio {Version} starting on stdio (home={Home}, data={Data})",
        ProductVersion.Current,
        options.Home,
        options.Data);

    await builder.Build().RunAsync();
    return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "prospect-studio stopped unexpectedly");
    return 70;
}
finally
{
    await Log.CloseAndFlushAsync();
}
