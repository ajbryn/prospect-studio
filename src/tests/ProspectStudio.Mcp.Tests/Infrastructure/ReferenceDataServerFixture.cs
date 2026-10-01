using ModelContextProtocol.Client;
using ProspectStudio.Tests.Shared;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// One server process whose <c>refdata</c> folder already holds the committed geography excerpts, as
/// if <c>prepare_data</c> had run. Shared by every test that needs the server to know US geography, so
/// the process starts once instead of per test, and nothing downloads anything.
/// </summary>
public sealed class ReferenceDataServerFixture : IAsyncLifetime
{
    private IsolatedMcpServer? _server;

    public string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "prospect-studio-refdata-tests",
        Guid.NewGuid().ToString("N"));

    public string Home => Path.GetFullPath(Path.Combine(Root, "home"));

    public string Data => Path.GetFullPath(Path.Combine(Root, "data"));

    public string ReferenceDataDirectory => ReferenceDataFixture.Directory(Data);

    public McpClient Client => _server?.Client ?? throw new InvalidOperationException("The server is not started.");

    public string StandardError => _server?.StandardError ?? string.Empty;

    /// <summary>
    /// Everything worth saying when a geography tool call fails: the server's stderr, and whether the
    /// DuckDB extension cache is primed.
    /// </summary>
    public string Diagnostics =>
        string.Join(
            Environment.NewLine,
            new[] { DuckDbExtensionCache.Advice, StandardError }.Where(line => !string.IsNullOrWhiteSpace(line)));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Data);
        ReferenceDataFixture.Install(Data);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        _server = await IsolatedMcpServer.StartAsync(Home, Data, cancellationToken: timeout.Token);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

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
public sealed class ReferenceDataServerCollection : ICollectionFixture<ReferenceDataServerFixture>
{
    public const string Name = "mcp-server-with-reference-data";
}
