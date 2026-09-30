using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The C1 campaign contracts from mcp-tools.md §Campaign, driven over the real stdio transport so the
/// error envelope comes from the server's own filter rather than an internal exception type.
/// Campaign names include the test name, so the tests share one server without colliding.
/// </summary>
[Collection(McpServerCollection.Name)]
public partial class CampaignContractTests(McpServerFixture server)
{
    /// <summary>
    /// <c>cmp_</c> plus 6 characters from the unambiguous alphabet tracking codes use
    /// (mcp-tools.md §create_campaign, CLAUDE.md §Conventions): no <c>0</c>, <c>O</c>, <c>1</c>,
    /// <c>I</c> or <c>L</c>, so an id read off a screen or a printed sheet cannot be mistyped.
    /// </summary>
    [GeneratedRegex("^cmp_[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{6}$")]
    private static partial Regex CampaignId();

    /// <summary>The folder name is <c>yyyy-MM &lt;Name&gt;</c> (implementation-plan C1).</summary>
    [GeneratedRegex(@"^\d{4}-\d{2} .+$")]
    private static partial Regex FolderName();

    /// <summary>The folders POC-3 requires under <c>PROSPECT_STUDIO_HOME</c>.</summary>
    private static readonly string[] WorkspaceFolders = ["Brand Kit", "Dealers", "Suppression", "Templates", "Campaigns"];

    // ── tools/list ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("create_campaign")]
    [InlineData("list_campaigns")]
    [InlineData("get_campaign")]
    [InlineData("save_search_profile")]
    public async Task Tools_list_advertises_the_campaign_tool_with_a_description(string name)
    {
        var tool = await ToolSchemas.FindAsync(server.Client, name);

        tool.Description.ShouldNotBeNullOrWhiteSpace(
            "Claude picks tools from their descriptions, so every tool needs one (mcp-tools.md §Conventions).");
    }

