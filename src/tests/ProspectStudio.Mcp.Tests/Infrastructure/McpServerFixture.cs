using ModelContextProtocol.Client;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

public sealed class McpServerFixture : IAsyncLifetime
{
    private readonly List<string> _standardError = [];

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "prospect-studio-mcp-tests", Guid.NewGuid().ToString("N"));

    public string Home => Path.Combine(Root, "home");

    public string Data => Path.Combine(Root, "data");

    public McpClient Client { get; private set; } = null!;

    public string StandardError
    {
        get
        {
            lock (_standardError)
            {
                return string.Join(Environment.NewLine, _standardError);
            }
        }
    }

    public async Task InitializeAsync()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [TestServerBinary.Dll],
            Name = "prospect-studio",
            EnvironmentVariables = TestEnvironment.For(Home, Data),
            StandardErrorLines = line =>
            {
                lock (_standardError)
                {
                    _standardError.Add(line);
                }
            },
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        Client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    }

    public async Task DisposeAsync()
    {
        await Client.DisposeAsync();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The server may still hold its log file; a leftover temp folder is harmless.
        }
    }
}

[CollectionDefinition(Name)]
public sealed class McpServerCollection : ICollectionFixture<McpServerFixture>
{
    public const string Name = "mcp-server";
}
