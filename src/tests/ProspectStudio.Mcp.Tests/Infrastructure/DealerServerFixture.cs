using ModelContextProtocol.Client;
using ProspectStudio.Tests.Shared;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// One server process in the state chunk C5's M1 demo starts from: the committed geography excerpts
/// under <c>refdata\</c>, the committed Overture Parquet under <c>overture\</c>, the three business
/// lists copied into the workspace folders <c>import_list</c> defaults to, and a campaign with
/// <c>poc/fixtures/sample-search-profile.json</c> saved to it.
/// </summary>
/// <remarks>
/// Separate from <see cref="CandidateServerFixture"/> on purpose. That server has <strong>no</strong>
/// dealers, which is a real state - everything a user does before <c>import_list</c> - and it is what
/// the C4 tests assert against. Importing lists into it would change those answers, and because tests
/// in one collection share the process the order would decide which assertions held.
/// </remarks>
public sealed class DealerServerFixture : IAsyncLifetime
{
    private IsolatedMcpServer? _server;

    public string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "prospect-studio-dealer-tests",
        Guid.NewGuid().ToString("N"));

    public string Home => Path.GetFullPath(Path.Combine(Root, "home"));

    public string Data => Path.GetFullPath(Path.Combine(Root, "data"));

    /// <summary>The workspace folder <c>import_list</c> reads dealers and territories from (§5.1).</summary>
    public string DealersFolder => Path.Combine(Home, "Dealers");

    /// <summary>The workspace folder <c>import_list</c> reads suppression lists from (§5.1).</summary>
    public string SuppressionFolder => Path.Combine(Home, "Suppression");

    public McpClient Client => _server?.Client ?? throw new InvalidOperationException("The server is not started.");

    /// <summary>A campaign with the sample profile saved, created once in <see cref="InitializeAsync"/>.</summary>
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

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _server = await IsolatedMcpServer.StartAsync(Home, Data, cancellationToken: timeout.Token);

        // The server creates the workspace folders on start (C1); the lists are dropped in afterwards,
        // which is exactly what the user does.
        InstallLists();

        CampaignId = await CreateCampaignWithProfileAsync();

        // Imported through the workspace default, with no `path`, so the first-import counts are
        // deterministic and the default-path half of the contract is covered by the setup every other
        // test depends on rather than by a test that has to run first.
        foreach (var kind in new[] { "dealers", "territories", "suppression" })
        {
            FirstImport[kind] = await ImportAsync(kind);
        }
    }

    /// <summary>
    /// What the initial <c>import_list</c> call returned for each kind, so a test can assert the
    /// first-import counts without depending on test order.
    /// </summary>
    public Dictionary<string, System.Text.Json.JsonElement> FirstImport { get; } = new(StringComparer.Ordinal);

    /// <summary>A second campaign with the same profile, for a test that needs a clean slate.</summary>
    public Task<string> NewCampaignWithProfileAsync() => CreateCampaignWithProfileAsync();

    /// <summary>Calls <c>import_list</c> with no <c>path</c>, so the workspace default is what is tested.</summary>
    public async Task<System.Text.Json.JsonElement> ImportAsync(string kind) =>
        await ToolCall.OkAsync(
            Client,
            "import_list",
            new Dictionary<string, object?> { ["kind"] = kind },
            Diagnostics);

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

    /// <summary>
    /// Copies the three lists into the folders technical-design §5.1 puts them in, so
    /// <c>import_list</c>'s "defaults to the workspace folder file" has something to find. Exactly one
    /// candidate file per kind, which is the only shape that default is unambiguous for.
    /// </summary>
    private void InstallLists()
    {
        Directory.CreateDirectory(DealersFolder);
        Directory.CreateDirectory(SuppressionFolder);

        File.Copy(RepoFixtures.DealersCsv, Path.Combine(DealersFolder, "dealers.csv"), overwrite: true);
        File.Copy(RepoFixtures.TerritoriesCsv, Path.Combine(DealersFolder, "territories.csv"), overwrite: true);
        File.Copy(RepoFixtures.SuppressionCsv, Path.Combine(SuppressionFolder, "suppression.csv"), overwrite: true);
    }

    private async Task<string> CreateCampaignWithProfileAsync()
    {
        var created = await ToolCall.OkAsync(
            Client,
            "create_campaign",
            new Dictionary<string, object?>
            {
                ["name"] = $"Houston dealers {Guid.NewGuid():N}"[..26],
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
public sealed class DealerServerCollection : ICollectionFixture<DealerServerFixture>
{
    public const string Name = "mcp-server-with-dealers-imported";
}