    [Fact]
    public async Task Create_campaign_takes_the_documented_parameters()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "create_campaign");

        ToolSchemas.Properties(tool).ShouldBe(["name", "product", "notes"], ignoreOrder: true, ToolSchemas.Describe(tool));
        ToolSchemas.Required(tool).ShouldBe(["name"], ToolSchemas.Describe(tool));
    }

    [Fact]
    public async Task List_campaigns_needs_no_parameters()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "list_campaigns");

        ToolSchemas.Required(tool).ShouldBeEmpty(ToolSchemas.Describe(tool));
    }

    [Fact]
    public async Task Get_campaign_requires_a_campaign_id()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "get_campaign");

        ToolSchemas.Properties(tool).ShouldContain("campaignId", ToolSchemas.Describe(tool));
        ToolSchemas.Required(tool).ShouldBe(["campaignId"], ToolSchemas.Describe(tool));
    }

    // ── create_campaign ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_campaign_returns_only_a_campaign_id_and_a_folder()
    {
        var name = Unique("Returns Id And Folder");

        var payload = await CreateAsync(name, product: "Scissor & boom lifts", notes: "");

        payload.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["campaignId", "folder"],
            ignoreOrder: true,
            "mcp-tools.md §create_campaign returns exactly { campaignId, folder }.");
    }

    [Fact]
    public async Task Create_campaign_returns_an_id_of_cmp_plus_six_characters()
    {
        var payload = await CreateAsync(Unique("Id Shape"));

        var id = payload.GetProperty("campaignId").GetString();
        id.ShouldNotBeNull();
        CampaignId().IsMatch(id).ShouldBeTrue(
            $"'{id}' is not 'cmp_' plus 6 characters (CLAUDE.md §Conventions, e.g. cmp_7Q3KXM).");
    }

    [Fact]
    public async Task Create_campaign_creates_the_folder_under_campaigns_named_year_month_and_name()
    {
        var name = Unique("Folder On Disk");

        var folder = (await CreateAsync(name)).GetProperty("folder").GetString();

        folder.ShouldNotBeNullOrWhiteSpace();
        Path.IsPathFullyQualified(folder).ShouldBeTrue($"'{folder}' must be an absolute path.");
        Directory.Exists(folder).ShouldBeTrue($"create_campaign must create the folder; '{folder}' does not exist.");

        var leaf = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        FolderName().IsMatch(leaf).ShouldBeTrue($"'{leaf}' is not 'yyyy-MM <Name>' (implementation-plan C1).");
        CurrentMonths().ShouldContain(leaf[..7], $"'{leaf}' does not start with the current year and month.");
        leaf.EndsWith(name, StringComparison.Ordinal).ShouldBeTrue(
            $"'{leaf}' must keep the campaign name after the 'yyyy-MM ' prefix.");

        Path.GetFileName(Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar)))
            .ShouldBe("Campaigns", "campaign folders live under the workspace's 'Campaigns' folder (technical-design §5.1).");
        folder.StartsWith(Path.GetFullPath(server.Home), StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
            $"'{folder}' is not inside PROSPECT_STUDIO_HOME ('{server.Home}').");
    }

    [Fact]
    public async Task Create_campaign_accepts_a_name_on_its_own()
    {
        var payload = await CreateAsync(Unique("Name Only"), product: null, notes: null);

        payload.GetProperty("campaignId").GetString().ShouldNotBeNullOrWhiteSpace(
            "product and notes are optional in mcp-tools.md §create_campaign.");
    }

    [Fact]
    public async Task Create_campaign_rejects_a_duplicate_name_with_CONFLICT()
    {
        var name = Unique("Duplicate Name");
        await CreateAsync(name);

        var error = await ToolCall.ErrorAsync(
            server.Client,
            "create_campaign",
            new Dictionary<string, object?> { ["name"] = name },
            server.StandardError);

        error.Code.ShouldBe("CONFLICT", $"mcp-tools.md §Errors maps a duplicate name to CONFLICT. Got: {error.RawJson}");
        error.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Create_campaign_rejects_a_duplicate_name_that_differs_only_in_case_with_CONFLICT()
    {
        var name = Unique("Case Insensitive Duplicate");
        await CreateAsync(name);

        var error = await ToolCall.ErrorAsync(
            server.Client,
            "create_campaign",
            new Dictionary<string, object?> { ["name"] = name.ToUpperInvariant() },
            server.StandardError);

        error.Code.ShouldBe(
            "CONFLICT",
            "duplicate detection matches on the normalized name (the campaign's slug), because CLAUDE.md "
            + $"forbids depending on SQLite's case-sensitive text comparison. Got: {error.RawJson}");
    }

    [Fact]
    public async Task Create_campaign_sanitizes_invalid_path_characters_into_a_usable_folder_name()
    {
        // Every character from implementation-plan C1's list: / : * ? " < > |
        var name = Unique("""Q4 Lifts: Houston/Galveston <Fall> "Big" ?*|""");

        var payload = await CreateAsync(name);
        var folder = payload.GetProperty("folder").GetString();

        folder.ShouldNotBeNullOrWhiteSpace();
        Directory.Exists(folder).ShouldBeTrue($"the sanitized folder '{folder}' must exist on disk.");

        var leaf = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        var offenders = leaf.Where(character => Path.GetInvalidFileNameChars().Contains(character)).ToList();
        offenders.ShouldBeEmpty($"'{leaf}' still holds characters Windows cannot use in a folder name.");
        FolderName().IsMatch(leaf).ShouldBeTrue($"'{leaf}' is not 'yyyy-MM <Name>'.");

        // Sanitizing is a file-system concern only: the campaign keeps the name the user typed.
        var campaign = await GetAsync(payload.GetProperty("campaignId").GetString()!);
        campaign.GetProperty("name").GetString().ShouldBe(name);
    }

    // ── get_campaign ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_campaign_returns_the_name_and_folder_it_was_created_with()
    {
        var name = Unique("Get Identity");
        var created = await CreateAsync(name, product: "Scissor & boom lifts");
        var id = created.GetProperty("campaignId").GetString()!;

        var campaign = await GetAsync(id);

        campaign.GetProperty("campaignId").GetString().ShouldBe(id);
        campaign.GetProperty("name").GetString().ShouldBe(name);
        campaign.GetProperty("folder").GetString().ShouldBe(created.GetProperty("folder").GetString());
    }

    [Fact]
    public async Task Get_campaign_returns_zero_counts_for_a_brand_new_campaign()
    {
        var created = await CreateAsync(Unique("Zero Counts"));

        var campaign = await GetAsync(created.GetProperty("campaignId").GetString()!);

        campaign.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["campaignId", "name", "folder", "status", "product", "profile", "geoLabel", "counts", "lastExportAt", "lastRenderAt"],
            ignoreOrder: true,
            $"mcp-tools.md §get_campaign documents exactly these fields. Got: {campaign}");

        // §get_campaign: each of these is present and null before the step that fills it, never absent,
        // so a skill can read the field instead of probing for it.
        foreach (var field in new[] { "profile", "geoLabel", "lastExportAt", "lastRenderAt" })
        {
            campaign.TryGetProperty(field, out var value).ShouldBeTrue(
                $"'{field}' must be present even with no value yet (mcp-tools.md §get_campaign). Got: {campaign}");
            value.ValueKind.ShouldBe(
                JsonValueKind.Null,
                $"'{field}' is null for a brand new campaign; geoLabel stays null for all of C1. Got: {campaign}");
        }

        campaign.TryGetProperty("counts", out var counts).ShouldBeTrue(
            "mcp-tools.md §get_campaign returns counts by status, tier and dealer. Got: " + campaign);
        counts.ValueKind.ShouldBe(JsonValueKind.Object);

        // "Counts are empty objects/arrays for a new campaign, not absent" (mcp-tools.md §get_campaign).
        foreach (var (grouping, kind) in new[]
                 {
                     ("byStatus", JsonValueKind.Object),
                     ("byTier", JsonValueKind.Object),
                     ("byDealer", JsonValueKind.Array),
                 })
        {
            counts.TryGetProperty(grouping, out var value).ShouldBeTrue(
                $"counts.{grouping} must be present for a campaign with no leads, not absent. Got: {counts}");
            value.ValueKind.ShouldBe(kind, $"counts.{grouping} has the wrong shape. Got: {counts}");
        }

        var nonZero = new List<string>();
        var nonEmpty = new List<string>();
        Walk(counts, "/counts", nonZero, nonEmpty);

        nonZero.ShouldBeEmpty($"a campaign with no leads has no non-zero counts. Got: {counts}");
        nonEmpty.ShouldBeEmpty($"a campaign with no leads has no populated groupings. Got: {counts}");
    }

    [Fact]
    public async Task Get_campaign_rejects_an_unknown_id_with_NOT_FOUND()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "get_campaign",
            new Dictionary<string, object?> { ["campaignId"] = "cmp_ZZZZZZ" },
            server.StandardError);

        error.Code.ShouldBe("NOT_FOUND", $"mcp-tools.md §Errors maps an unknown campaign to NOT_FOUND. Got: {error.RawJson}");
        error.Message.ShouldNotBeNullOrWhiteSpace();
    }

    // ── list_campaigns ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_campaigns_returns_the_documented_row_for_a_campaign_that_was_just_created()
    {
        var name = Unique("Listed");
        var created = await CreateAsync(name, product: "Scissor & boom lifts");
        var id = created.GetProperty("campaignId").GetString()!;

        var payload = await ToolCall.OkAsync(server.Client, "list_campaigns", diagnostics: server.StandardError);

        payload.TryGetProperty("total", out var total).ShouldBeTrue(
            $"mcp-tools.md §Conventions: a list always returns 'total'. Got: {payload}");
        total.ValueKind.ShouldBe(JsonValueKind.Number);

        payload.TryGetProperty("campaigns", out var campaigns).ShouldBeTrue(
            $"list_campaigns returns a 'campaigns' array. Got: {payload}");
        campaigns.ValueKind.ShouldBe(JsonValueKind.Array);

        var rows = campaigns.EnumerateArray().ToList();
        total.GetInt32().ShouldBeGreaterThanOrEqualTo(rows.Count, "'total' counts every campaign, not just this page.");

        var row = Row(rows, id);
        row.ValueKind.ShouldBe(
            JsonValueKind.Object,
            $"'{id}' is missing from list_campaigns: {payload}{Environment.NewLine}If the campaign exists but is "
            + "not listed, check the default paging limit and sort order (mcp-tools.md §list_campaigns: 100 by "
            + "default, newest first); the tests in this class share one server.");

        row.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["campaignId", "name", "folder", "status", "product", "createdAt", "leads"],
            ignoreOrder: true,
            $"mcp-tools.md §list_campaigns documents exactly these row fields. Got: {row}");

        row.GetProperty("name").GetString().ShouldBe(name);
        row.GetProperty("folder").GetString().ShouldBe(created.GetProperty("folder").GetString());
        row.GetProperty("status").GetString().ShouldBe("draft", $"a new campaign is a draft. Got: {row}");
        row.GetProperty("product").GetString().ShouldBe("Scissor & boom lifts");
        row.GetProperty("leads").GetInt32().ShouldBe(0, $"a new campaign has no leads. Got: {row}");

        var createdAt = row.GetProperty("createdAt").GetString();
        createdAt.ShouldNotBeNullOrWhiteSpace();
        DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
            .ShouldBeTrue($"'createdAt' must be an ISO-8601 timestamp (mcp-tools.md §list_campaigns). Got: {createdAt}");
    }

    [Fact]
    public async Task List_campaigns_pages_with_limit_and_offset()
    {
        // Two of its own, so the assertions hold however many campaigns the shared server already has.
        await CreateAsync(Unique("Paged A"));
        await CreateAsync(Unique("Paged B"));

        var everything = await ListAsync(limit: 500, offset: 0);
        var total = everything.GetProperty("total").GetInt32();
        total.ShouldBeGreaterThanOrEqualTo(2);

        var firstPage = await ListAsync(limit: 1, offset: 0);
        var secondPage = await ListAsync(limit: 1, offset: 1);

        firstPage.GetProperty("campaigns").GetArrayLength().ShouldBe(1, $"limit=1 returns one row. Got: {firstPage}");
        secondPage.GetProperty("campaigns").GetArrayLength().ShouldBe(1, $"limit=1 returns one row. Got: {secondPage}");
        firstPage.GetProperty("total").GetInt32().ShouldBe(total, "'total' does not change with paging.");
        secondPage.GetProperty("total").GetInt32().ShouldBe(total, "'total' does not change with paging.");

        Id(firstPage).ShouldNotBe(Id(secondPage), "offset=1 must skip the row offset=0 returned.");

        static string? Id(JsonElement page) =>
            page.GetProperty("campaigns")[0].GetProperty("campaignId").GetString();
    }

    // ── POC-3: the workspace ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_server_creates_the_five_workspace_folders_on_first_use()
    {
        // A tool call proves the server is up and has finished its bootstrap.
        await ToolCall.OkAsync(server.Client, "get_status", diagnostics: server.StandardError);

        foreach (var folder in WorkspaceFolders)
        {
            Directory.Exists(Path.Combine(server.Home, folder)).ShouldBeTrue(
                $"POC-3: '{folder}' must be created under PROSPECT_STUDIO_HOME on first use. Server log:{Environment.NewLine}{server.StandardError}");
        }
    }

    [Fact]
    public async Task The_server_does_not_seed_the_fixture_brand_kit_without_PS_SEED_FIXTURES()
    {
        await ToolCall.OkAsync(server.Client, "get_status", diagnostics: server.StandardError);

        var brandKit = Path.Combine(server.Home, "Brand Kit");
        Directory.Exists(brandKit).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(brandKit).ShouldBeEmpty(
            $"seeding is a dev convenience gated on {TestEnvironment.SeedFixturesVariable}=1 (implementation-plan C1).");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private async Task<JsonElement> CreateAsync(string name, string? product = null, string? notes = null)
    {
        var arguments = new Dictionary<string, object?> { ["name"] = name };
        if (product is not null)
        {
            arguments["product"] = product;
        }

        if (notes is not null)
        {
            arguments["notes"] = notes;
        }

        return await ToolCall.OkAsync(server.Client, "create_campaign", arguments, server.StandardError);
    }

    private async Task<JsonElement> GetAsync(string campaignId) => await ToolCall.OkAsync(
        server.Client,
        "get_campaign",
        new Dictionary<string, object?> { ["campaignId"] = campaignId },
        server.StandardError);

    private async Task<JsonElement> ListAsync(int limit, int offset) => await ToolCall.OkAsync(
        server.Client,
        "list_campaigns",
        new Dictionary<string, object?> { ["limit"] = limit, ["offset"] = offset },
        server.StandardError);

    /// <summary>The row for <paramref name="campaignId"/>, or an undefined element when it is absent.</summary>
    private static JsonElement Row(List<JsonElement> rows, string campaignId) =>
        rows.FirstOrDefault(row =>
            row.ValueKind == JsonValueKind.Object
            && row.TryGetProperty("campaignId", out var value)
            && value.GetString() == campaignId);

    /// <summary>
    /// A name no other test uses, so the shared server can hold them all. The prefix stays readable in
    /// a folder listing when a test fails.
    /// </summary>
    private static string Unique(string label) => $"{label} {Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>Both the local and the UTC year-month, so the test cannot fail around midnight.</summary>
    private static string[] CurrentMonths() =>
    [
        DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture),
    ];

    /// <summary>
    /// Collects every non-zero number and every populated collection under <paramref name="node"/>,
    /// with its JSON pointer, so the assertion works whatever shape the counts take.
    /// </summary>
    private static void Walk(JsonElement node, string pointer, List<string> nonZero, List<string> nonEmpty)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Number:
                if (node.GetDouble() != 0)
                {
                    nonZero.Add($"{pointer} = {node}");
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    Walk(property.Value, $"{pointer}/{property.Name}", nonZero, nonEmpty);
                }

                break;
            case JsonValueKind.Array:
                var items = node.EnumerateArray().ToList();
                if (items.Count > 0)
                {
                    nonEmpty.Add($"{pointer} has {items.Count} entries");
                }

                for (var index = 0; index < items.Count; index++)
                {
                    Walk(items[index], $"{pointer}/{index}", nonZero, nonEmpty);
                }

                break;
            default:
                break;
        }
    }
}
