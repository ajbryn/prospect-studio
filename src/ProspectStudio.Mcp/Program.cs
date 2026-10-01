using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ProspectStudio.Core;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Jobs;
using ProspectStudio.Core.SearchProfiles;
using ProspectStudio.Core.Status;
using ProspectStudio.Infrastructure.Config;
using ProspectStudio.Infrastructure.Reference;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Infrastructure.Workspace;
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
        return await CliVerbs.RunAsync(args, options, CancellationToken.None);
    }

    // Defence in depth for the hard rule: the stdio transport writes JSON-RPC to the standard
    // output stream itself, so redirecting the console writer sends any stray write to stderr.
    Console.SetOut(Console.Error);

    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(Log.Logger);

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
    builder.Services.AddSingleton<StatusService>();
    builder.Services.AddProspectStudioStorage(options.DatabasePath);
    builder.Services.AddProspectStudioReferenceData(options);
    builder.Services.AddSingleton<ICampaignWorkspace, FileSystemCampaignWorkspace>();
    builder.Services.AddSingleton<SearchProfileValidator>();
    builder.Services.AddSingleton<CampaignService>();
    builder.Services.AddSingleton(provider => new JobRunner(
        provider.GetRequiredService<IJobStore>(),
        provider.GetRequiredService<TimeProvider>(),
        JobLogging.Observer));
    builder.Services.AddSingleton<JobService>();

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

    var host = builder.Build();
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    await StartupTasks.RunAsync(host.Services, options, lifetime.ApplicationStopping);
    await host.RunAsync();
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
