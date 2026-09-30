using System.Text.Json;
using System.Text.Json.Nodes;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// POC-6 and mcp-tools.md §save_search_profile: the profile is validated against
/// <c>poc/schemas/search-profile.schema.json</c>, the extra <c>scoringWeights</c> sum rule is applied
/// in code, and <c>search-profile.json</c> is written into the campaign folder. Every rejection is
/// <c>VALIDATION_FAILED</c> with <c>details[]</c> that carry a JSON pointer, so Claude sees one shape
/// whichever check failed.
/// <para>
/// The invalid variants are mutations of <c>poc/fixtures/sample-search-profile.json</c>, so the
/// fixture stays the single source of truth instead of being copied four times.
/// </para>
/// </summary>
[Collection(McpServerCollection.Name)]
public class SaveSearchProfileContractTests(McpServerFixture server)
{
    private const string ProfileFileName = "search-profile.json";

    // ── the fixture itself ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_sample_profile_fixture_has_scoring_weights_that_sum_to_one()
    {
        var weights = ValidProfile()["scoringWeights"]!.AsObject();

        weights.Count.ShouldBe(6, "the schema requires all six weight keys.");
        weights.Select(pair => pair.Value!.GetValue<double>()).Sum()
            .ShouldBe(1.0, tolerance: 0.0000001, "the fixture must stay valid input for the accepted case.");
    }

