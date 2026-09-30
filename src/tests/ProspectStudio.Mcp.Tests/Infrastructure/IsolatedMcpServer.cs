using ModelContextProtocol.Client;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// A server process with its own workspace and data folders, for the few tests that need a
/// non-shared environment: a different environment variable, or a restart against the same folders.
/// Most contract tests should use the shared <see cref="McpServerFixture"/> instead, which starts the
/// server once for the whole collection.
/// </summary>
internal sealed class IsolatedMcpServer : IAsyncDisposable
{
    private readonly List<string> _standardError;

    private IsolatedMcpServer(McpClient client, List<string> standardError)
    {
        Client = client;
        _standardError = standardError;
    }

    public McpClient Client { get; }

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

    public static async Task<IsolatedMcpServer> StartAsync(
        string home,
        string data,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        var standardError = new List<string>();

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [TestServerBinary.Dll],
            Name = "prospect-studio",
            EnvironmentVariables = TestEnvironment.For(home, data, extraEnvironment),
            StandardErrorLines = line =>
            {
                lock (standardError)
                {
                    standardError.Add(line);
                }
            },
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        return new IsolatedMcpServer(client, standardError);
    }

    public async ValueTask DisposeAsync() => await Client.DisposeAsync();
}
