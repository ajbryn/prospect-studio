using System.Text.Json;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The two tools chunk C4 adds, against mcp-tools.md §find_candidates and
/// §lookup_overture_categories: they are advertised with the documented <c>camelCase</c> parameters,
/// the summary is the documented shape and small enough to hand to a skill, and re-running changes
/// nothing.
/// </summary>
/// <remarks>
/// This server has reference data <em>and</em> an Overture extract, which is the state a user is in
/// after <c>prepare_data</c>. The NOT_READY case needs a server without one, so it lives in
/// <see cref="C4NotReadyContractTests"/>.
/// </remarks>
[Collection(CandidateServerCollection.Name)]
public class C4ToolContractTests(CandidateServerFixture server)
{
    /// <summary>
    /// Every key mcp-tools.md §find_candidates shows in its output example. All of them are present
    /// even when C4 cannot fill them: <c>suppressed</c> and <c>byDealer</c> arrive in C5, and an absent
    /// key reads differently from an empty one.
    /// </summary>
    private static readonly string[] SummaryKeys =
    [
        "found", "stored", "duplicates", "suppressed", "coverageGaps", "byCategory", "byDealer", "sample",
    ];

    [Theory]
    [InlineData("find_candidates")]
    [InlineData("lookup_overture_categories")]
    public async Task The_tool_is_advertised_with_a_description(string tool)
    {
        var listed = await ToolSchemas.FindAsync(server.Client, tool);

        listed.Description.ShouldNotBeNullOrWhiteSpace(
            "Claude picks tools from their descriptions, so every tool needs one (mcp-tools.md §Conventions).");
    }

