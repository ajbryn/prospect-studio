using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The two C1 behaviours that need a server of their own rather than the shared fixture: the
/// <c>PS_SEED_FIXTURES</c> dev convenience (a different environment), and POC-5's "a record in
/// SQLite" (a restart against the same folders). Each test starts its own server process, so these
/// are slower than the rest of the contract tests.
/// </summary>
public class WorkspaceAndPersistenceTests
{
    [Fact]
    public async Task The_server_seeds_the_fixture_brand_kit_when_PS_SEED_FIXTURES_is_set()
    {
        using var timeout = TestTimeout.Start(120);
        using var workspace = new TempWorkspace();

        await using (var server = await StartAsync(workspace, seedFixtures: true, timeout.Token))
        {
            await ToolCall.OkAsync(server.Client, "get_status", diagnostics: server.StandardError);

            var brandKit = workspace.WorkspaceFolder("Brand Kit");
            Directory.Exists(brandKit).ShouldBeTrue("POC-3 creates 'Brand Kit' whether or not it is seeded.");

            var seeded = Directory.EnumerateFiles(brandKit, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(brandKit, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToList();

            var expected = Directory.EnumerateFiles(RepoFixtures.BrandKitDirectory, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(RepoFixtures.BrandKitDirectory, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToList();

            seeded.ShouldBe(
                expected,
                ignoreOrder: true,
                $"{TestEnvironment.SeedFixturesVariable}=1 copies poc/fixtures/brand-kit into an empty 'Brand Kit' "
                + $"(implementation-plan C1). Server log:{Environment.NewLine}{server.StandardError}");
        }
    }

    [Fact]
    public async Task The_server_never_overwrites_a_brand_kit_file_it_already_finds()
    {
        using var timeout = TestTimeout.Start(120);
        using var workspace = new TempWorkspace();

        var brand = workspace.WorkspaceFolder("Brand Kit", "brand.json");
        Directory.CreateDirectory(Path.GetDirectoryName(brand)!);
        await File.WriteAllTextAsync(brand, """{"brand":"edited by the user"}""", timeout.Token);

        await using (var server = await StartAsync(workspace, seedFixtures: true, timeout.Token))
        {
            await ToolCall.OkAsync(server.Client, "get_status", diagnostics: server.StandardError);

            // Proves the bootstrap ran at all, so the assertions below cannot pass vacuously.
            Directory.Exists(workspace.WorkspaceFolder("Campaigns")).ShouldBeTrue(
                $"POC-3: the workspace folders must be created. Server log:{Environment.NewLine}{server.StandardError}");

            (await File.ReadAllTextAsync(brand, timeout.Token))
                .ShouldBe("""{"brand":"edited by the user"}""", "seeding is skipped once 'Brand Kit' holds a file.");
            File.Exists(workspace.WorkspaceFolder("Brand Kit", "logo.svg")).ShouldBeFalse(
                "a non-empty 'Brand Kit' is left alone entirely, not topped up file by file.");
        }
    }

    /// <summary>
    /// POC-5: a campaign is a folder <em>and</em> a row in SQLite, so a restart against the same
    /// <c>PROSPECT_STUDIO_DATA</c> still lists it. The assertion goes through the tools, so it stays
    /// provider-neutral (no schema or SQLite-specific query).
    /// </summary>
    [Fact]
    public async Task Campaigns_survive_a_server_restart()
    {
        using var timeout = TestTimeout.Start(180);
        using var workspace = new TempWorkspace();
        var name = $"Survives A Restart {Guid.NewGuid().ToString("N")[..6]}";

        string campaignId;
        string folder;

        await using (var first = await StartAsync(workspace, seedFixtures: false, timeout.Token))
        {
            var created = await ToolCall.OkAsync(
                first.Client,
                "create_campaign",
                new Dictionary<string, object?> { ["name"] = name },
                first.StandardError);

            campaignId = created.GetProperty("campaignId").GetString()!;
            folder = created.GetProperty("folder").GetString()!;
        }

        await using var second = await StartAsync(workspace, seedFixtures: false, timeout.Token);

        var listed = await ToolCall.OkAsync(second.Client, "list_campaigns", diagnostics: second.StandardError);
        listed.GetRawText().Contains(campaignId, StringComparison.Ordinal).ShouldBeTrue(
            $"'{campaignId}' was not persisted. list_campaigns returned: {listed}");

        var campaign = await ToolCall.OkAsync(
            second.Client,
            "get_campaign",
            new Dictionary<string, object?> { ["campaignId"] = campaignId },
            second.StandardError);

        campaign.GetProperty("name").GetString().ShouldBe(name);
        campaign.GetProperty("folder").GetString().ShouldBe(folder);
        Directory.Exists(folder).ShouldBeTrue();
    }

    private static async Task<IsolatedMcpServer> StartAsync(
        TempWorkspace workspace,
        bool seedFixtures,
        CancellationToken cancellationToken) =>
        await IsolatedMcpServer.StartAsync(
            workspace.Home,
            workspace.Data,
            new Dictionary<string, string?> { [TestEnvironment.SeedFixturesVariable] = seedFixtures ? "1" : string.Empty },
            cancellationToken);
}
