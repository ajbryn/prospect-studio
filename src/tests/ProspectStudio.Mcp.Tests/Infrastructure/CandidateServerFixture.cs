using ModelContextProtocol.Client;
using ProspectStudio.Tests.Shared;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// One server process that already has everything <c>find_candidates</c> needs: the committed
/// geography excerpts under <c>refdata\</c>, the committed Overture Parquet under
/// <c>overture\&lt;release&gt;\places_TX.parquet</c>, and a campaign with
/// <c>poc/fixtures/sample-search-profile.json</c> saved to it.
/// </summary>
/// <remarks>
/// Separate from <see cref="ReferenceDataServerFixture"/>, which deliberately has <em>no</em> Overture
/// extract - that server is what proves <c>NOT_READY</c>, and this one is what proves the happy path.
/// Shared across the collection, so the process starts once.
/// </remarks>
public sealed class CandidateServerFixture : IAsyncLifetime
{
    private IsolatedMcpServer? _server;

    public string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "prospect-studio-candidate-tests",
        Guid.NewGuid().ToString("N"));

    public string Home => Path.GetFullPath(Path.Combine(Root, "home"));

    public string Data => Path.GetFullPath(Path.Combine(Root, "data"));

    public McpClient Client => _server?.Client ?? throw new InvalidOperationException("The server is not started.");

    /// <summary>The campaign the profile was saved to, created once in <see cref="InitializeAsync"/>.</summary>
    public string CampaignId { get; private set; } = string.Empty;

    public string StandardError => _server?.StandardError ?? string.Empty;

    public string Diagnostics =>
        string.Join(
            Environment.NewLine,
            new[] { DuckDbExtensionCache.Advice, StandardError }.Where(line => !string.IsNullOrWhiteSpace(line)));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Data);
        ReferenceDataFixture.Install(Data);
        PlacesDataFixture.Install(Data);

        // A stand-in extract for Oklahoma as well, so the prepare_data contract tests can prove a
        // two-state run echoes both codes without the Overture step downloading anything.
        PlacesDataFixture.Install(Data, "OK");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _server = await IsolatedMcpServer.StartAsync(Home, Data, cancellationToken: timeout.Token);

        CampaignId = await CreateCampaignWithProfileAsync();
    }

    /// <summary>
    /// A second campaign with the same profile, for a test that needs a campaign nothing has searched
    /// yet (<c>geoLabel</c> starts out null, and an idempotent re-run needs a clean slate).
    /// </summary>
    public Task<string> NewCampaignWithProfileAsync() => CreateCampaignWithProfileAsync();

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

    private async Task<string> CreateCampaignWithProfileAsync()
    {
        var created = await ToolCall.OkAsync(
            Client,
            "create_campaign",
            new Dictionary<string, object?>
            {
                ["name"] = $"Houston candidates {Guid.NewGuid():N}"[..28],
                ["product"] = "Scissor & boom lifts",
            },
            Diagnostics);

        var campaignId = created.GetProperty("campaignId").GetString()!;

        using var profile = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(RepoFixtures.SampleSearchProfile));

        await ToolCall.OkAsync(
            Client,
            "save_search_profile",
            new Dictionary<string, object?>
            {
                ["campaignId"] = campaignId,
                ["profile"] = profile.RootElement.Clone(),
            },
            Diagnostics);

        return campaignId;
    }
}

[CollectionDefinition(Name)]
public sealed class CandidateServerCollection : ICollectionFixture<CandidateServerFixture>
{
    public const string Name = "mcp-server-with-overture-extract";
}