    [Fact]
    public async Task Find_candidates_takes_the_documented_parameters()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "find_candidates");

        ToolSchemas.Properties(tool).ShouldBe(
            [
                "campaignId", "geo", "categories", "keywords", "excludedCategories", "minConfidence",
                "limit", "replace",
            ],
            ignoreOrder: true,
            "mcp-tools.md §find_candidates: an optional excludedCategories parameter overrides the "
            + $"profile's, the same way categories and keywords do. {ToolSchemas.Describe(tool)}");
    }

    [Fact]
    public async Task Find_candidates_requires_only_the_campaign()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "find_candidates");

        ToolSchemas.Required(tool).ShouldBe(
            ["campaignId"],
            ignoreOrder: true,
            "mcp-tools.md marks the rest as defaulting to the profile: 'geo <optional; defaults to "
            + $"profile geography>', 'categories/keywords default to the profile's'. {ToolSchemas.Describe(tool)}");
    }

    [Fact]
    public async Task Lookup_overture_categories_takes_query_state_and_limit()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "lookup_overture_categories");

        ToolSchemas.Properties(tool).ShouldBe(
            ["query", "state", "limit"],
            ignoreOrder: true,
            ToolSchemas.Describe(tool));
    }

    [Fact]
    public async Task Find_candidates_for_an_unknown_campaign_is_NOT_FOUND()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "find_candidates",
            new Dictionary<string, object?> { ["campaignId"] = "cmp_NOPE99" },
            server.Diagnostics);

        error.Code.ShouldBe("NOT_FOUND", "mcp-tools.md §Errors. Raw: " + error.RawJson);
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Find_candidates_summary_is_the_documented_shape()
    {
        var summary = await FindAsync(server.CampaignId);

        summary.EnumerateObject().Select(property => property.Name).ShouldBe(
            SummaryKeys,
            ignoreOrder: true,
            $"mcp-tools.md §find_candidates' output example. Got: {summary}");

        summary.GetProperty("suppressed").ValueKind.ShouldBe(
            JsonValueKind.Object,
            "an object with no reasons yet - suppression is C5. An absent key would make a skill unable "
            + "to tell 'nothing suppressed' from 'this server does not do suppression'.");
        summary.GetProperty("byDealer").ValueKind.ShouldBe(
            JsonValueKind.Array,
            "empty until C5 assigns dealers, but present.");
        summary.GetProperty("coverageGaps").GetInt32().ShouldBe(0, "territories arrive in C5.");
    }

    [Fact]
    public async Task Found_is_stored_plus_duplicates()
    {
        var summary = await FindAsync(server.CampaignId);

        var found = summary.GetProperty("found").GetInt32();
        var stored = summary.GetProperty("stored").GetInt32();
        var duplicates = summary.GetProperty("duplicates").GetInt32();

        found.ShouldBe(
            stored + duplicates,
            "mcp-tools.md's own example adds up that way (2890 + 230 = 3120), so 'found' is the matches "
            + $"after filtering and 'stored' is what survived dedupe. Got: {summary}");
    }

    [Fact]
    public async Task The_fixture_campaign_finds_the_expected_candidates()
    {
        var summary = await FindAsync(server.CampaignId);

        // 112 fixture rows sit in the ten Houston counties. 84 match the profile's categories or
        // keywords at confidence >= 0.6. Two of those go to excluded categories (fx_0075, a restaurant
        // caught by the keyword 'warehouse', and fx_0114) and one to an excluded keyword (fx_0115),
        // leaving 81; dedupe then collapses three duplicates into their primaries, leaving 78 leads.
        // src/tests/Fixtures/places/README.md carries the same arithmetic row by row.
        summary.GetProperty("duplicates").GetInt32().ShouldBe(
            3,
            $"fx_0013 (domain), fx_0014 (name within 15 m) and fx_0113 (name in the same geohash cell). Got: {summary}");
        summary.GetProperty("stored").GetInt32().ShouldBe(
            78,
            "see src/tests/Fixtures/places/README.md for the arithmetic: 112 rows in the ten counties, "
            + "84 match on category or keyword at confidence >= 0.6, two go to excluded categories "
            + "(fx_0075, fx_0114) and one to an excluded keyword (fx_0115), leaving 81; dedupe then "
            + "collapses three. If this is 79 or 80 an exclusion list was skipped; if it is 81 dedupe "
            + $"did not run; if it is 112 or 115 the county or confidence filter did not. Got: {summary}");
    }

    [Fact]
    public async Task Counts_by_category_use_the_real_Overture_names()
    {
        var summary = await FindAsync(server.CampaignId);

        var byCategory = summary.GetProperty("byCategory").EnumerateArray()
            .ToDictionary(
                row => row.GetProperty("category").GetString() ?? string.Empty,
                row => row.GetProperty("count").GetInt32(),
                StringComparer.Ordinal);

        // Counted over the 78 STORED leads, so the three duplicates are not counted twice:
        // 'warehouse' loses fx_0013 and fx_0113, 'metal_fabricator' loses fx_0014.
        byCategory.ShouldContainKeyAndValue("warehouse", 13);
        byCategory.ShouldContainKeyAndValue("industrial_equipment_manufacturer", 3);
        byCategory.ShouldContainKeyAndValue("electrician", 8);
        byCategory.ShouldContainKeyAndValue("metal_fabricator", 6);
        byCategory.ShouldContainKeyAndValue(
            "steel_fabricator",
            1,
            "fx_0106 is counted under its own taxonomy.primary, not under the 'metal_fabricator' that "
            + "matched it through the hierarchy - a breakdown that renamed categories to the segment's "
            + "would hide what was actually found.");
        byCategory.Keys.ShouldNotContain(
            "logistics_service",
            "an invented category name reaching an output means the fixtures or the profile have "
            + "drifted back to the placeholder taxonomy C4 replaced.");
        byCategory.Keys.ShouldNotContain("restaurant", "an excluded category must not appear at all.");
        byCategory.Keys.ShouldNotContain(
            "machine_and_tool_rental",
            "fx_0114 is pulled in by the keyword 'racking' and then dropped for its category, so "
            + "it must not reach the breakdown either.");
        byCategory.Values.Sum().ShouldBe(
            summary.GetProperty("stored").GetInt32(),
            "every stored lead is counted once, so the breakdown and the total cannot disagree.");
    }

    [Fact]
    public async Task Both_profile_exclusion_lists_are_applied_without_being_asked_for()
    {
        var summary = await FindAsync(server.CampaignId);
        var sampled = summary.GetProperty("sample").EnumerateArray().Select(lead => lead.ToString()).ToList();

        // The call passes only campaignId, so both exclusion lists have to come from the saved profile.
        // Before C4 nothing consumed exclusions.overtureCategories or exclusions.keywords at all.
        foreach (var name in new[] { "Warehouse Grill", "Northside Racking", "Coastal Equipment Rental" })
        {
            sampled.ShouldNotContain(
                row => row.Contains(name, StringComparison.OrdinalIgnoreCase),
                $"'{name}' is excluded by the saved profile, so it must not reach a lead. Got: {summary}");
        }
    }

    [Fact]
    public async Task An_excludedCategories_override_replaces_the_profile_list()
    {
        var campaignId = await server.NewCampaignWithProfileAsync();

        // Overriding with an empty list is the sharpest case: it has to mean "exclude nothing", not
        // "fall back to the profile", or a caller could never widen a search.
        var widened = await ToolCall.OkAsync(
            server.Client,
            "find_candidates",
            new Dictionary<string, object?>
            {
                ["campaignId"] = campaignId,
                ["excludedCategories"] = Array.Empty<string>(),
            },
            server.Diagnostics);

        widened.GetProperty("byCategory").EnumerateArray()
            .Select(row => row.GetProperty("category").GetString())
            .ShouldContain(
                "restaurant",
                "with the category exclusions overridden away, the keyword 'warehouse' pulls fx_0075 "
                + $"back in. Got: {widened}");
    }

    [Fact]
    public async Task The_sample_is_at_most_ten_compact_leads()
    {
        var summary = await FindAsync(server.CampaignId);
        var sample = summary.GetProperty("sample").EnumerateArray().ToList();

        sample.Count.ShouldBeLessThanOrEqualTo(10, "mcp-tools.md says '10 compact leads'.");
        sample.ShouldNotBeEmpty("a summary with no example rows gives Claude nothing to look at.");

        foreach (var lead in sample)
        {
            lead.EnumerateObject().Count().ShouldBeLessThanOrEqualTo(
                8,
                $"a sample row is a few fields, not a whole lead: {lead}");
        }
    }

    [Fact]
    public async Task The_whole_response_stays_well_inside_the_token_budget()
    {
        var summary = await FindAsync(server.CampaignId);
        var bytes = JsonSerializer.Serialize(summary).Length;

        bytes.ShouldBeLessThan(
            16 * 1024,
            "CLAUDE.md: 'Default responses fit in about 4,000 tokens' (NFR-2). 16 KB is a generous "
            + $"ceiling for that; this response was {bytes} bytes. A tool that returns every candidate "
            + "would be unusable from a skill.");
    }

    [Fact]
    public async Task Re_running_with_the_same_inputs_changes_nothing()
    {
        var campaignId = await server.NewCampaignWithProfileAsync();

        var first = await FindAsync(campaignId);
        var second = await FindAsync(campaignId);

        foreach (var key in new[] { "found", "stored", "duplicates" })
        {
            second.GetProperty(key).GetInt32().ShouldBe(
                first.GetProperty(key).GetInt32(),
                $"NFR-3: 'Re-running with the same inputs is idempotent.' '{key}' changed. "
                + $"First: {first}{Environment.NewLine}Second: {second}");
        }

        var counts = await ToolCall.OkAsync(
            server.Client,
            "get_campaign",
            new Dictionary<string, object?> { ["campaignId"] = campaignId },
            server.Diagnostics);

        counts.GetProperty("counts").GetProperty("byStatus").GetProperty("candidate").GetInt32().ShouldBe(
            second.GetProperty("stored").GetInt32(),
            "the campaign holds exactly the leads the second run reported, not twice as many.");
    }

    [Fact]
    public async Task Find_candidates_populates_the_campaign_geo_label()
    {
        var campaignId = await server.NewCampaignWithProfileAsync();

        var before = await ToolCall.OkAsync(
            server.Client,
            "get_campaign",
            new Dictionary<string, object?> { ["campaignId"] = campaignId },
            server.Diagnostics);

        before.GetProperty("geoLabel").ValueKind.ShouldBe(
            JsonValueKind.Null,
            "mcp-tools.md §get_campaign: geoLabel is read from the campaign's stored geo_json, and "
            + "find_candidates is the first thing to write it.");

        await FindAsync(campaignId);

        var after = await ToolCall.OkAsync(
            server.Client,
            "get_campaign",
            new Dictionary<string, object?> { ["campaignId"] = campaignId },
            server.Diagnostics);

        after.GetProperty("geoLabel").GetString().ShouldBe(
            "Houston-Pasadena-The Woodlands, TX",
            "the resolved label for the profile's 'Houston metro', CBSA 26420. Anything else means the "
            + $"raw query was echoed rather than resolved. Got: {after}");
    }

    [Fact]
    public async Task An_explicit_scope_overrides_the_profile_geography()
    {
        var campaignId = await server.NewCampaignWithProfileAsync();

        var summary = await ToolCall.OkAsync(
            server.Client,
            "find_candidates",
            new Dictionary<string, object?>
            {
                ["campaignId"] = campaignId,
                ["geo"] = new Dictionary<string, object?> { ["type"] = "counties", ["values"] = new[] { "48339" } },
            },
            server.Diagnostics);

        var stored = summary.GetProperty("stored").GetInt32();

        stored.ShouldBe(
            7,
            "Montgomery County 48339 holds nine fixture rows; eight match the profile at confidence "
            + ">= 0.6, and one of those (fx_0113) is a duplicate of fx_0025, leaving seven leads. A "
            + $"one-county scope must not quietly fall back to the profile's ten. Got: {summary}");

        summary.GetProperty("duplicates").GetInt32().ShouldBe(
            1,
            "dedupe runs inside the scope, so the Katy and Westpark pairs are not in play here. "
            + $"Got: {summary}");
    }

    [Fact]
    public async Task A_confidence_floor_above_every_row_finds_nothing_rather_than_failing()
    {
        var campaignId = await server.NewCampaignWithProfileAsync();

        var summary = await ToolCall.OkAsync(
            server.Client,
            "find_candidates",
            new Dictionary<string, object?> { ["campaignId"] = campaignId, ["minConfidence"] = 0.999 },
            server.Diagnostics);

        summary.GetProperty("found").GetInt32().ShouldBe(
            0,
            "the highest fixture confidence is 0.97, so an empty result is the right answer here - and "
            + "an empty result is a result, not an error.");
        summary.GetProperty("byCategory").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Lookup_overture_categories_returns_real_names_with_in_state_counts()
    {
        var payload = await ToolCall.OkAsync(
            server.Client,
            "lookup_overture_categories",
            new Dictionary<string, object?> { ["query"] = "warehouse", ["state"] = "TX", ["limit"] = 15 },
            server.Diagnostics);

        payload.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["results"],
            $"mcp-tools.md §lookup_overture_categories returns {{results:[...]}}. Got: {payload}");

        var results = payload.GetProperty("results").EnumerateArray().ToList();
        results.ShouldNotBeEmpty();
        results.Count.ShouldBeLessThanOrEqualTo(15, "the limit is honoured.");

        var warehouse = results
            .SingleOrDefault(row => row.GetProperty("category").GetString() == "warehouse");

        warehouse.ValueKind.ShouldBe(
            JsonValueKind.Object,
            $"'warehouse' is a real Overture category and the fixture holds 17 of them. Got: {payload}");
        warehouse.GetProperty("countInState").GetInt32().ShouldBe(17);
        warehouse.GetProperty("path").EnumerateArray().Select(value => value.GetString()).Last()
            .ShouldBe("warehouse", "the path is the hierarchy, root first, leaf last.");
    }

    [Fact]
    public async Task Lookup_overture_categories_never_invents_a_name()
    {
        var payload = await ToolCall.OkAsync(
            server.Client,
            "lookup_overture_categories",
            new Dictionary<string, object?> { ["state"] = "TX", ["limit"] = 50 },
            server.Diagnostics);

        var real = SamplePlaces.All.Select(place => place.TaxonomyPrimary).ToHashSet(StringComparer.Ordinal);

        foreach (var row in payload.GetProperty("results").EnumerateArray())
        {
            real.ShouldContain(
                row.GetProperty("category").GetString() ?? string.Empty,
                "every category comes from the extract itself, so none can be a name that is not in it. "
                + "This tool is what a skill uses to write a search profile, and a hallucinated "
                + "category there produces a profile that matches nothing.");
        }
    }

    private async Task<JsonElement> FindAsync(string campaignId) =>
        await ToolCall.OkAsync(
            server.Client,
            "find_candidates",
            new Dictionary<string, object?> { ["campaignId"] = campaignId },
            server.Diagnostics);
}

