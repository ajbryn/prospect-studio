using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// Runs an MCP server in this process over a pair of pipes, with the real tool-call filter and a
/// test-only tool type, so the error contract can be tested in Debug and Release alike.
/// </summary>
public sealed class InProcessMcpServer : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly Task _serverTask;
    private readonly CancellationTokenSource _cancellation;

    private InProcessMcpServer(
        ServiceProvider services,
        McpServer server,
        Task serverTask,
        CancellationTokenSource cancellation,
        McpClient client)
    {
        _services = services;
        _server = server;
        _serverTask = serverTask;
        _cancellation = cancellation;
        Client = client;
    }

    public McpClient Client { get; }

    public static async Task<InProcessMcpServer> StartAsync<TTools>(CancellationToken cancellationToken)
        where TTools : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer()
            .WithTools<TTools>()
            .WithProspectStudioToolFilter();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var transport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "test-server",
            NullLoggerFactory.Instance);

        var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, provider);
        var cancellation = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.RunAsync(cancellation.Token), CancellationToken.None);

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            cancellationToken: cancellationToken);

        return new InProcessMcpServer(provider, server, serverTask, cancellation, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _cancellation.CancelAsync();

        try
        {
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException)
        {
            // The transport is torn down; how the server task ends does not matter here.
        }

        await _server.DisposeAsync();
        await _services.DisposeAsync();
        _cancellation.Dispose();
    }
}