    [Fact]
    public async Task Save_search_profile_requires_a_campaign_id_and_a_profile()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "save_search_profile");

        ToolSchemas.Properties(tool).ShouldBe(["campaignId", "profile"], ignoreOrder: true, ToolSchemas.Describe(tool));
        ToolSchemas.Required(tool).ShouldBe(["campaignId", "profile"], ignoreOrder: true, ToolSchemas.Describe(tool));
    }

    // ── the accepted case ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_search_profile_accepts_the_sample_profile_and_returns_the_documented_shape()
    {
        var campaign = await NewCampaignAsync("Profile Accepted");

        var payload = await SaveAsync(campaign.Id, ValidProfile());

        payload.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["saved", "path", "warnings"],
            ignoreOrder: true,
            "mcp-tools.md §save_search_profile returns { saved, path, warnings }.");

        payload.GetProperty("saved").GetBoolean().ShouldBeTrue();
        payload.GetProperty("warnings").ValueKind.ShouldBe(JsonValueKind.Array);
        payload.GetProperty("path").GetString()
            .ShouldBe(Path.Combine(campaign.Folder, ProfileFileName), "POC-6 writes search-profile.json into the campaign folder.");
    }

    [Fact]
    public async Task Save_search_profile_writes_the_profile_verbatim_into_the_campaign_folder()
    {
        var campaign = await NewCampaignAsync("Profile Written");
        var profile = ValidProfile();

        await SaveAsync(campaign.Id, profile);

        var file = Path.Combine(campaign.Folder, ProfileFileName);
        File.Exists(file).ShouldBeTrue($"POC-6: '{file}' must exist after save_search_profile.");

        var written = JsonNode.Parse(await File.ReadAllTextAsync(file));
        JsonNode.DeepEquals(written, profile).ShouldBeTrue(
            $"the saved file must hold the profile that was submitted. It holds:{Environment.NewLine}{written}");
    }

    [Fact]
    public async Task Save_search_profile_warns_about_a_segment_with_keywords_but_no_overture_categories()
    {
        var campaign = await NewCampaignAsync("Profile Warning");
        var profile = ValidProfile();
        var segment = profile["segments"]!.AsArray()[3]!.AsObject();
        var segmentName = segment["name"]!.GetValue<string>();
        segment.Remove("overtureCategories");

        var payload = await SaveAsync(campaign.Id, profile);

        payload.GetProperty("saved").GetBoolean().ShouldBeTrue("keywords alone satisfy the schema's anyOf.");

        var warnings = payload.GetProperty("warnings").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToList();
        warnings.ShouldContain(
            warning => warning.Contains(segmentName, StringComparison.Ordinal),
            $"mcp-tools.md's example warning names the segment: \"Segment '{segmentName}' has no overtureCategories; keywords only\". Got: {payload}");
    }

    // ── the three rejections ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_search_profile_rejects_a_profile_with_no_segments()
    {
        var campaign = await NewCampaignAsync("Profile No Segments");
        var profile = ValidProfile();
        profile.Remove("segments");

        var error = await SaveExpectingFailureAsync(campaign.Id, profile);

        error.Details.Select(detail => detail.Pointer).ShouldContain(
            "/segments",
            "a missing required property reports the pointer of the property itself, not of its parent, so the "
            + "server synthesizes it by appending the member name to the instance location "
            + $"(mcp-tools.md §save_search_profile). Got: {Format(error)}");

        // Nothing but 'segments' was touched, so nothing else may be blamed. JSON Schema evaluation
        // also reports the failed branches of an anyOf that another branch satisfied - here the
        // geography, which is untouched and valid - and those must never reach details[]: Claude acts
        // on every pointer it is given.
        error.Details.Select(detail => detail.Pointer).ShouldAllBe(
            pointer => pointer == "/segments" || pointer.StartsWith("/segments/", StringComparison.Ordinal),
            $"every detail must point at the one thing that is wrong. Got: {Format(error)}");

        File.Exists(Path.Combine(campaign.Folder, ProfileFileName)).ShouldBeFalse("a rejected profile must not be written.");
    }

    [Fact]
    public async Task Save_search_profile_rejects_an_invalid_naics_code_with_a_pointer_to_it()
    {
        var campaign = await NewCampaignAsync("Profile Bad Naics");
        var profile = ValidProfile();
        profile["segments"]!.AsArray()[0]!.AsObject()["naics"]!.AsArray()[0] = "23A";

        var error = await SaveExpectingFailureAsync(campaign.Id, profile);

        error.Details.Select(detail => detail.Pointer).ShouldContain(
            "/segments/0/naics/0",
            $"the JSON pointer must locate the offending code, not just the document. Got: {Format(error)}");
    }

    /// <summary>
    /// A segment must carry <c>overtureCategories</c> or <c>keywords</c> (the schema's <c>anyOf</c>).
    /// When it carries neither, one detail says so at the segment: a detail per branch would read as
    /// "both are required", which is not what the schema says and would have Claude add both.
    /// </summary>
    [Fact]
    public async Task Save_search_profile_reports_an_either_or_requirement_once_at_the_value_it_belongs_to()
    {
        var campaign = await NewCampaignAsync("Profile Either Or");
        var profile = ValidProfile();
        var segment = profile["segments"]!.AsArray()[1]!.AsObject();
        segment.Remove("overtureCategories");
        segment.Remove("keywords");

        var error = await SaveExpectingFailureAsync(campaign.Id, profile);

        var pointers = error.Details.Select(detail => detail.Pointer).ToList();
        pointers.ShouldBe(["/segments/1"], $"one detail, at the segment the schema failed on. Got: {Format(error)}");
        error.Details[0].Message.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The same <c>anyOf</c>, but one branch fails for a reason of its own: <c>overtureCategories</c> is
    /// present and empty. That is a likely typo, and the detail must name the field and say what is
    /// wrong with it rather than collapsing into "this value matches none of the allowed shapes", which
    /// nobody can act on.
    /// </summary>
    [Fact]
    public async Task Save_search_profile_names_the_field_when_an_either_or_branch_fails_for_its_own_reason()
    {
        var campaign = await NewCampaignAsync("Profile Empty Categories");
        var profile = ValidProfile();
        var segment = profile["segments"]!.AsArray()[0]!.AsObject();
        segment["overtureCategories"] = new JsonArray();
        segment.Remove("keywords");

        var error = await SaveExpectingFailureAsync(campaign.Id, profile);

        var detail = error.Details.FirstOrDefault(candidate => candidate.Pointer == "/segments/0/overtureCategories");
        detail.ShouldNotBeNull(
            "an empty 'overtureCategories' must be reported at the array itself, not summarized at the "
            + $"segment: a caller can act on 'this array needs at least one item'. Got: {Format(error)}");
        detail.Message.ShouldNotBeNullOrWhiteSpace();

        error.Details.Select(candidate => candidate.Pointer).ShouldAllBe(
            pointer => pointer.StartsWith("/segments/0", StringComparison.Ordinal),
            $"only the segment that was changed may be blamed. Got: {Format(error)}");
    }

    [Fact]
    public async Task Save_search_profile_rejects_scoring_weights_that_do_not_sum_to_one()
    {
        var campaign = await NewCampaignAsync("Profile Weights");
        var profile = ValidProfile();

        // 0.25 + 0.15 + 0.20 + 0.15 + 0.05 + 0.10 = 0.90
        profile["scoringWeights"]!.AsObject()["signals"] = 0.15;

        var error = await SaveExpectingFailureAsync(campaign.Id, profile);

        error.Details.Select(detail => detail.Pointer).ShouldContain(
            "/scoringWeights",
            "the sum-to-1.0 rule is checked in code, not by the schema, but its detail carries a pointer too so "
            + $"Claude sees one consistent shape (implementation-plan C1). Got: {Format(error)}");
    }

    [Fact]
    public async Task Save_search_profile_keeps_the_last_valid_profile_when_a_later_save_is_rejected()
    {
        var campaign = await NewCampaignAsync("Profile Not Clobbered");
        var valid = ValidProfile();
        await SaveAsync(campaign.Id, valid);

        var broken = ValidProfile();
        broken.Remove("segments");
        await SaveExpectingFailureAsync(campaign.Id, broken);

        var written = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(campaign.Folder, ProfileFileName)));
        JsonNode.DeepEquals(written, valid).ShouldBeTrue("a rejected save must leave the stored profile untouched.");
    }

    [Fact]
    public async Task Save_search_profile_rejects_an_unknown_campaign_with_NOT_FOUND()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "save_search_profile",
            Arguments("cmp_ZZZZZZ", ValidProfile()),
            server.StandardError);

        error.Code.ShouldBe("NOT_FOUND", $"mcp-tools.md §Errors maps an unknown campaign to NOT_FOUND. Got: {error.RawJson}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A fresh mutable copy of the fixture, so one test's mutation cannot reach another.</summary>
    private static JsonObject ValidProfile() =>
        JsonNode.Parse(File.ReadAllText(RepoFixtures.SampleSearchProfile))!.AsObject();

    private async Task<(string Id, string Folder)> NewCampaignAsync(string label)
    {
        var payload = await ToolCall.OkAsync(
            server.Client,
            "create_campaign",
            new Dictionary<string, object?> { ["name"] = $"{label} {Guid.NewGuid().ToString("N")[..6]}" },
            server.StandardError);

        return (payload.GetProperty("campaignId").GetString()!, payload.GetProperty("folder").GetString()!);
    }

    private async Task<JsonElement> SaveAsync(string campaignId, JsonObject profile) =>
        await ToolCall.OkAsync(server.Client, "save_search_profile", Arguments(campaignId, profile), server.StandardError);

    private async Task<ToolErrorPayload> SaveExpectingFailureAsync(string campaignId, JsonObject profile)
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "save_search_profile",
            Arguments(campaignId, profile),
            server.StandardError);

        error.Code.ShouldBe("VALIDATION_FAILED", $"mcp-tools.md §Errors: a schema or rule violation is VALIDATION_FAILED. Got: {error.RawJson}");
        error.Details.ShouldNotBeEmpty($"VALIDATION_FAILED must carry details[] (JSON pointer + message). Got: {error.RawJson}");
        return error;
    }

    private static Dictionary<string, object?> Arguments(string campaignId, JsonObject profile) => new()
    {
        ["campaignId"] = campaignId,

        // Sent as a JsonElement so the profile reaches the server as the document the schema validates,
        // not as a re-serialized CLR object.
        ["profile"] = JsonDocument.Parse(profile.ToJsonString()).RootElement.Clone(),
    };

    private static string Format(ToolErrorPayload error) =>
        $"{error.RawJson}{Environment.NewLine}details: {string.Join(" | ", error.Details)}";
}