/// <summary>
/// <c>find_candidates</c> and <c>lookup_overture_categories</c> against a server that has reference
/// data but <strong>no</strong> Overture extract - which is where every user is before
/// <c>prepare_data</c> downloads 205 MB, and where returning "no candidates" would be a lie.
/// </summary>
[Collection(ReferenceDataServerCollection.Name)]
public class C4NotReadyContractTests(ReferenceDataServerFixture server)
{
    [Fact]
    public async Task Find_candidates_without_an_extract_is_NOT_READY()
    {
        var created = await ToolCall.OkAsync(
            server.Client,
            "create_campaign",
            new Dictionary<string, object?> { ["name"] = "No extract yet" },
            server.Diagnostics);

        var error = await ToolCall.ErrorAsync(
            server.Client,
            "find_candidates",
            new Dictionary<string, object?>
            {
                ["campaignId"] = created.GetProperty("campaignId").GetString(),
                ["geo"] = new Dictionary<string, object?> { ["query"] = "Houston metro" },
                ["categories"] = new[] { "warehouse" },
            },
            server.Diagnostics);

        error.Code.ShouldBe(
            "NOT_READY",
            "mcp-tools.md §Errors: 'Setup or prerequisite missing'. Zero candidates would read as a "
            + "real answer about the Houston market. Raw: " + error.RawJson);
        (error.Hint ?? string.Empty).ShouldContain(
            "prepare_data",
            Case.Insensitive,
            $"the hint has to name the way out ('Run prepare_data for TX first'). Got: {error.Hint}");
    }

    [Fact]
    public async Task Lookup_overture_categories_without_an_extract_is_NOT_READY()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "lookup_overture_categories",
            new Dictionary<string, object?> { ["query"] = "warehouse", ["state"] = "TX" },
            server.Diagnostics);

        error.Code.ShouldBe("NOT_READY", "the counts come from the extract. Raw: " + error.RawJson);
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Get_status_reports_the_overture_release_and_states_it_actually_has()
    {
        var ready = (await ToolCall.OkAsync(server.Client, "get_status", diagnostics: server.Diagnostics))
            .GetProperty("ready")
            .GetProperty("overture");

        ready.GetProperty("states").EnumerateArray().ShouldBeEmpty(
            "this server has no extract, and get_status is where a skill looks before searching.");
    }
}
