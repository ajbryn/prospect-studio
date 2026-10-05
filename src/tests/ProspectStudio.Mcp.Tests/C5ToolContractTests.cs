using System.Text.Json;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The tools chunk C5 adds and the ones it changes, against mcp-tools.md §import_list,
/// §list_dealers, §assign_dealers / apply_suppression, §resolve_geography and §find_candidates. This
/// is the M1 contract: a brief becomes hundreds of dealer-assigned candidates with customers
/// suppressed.
/// </summary>
/// <remarks>
/// The fixture server has the three business lists imported through the workspace default path, which
/// is the state the M1 demo starts the search from.
/// </remarks>
[Collection(DealerServerCollection.Name)]
public class C5ToolContractTests(DealerServerFixture server)
{
    [Theory]
    [InlineData("import_list")]
    [InlineData("list_dealers")]
    [InlineData("assign_dealers")]
    [InlineData("apply_suppression")]
    public async Task The_tool_is_advertised_with_a_description(string tool)
    {
        var listed = await ToolSchemas.FindAsync(server.Client, tool);

        listed.Description.ShouldNotBeNullOrWhiteSpace(
            "Claude picks tools from their descriptions, so every tool needs one.");
    }

    [Fact]
    public async Task Import_list_takes_the_documented_parameters()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "import_list");

        ToolSchemas.Properties(tool).ShouldBe(
            ["kind", "path", "reason", "replace"],
            ignoreOrder: true,
            "mcp-tools.md §import_list: { kind, path (optional; defaults to the workspace folder file), "
            + "reason }, plus the 'replace' flag the C5 review added so a suppression row dropped from "
            + $"the file can stop suppressing. {ToolSchemas.Describe(tool)}");

        ToolSchemas.Required(tool).ShouldBe(
            ["kind"],
            ignoreOrder: true,
            $"only the kind is required; path defaults to the workspace file. {ToolSchemas.Describe(tool)}");
    }

    [Theory]
    [InlineData("assign_dealers")]
    [InlineData("apply_suppression")]
    public async Task The_routing_tools_take_only_a_campaign(string tool)
    {
        var listed = await ToolSchemas.FindAsync(server.Client, tool);

        ToolSchemas.Properties(listed).ShouldBe(
            ["campaignId"],
            ignoreOrder: true,
            $"mcp-tools.md: 'Input: {{ \"campaignId\": \"...\" }} -> counts changed'. {ToolSchemas.Describe(listed)}");
        ToolSchemas.Required(listed).ShouldBe(["campaignId"], ignoreOrder: true, ToolSchemas.Describe(listed));
    }

    [Fact]
    public async Task List_dealers_needs_no_arguments()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "list_dealers");

        ToolSchemas.Properties(tool).ShouldBeEmpty(
            $"mcp-tools.md §list_dealers documents output only. {ToolSchemas.Describe(tool)}");
    }

    [Fact]
    public void Import_list_with_no_path_reads_the_workspace_folder_file()
    {
        // The fixture imported all three lists with no `path`, from Dealers\ and Suppression\
        // (technical-design §5.1). These are the first-import counts.
        foreach (var (kind, expected) in new[] { ("dealers", 3), ("territories", 35), ("suppression", 7) })
        {
            var result = server.FirstImport[kind];

            result.EnumerateObject().Select(property => property.Name).ShouldBe(
                ["imported", "updated", "errors"],
                ignoreOrder: true,
                $"mcp-tools.md §import_list returns {{imported, updated, errors}}. Got: {result}");

            result.GetProperty("errors").EnumerateArray().ShouldBeEmpty(
                $"the committed {kind} list is clean. Got: {result}");
            result.GetProperty("imported").GetInt32().ShouldBe(
                expected,
                $"'{kind}' was imported from the workspace folder with no path given, so this also "
                + $"proves the default resolved to the right file. Got: {result}");
        }
    }

    [Fact]
    public async Task Import_list_accepts_an_explicit_xlsx_path_and_re_importing_adds_nothing()
    {
        // mcp-tools.md §import_list: "XLSX accepted with the same headers on the first sheet", and
        // re-importing is idempotent. The XLSX holds the same rows as the CSV the fixture imported, so
        // this proves both at once and leaves the server's state exactly as it was.
        var before = await ToolCall.OkAsync(server.Client, "list_dealers", diagnostics: server.Diagnostics);

        var result = await ToolCall.OkAsync(
            server.Client,
            "import_list",
            new Dictionary<string, object?>
            {
                ["kind"] = "territories",
                ["path"] = RepoFixtures.ListFixture("territories.xlsx"),
            },
            server.Diagnostics);

        result.GetProperty("errors").EnumerateArray().ShouldBeEmpty($"Got: {result}");
        result.GetProperty("imported").GetInt32().ShouldBe(
            0,
            "the same 35 rows are already there, so nothing is created. If this is 35 the territory rows "
            + $"have been doubled and every byDealer count with them. Got: {result}");
        result.GetProperty("updated").GetInt32().ShouldBeGreaterThan(
            0,
            $"the rows were read and matched, not skipped unseen. Got: {result}");

        var after = await ToolCall.OkAsync(server.Client, "list_dealers", diagnostics: server.Diagnostics);
        after.ToString().ShouldBe(before.ToString(), "the counts are unchanged after a re-import.");
    }

    [Fact]
    public async Task Import_list_reports_the_row_number_of_a_bad_dealer_id_and_imports_the_rest()
    {
        var result = await ToolCall.OkAsync(
            server.Client,
            "import_list",
            new Dictionary<string, object?>
            {
                ["kind"] = "territories",
                ["path"] = RepoFixtures.ListFixture("territories_bad_dealer.csv"),
            },
            server.Diagnostics);

        var errors = result.GetProperty("errors").EnumerateArray().ToList();
        var error = errors.ShouldHaveSingleItem($"one row names 'gulff'; the other four are fine. Got: {result}");

        error.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["row", "message"],
            ignoreOrder: true,
            $"mcp-tools.md §import_list: errors are [{{ row, message }}]. Got: {result}");
        error.GetProperty("row").GetInt32().ShouldBe(
            4,
            "the header is row 1, so the broken row is row 4 - the number the user sees when they open "
            + $"the file. Got: {result}");
        (error.GetProperty("message").GetString() ?? string.Empty).ShouldContain(
            "gulff",
            Case.Insensitive,
            $"the message names the value that was wrong. Got: {result}");

        // The four good rows already exist, so nothing was created and nothing was lost; restore the
        // full list anyway, because the next test in this collection reads it.
        var restored = await server.ImportAsync("territories");
        restored.GetProperty("errors").EnumerateArray().ShouldBeEmpty($"Got: {restored}");

        var status = await ToolCall.OkAsync(server.Client, "get_status", diagnostics: server.Diagnostics);
        status.GetProperty("ready").GetProperty("territories").GetInt32().ShouldBe(
            35,
            "a file with one bad row must not take the other 31 stored rows with it. "
            + $"Got: {status.GetProperty("ready")}");
    }

    [Fact]
    public async Task Import_list_warranty_with_an_explicit_path_is_UNSUPPORTED()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "import_list",
            new Dictionary<string, object?>
            {
                ["kind"] = "warranty",
                ["path"] = RepoFixtures.Fixture("warranty-sample.csv"),
            },
            server.Diagnostics);

        error.Code.ShouldBe(
            "UNSUPPORTED",
            "mcp-tools.md §import_list advertises four kinds and §Errors reserves UNSUPPORTED for "
            + "'Feature not built yet'. Warranty arrives in C13. Raw: " + error.RawJson);
        error.Hint.ShouldNotBeNullOrWhiteSpace("the hint should name the chunk, as §Errors' example does.");
    }

    [Fact]
    public async Task Import_list_warranty_with_no_path_is_UNSUPPORTED_rather_than_NOT_FOUND()
    {
        // The route that gets this wrong. With no `path`, the service resolves the workspace default
        // first, finds no warranty file in Dealers\ and reports NOT_FOUND - which sends the user off to
        // create a file for a feature that does not exist. The kind has to be refused before the disk is
        // consulted, and the two routes have to agree.
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "import_list",
            new Dictionary<string, object?> { ["kind"] = "warranty" },
            server.Diagnostics);

        error.Code.ShouldBe(
            "UNSUPPORTED",
            "a NOT_FOUND here is a file-shaped answer to a feature-shaped question: the user would go "
            + "and save warranty.csv into the Dealers folder and get the same error again. Raw: "
            + error.RawJson);
    }

    [Fact]
    public async Task Import_list_advertises_replace_as_a_boolean_that_is_not_required()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "import_list");
        var schema = ToolSchemas.Describe(tool);

        ToolSchemas.Required(tool).ShouldBe(
            ["kind"],
            ignoreOrder: true,
            "replace defaults to false: upsert is the safe direction, because a stale suppression entry "
            + $"only costs a lead while dropping a stale 'dnc' row risks contacting someone who asked "
            + $"not to be. {schema}");

        using var parsed = JsonDocument.Parse(schema);

        parsed.RootElement
            .GetProperty("properties")
            .TryGetProperty("replace", out var replace)
            .ShouldBeTrue($"the parameter has to be advertised, or no skill can reach it. {schema}");

        replace.GetProperty("type").GetString().ShouldBe(
            "boolean",
            "a flag, not a string: 'replace' deletes rows, and a skill passing \"false\" as text must "
            + $"not be read as true. {schema}");
    }

    [Fact]
    public async Task Import_list_with_an_unknown_kind_is_VALIDATION_FAILED()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "import_list",
            new Dictionary<string, object?> { ["kind"] = "dealerz" },
            server.Diagnostics);

        error.Code.ShouldBe(
            "VALIDATION_FAILED",
            "mcp-tools.md §Errors: 'Schema or rule violation'. mcp-tools.md §import_list names four "
            + "kinds, so a fifth is a rule violation rather than an empty import. Raw: " + error.RawJson);
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Import_list_with_a_path_that_does_not_exist_is_a_documented_error()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "import_list",
            new Dictionary<string, object?>
            {
                ["kind"] = "dealers",
                ["path"] = Path.Combine(server.DealersFolder, "no-such-file.csv"),
            },
            server.Diagnostics);

        // mcp-tools.md §Errors has no code for "that file is not there": NOT_FOUND is described as an
        // unknown campaign/lead/template/job, and VALIDATION_FAILED as a schema or rule violation.
        // Either reading is defensible and inventing a code is not, so what is pinned here is that the
        // failure is one of the documented codes rather than INTERNAL, and that the hint names the path.
        error.Code.ShouldBeOneOf(
            ["NOT_FOUND", "VALIDATION_FAILED"],
            "an INTERNAL here would hide a plain typo behind 'something went wrong; check the log'. "
            + "Raw: " + error.RawJson);
        (error.Hint ?? string.Empty).ShouldNotBeNullOrWhiteSpace(
            "the hint has to say where the server looked, or the user has no way to find their file.");
    }

    [Fact]
    public async Task List_dealers_returns_each_dealer_with_its_branch_and_territory_counts()
    {
        var payload = await ToolCall.OkAsync(server.Client, "list_dealers", diagnostics: server.Diagnostics);

        payload.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["dealers"],
            $"mcp-tools.md §list_dealers returns {{dealers:[...]}}. Got: {payload}");

        var dealers = payload.GetProperty("dealers").EnumerateArray().ToList();
        dealers.Count.ShouldBe(3, $"Got: {payload}");

        foreach (var dealer in dealers)
        {
            dealer.EnumerateObject().Select(property => property.Name).ShouldBe(
                ["id", "name", "branches", "territoryRows"],
                ignoreOrder: true,
                $"mcp-tools.md §list_dealers' own example names exactly these. Got: {dealer}");
        }

        var byId = dealers.ToDictionary(
            dealer => dealer.GetProperty("id").GetString() ?? string.Empty,
            dealer => dealer,
            StringComparer.Ordinal);

        byId["gulf"].GetProperty("name").GetString().ShouldBe("Gulf Lift Equipment");
        byId["gulf"].GetProperty("branches").GetInt32().ShouldBe(1);
        byId["gulf"].GetProperty("territoryRows").GetInt32().ShouldBe(4, "four county rows, no ZIP overrides.");
        byId["bay"].GetProperty("territoryRows").GetInt32().ShouldBe(18, "3 counties + 15 ZIPs.");
        byId["pine"].GetProperty("territoryRows").GetInt32().ShouldBe(13, "2 counties + 11 ZIPs.");
    }

    [Fact]
    public async Task Get_status_reports_the_list_counts_instead_of_zeroes()
    {
        var ready = (await ToolCall.OkAsync(server.Client, "get_status", diagnostics: server.Diagnostics))
            .GetProperty("ready");

        ready.GetProperty("dealers").GetInt32().ShouldBe(
            3,
            "mcp-tools.md §get_status: C0 shipped these as hardcoded zeroes, and C5 is where they become "
            + $"facts. It is how a skill knows whether import_list still has to run. Got: {ready}");
        ready.GetProperty("territories").GetInt32().ShouldBe(35, $"Got: {ready}");
        ready.GetProperty("suppression").GetInt32().ShouldBe(7, $"Got: {ready}");
    }

    [Fact]
    public async Task Resolve_geography_for_a_dealer_returns_its_territory_as_a_scope()
    {
        var scope = await ToolCall.OkAsync(
            server.Client,
            "resolve_geography",
            new Dictionary<string, object?> { ["type"] = "dealer", ["values"] = new[] { "bay" } },
            server.Diagnostics);

        scope.GetProperty("type").GetString().ShouldBe(
            "dealer",
            $"technical-design §6.2 lists 'dealer' among the GeoScope types. Got: {scope}");

        foreach (var key in new[] { "type", "label", "cbsa", "states", "countyFips", "zips", "radius", "bbox" })
        {
            scope.TryGetProperty(key, out _).ShouldBeTrue(
                $"'every GeoScope key is always present, using [] or null where it doesn't apply' "
                + $"(mcp-tools.md §resolve_geography). Missing '{key}'. Got: {scope}");
        }

        (scope.GetProperty("label").GetString() ?? string.Empty).ShouldContain(
            "Bayport",
            Case.Insensitive,
            $"the label is what a person reads back, so it names the dealer. Got: {scope}");

        var zips = scope.GetProperty("zips").EnumerateArray().Select(value => value.GetString()).ToList();
        zips.Count.ShouldBe(15, $"bay's fifteen ZIP overrides. Got: {scope}");
        zips.ShouldContain("77506", "the Pasadena override the plan names.");

        var counties = scope.GetProperty("countyFips").EnumerateArray()
            .Select(value => value.GetString())
            .Order(StringComparer.Ordinal)
            .ToList();

        counties.ShouldBe(
            ["48039", "48071", "48167", "48201"],
            "technical-design §6.2: 'dealer: dealer:<id> -> union of its territory ZIPs and counties'. "
            + "Brazoria, Chambers and Galveston come from bay's county rules; Harris 48201 comes from "
            + "its fifteen ZIP overrides, which all sit in Harris - and a county list without it would "
            + "make bay's own Pasadena and Baytown ZIPs unsearchable, because the candidate query reads "
            + $"the Parquet by county. Got: {scope}");

        scope.GetProperty("states").EnumerateArray().Select(value => value.GetString()).ShouldBe(["TX"]);
        scope.GetProperty("bbox").EnumerateArray().Count().ShouldBe(4, $"Got: {scope}");
    }

    [Fact]
    public async Task Resolve_geography_for_an_unknown_dealer_is_NOT_FOUND_rather_than_UNSUPPORTED()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "resolve_geography",
            new Dictionary<string, object?> { ["type"] = "dealer", ["values"] = new[] { "nope" } },
            server.Diagnostics);

        error.Code.ShouldBe(
            "NOT_FOUND",
            "mcp-tools.md §resolve_geography reserved UNSUPPORTED for 'type dealer until C5'. Once C5 "
            + "lands, an unknown dealer id is a NOT_FOUND - an empty scope would resolve to a market of "
            + "zero and read as a real answer. Raw: " + error.RawJson);
        error.Hint.ShouldNotBeNullOrWhiteSpace("the hint should point at list_dealers.");
    }

    [Fact]
    public async Task Find_candidates_now_assigns_dealers_and_suppresses_without_being_asked()
    {
        var summary = await FindAsync(server.CampaignId);

        summary.GetProperty("stored").GetInt32().ShouldBe(
            83,
            "86 of the 117 Houston rows survive the profile and its exclusions; dedupe collapses three. "
            + "src/tests/Fixtures/places/README.md carries the arithmetic row by row. Got: " + summary);
        summary.GetProperty("duplicates").GetInt32().ShouldBe(3, "Got: " + summary);
        summary.GetProperty("found").GetInt32().ShouldBe(
            86,
            "mcp-tools.md: 'found = stored + duplicates'. Suppressed leads are still stored rows - they "
            + "have status 'suppressed' - so they are not subtracted here. Got: " + summary);

        var suppressed = summary.GetProperty("suppressed");
        suppressed.ValueKind.ShouldBe(JsonValueKind.Object);

        foreach (var (reason, count) in new[] { ("dealer", 3), ("customer", 2), ("dnc", 1), ("competitor", 1) })
        {
            suppressed.TryGetProperty(reason, out var value).ShouldBeTrue(
                $"mcp-tools.md §find_candidates shows suppressed as counts per reason, and the fixture "
                + $"list has a '{reason}' row with a lead to remove. Got: {suppressed}");
            value.GetInt32().ShouldBe(count, $"Got: {suppressed}");
        }

        suppressed.EnumerateObject().Sum(property => property.Value.GetInt32()).ShouldBe(
            7,
            $"three dealers, two customers, one dnc and one competitor. Got: {suppressed}");

        var byDealer = summary.GetProperty("byDealer").EnumerateArray().ToList();
        byDealer.Count.ShouldBe(
            3,
            "byDealer stopped being empty in C5. Got: " + summary);

        foreach (var row in byDealer)
        {
            row.EnumerateObject().Select(property => property.Name).ShouldBe(
                ["dealer", "count"],
                ignoreOrder: true,
                $"mcp-tools.md §find_candidates: byDealer is [{{ dealer, count }}] and 'dealer' is the "
                + $"name a person reads, as in its own example. Got: {row}");
        }

        var counts = byDealer.ToDictionary(
            row => row.GetProperty("dealer").GetString() ?? string.Empty,
            row => row.GetProperty("count").GetInt32(),
            StringComparer.Ordinal);

        counts.ShouldContainKeyAndValue("Gulf Lift Equipment", 32, "Got: " + summary);
        counts.ShouldContainKeyAndValue("Bayport Aerial Supply", 24, "Got: " + summary);
        counts.ShouldContainKeyAndValue("Pineland Equipment", 19, "Got: " + summary);

        summary.GetProperty("coverageGaps").GetInt32().ShouldBe(
            1,
            "the San Jacinto lead: a real Houston CBSA county with no territory row. Got: " + summary);

        (counts.Values.Sum() + summary.GetProperty("coverageGaps").GetInt32()).ShouldBe(
            83 - 7,
            "every lead that is not suppressed is either routed or a gap - that is chunk C5's goal. A "
            + "suppressed company is never mailed, so it is counted under neither. Got: " + summary);
    }

    [Fact]
    public async Task Find_candidates_reaches_the_M1_threshold_with_customers_removed()
    {
        // requirements §6 / the M1 milestone: "a brief becomes hundreds of dealer-assigned candidates
        // with customers suppressed". The fixture is 120 rows rather than hundreds, so what is pinned
        // here is the shape of the claim: every dealer has leads, none of the suppressed companies does.
        var summary = await FindAsync(server.CampaignId);
        var sampled = summary.GetProperty("sample").EnumerateArray().Select(lead => lead.ToString()).ToList();

        foreach (var name in new[] { "Gulf Lift Equipment", "Bayport Aerial Supply", "Pineland Equipment", "Apex Aerial Rentals", "Northfield Storage" })
        {
            sampled.ShouldNotContain(
                row => row.Contains(name, StringComparison.OrdinalIgnoreCase),
                $"'{name}' is on the suppression list, so it must not be offered as a candidate. "
                + $"Got: {summary}");
        }
    }

    [Fact]
    public async Task Assign_dealers_and_apply_suppression_are_separately_re_runnable()
    {
        var campaignId = await server.NewCampaignWithProfileAsync();
        await FindAsync(campaignId);

        var suppression = await RouteAsync("apply_suppression", campaignId);
        var assignment = await RouteAsync("assign_dealers", campaignId);

        // find_candidates already ran both, so a call straight afterwards must change nothing.
        suppression.GetProperty("changed").GetInt32().ShouldBe(
            0,
            "mcp-tools.md §assign_dealers / apply_suppression: 'counts changed'. find_candidates ran "
            + $"suppression already, so a second pass changes nothing. Got: {suppression}");
        assignment.GetProperty("changed").GetInt32().ShouldBe(0, $"Got: {assignment}");

        var second = await RouteAsync("assign_dealers", campaignId);
        second.GetProperty("changed").GetInt32().ShouldBe(0, $"NFR-3. Got: {second}");
    }

    [Theory]
    [InlineData("assign_dealers")]
    [InlineData("apply_suppression")]
    public async Task The_routing_tools_report_NOT_FOUND_for_an_unknown_campaign(string tool)
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            tool,
            new Dictionary<string, object?> { ["campaignId"] = "cmp_NOPE99" },
            server.Diagnostics);

        error.Code.ShouldBe("NOT_FOUND", "mcp-tools.md §Errors. Raw: " + error.RawJson);
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Get_campaign_counts_by_dealer_after_a_search()
    {
        var campaignId = await server.NewCampaignWithProfileAsync();
        await FindAsync(campaignId);

        var campaign = await ToolCall.OkAsync(
            server.Client,
            "get_campaign",
            new Dictionary<string, object?> { ["campaignId"] = campaignId },
            server.Diagnostics);

        var counts = campaign.GetProperty("counts");

        counts.GetProperty("byStatus").GetProperty("suppressed").GetInt32().ShouldBe(
            7,
            "mcp-tools.md §get_campaign returns counts by status, and 'suppressed' is one of §5.2's "
            + $"statuses. Got: {counts}");
        counts.GetProperty("byStatus").GetProperty("candidate").GetInt32().ShouldBe(
            76,
            $"83 leads less the 7 suppressed. Got: {counts}");

        counts.GetProperty("byDealer").EnumerateArray().Count().ShouldBe(
            3,
            $"the workbook and the dealer packets both read this. Got: {counts}");
    }

    [Fact]
    public async Task The_summary_still_fits_in_the_token_budget_with_the_new_breakdowns()
    {
        var summary = await FindAsync(server.CampaignId);
        var bytes = JsonSerializer.Serialize(summary).Length;

        bytes.ShouldBeLessThan(
            16 * 1024,
            "CLAUDE.md: 'Default responses fit in about 4,000 tokens' (NFR-2). byDealer and suppressed "
            + $"are both small breakdowns, not per-lead lists; this response was {bytes} bytes.");
    }

    private async Task<JsonElement> FindAsync(string campaignId) =>
        await ToolCall.OkAsync(
            server.Client,
            "find_candidates",
            new Dictionary<string, object?> { ["campaignId"] = campaignId },
            server.Diagnostics);

    private async Task<JsonElement> RouteAsync(string tool, string campaignId) =>
        await ToolCall.OkAsync(
            server.Client,
            tool,
            new Dictionary<string, object?> { ["campaignId"] = campaignId },
            server.Diagnostics);
}
