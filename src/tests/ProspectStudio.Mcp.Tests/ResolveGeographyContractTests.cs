using System.Text.Json;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// POC-8 and mcp-tools.md §resolve_geography, against a server whose <c>refdata</c> holds the committed
/// Census excerpts. The authoritative <c>GeoScope</c> shape is <c>$defs.geoScope</c> in
/// <c>schemas/search-profile.schema.json</c>: every key is always present, using <c>[]</c> or
/// <c>null</c> where it does not apply, and <c>alternatives</c> is a flat sibling omitted when empty.
/// Every expected value here was verified against the real Census files in C2 - in particular the
/// Houston CBSA has <strong>ten</strong> counties, and ZIP 77494 spans three.
/// </summary>
[Collection(ReferenceDataServerCollection.Name)]
public class ResolveGeographyContractTests(ReferenceDataServerFixture server)
{
    /// <summary>CBSA 26420, "Houston-Pasadena-The Woodlands, TX". San Jacinto (48407) is easy to miss.</summary>
    private static readonly string[] HoustonCbsaCounties =
        ["48015", "48039", "48071", "48157", "48167", "48201", "48291", "48339", "48407", "48473"];

    /// <summary>`$defs.geoScope`, which mcp-tools.md now names as the authoritative shape.</summary>
    private static readonly string[] GeoScopeKeys =
        ["type", "label", "cbsa", "states", "countyFips", "zips", "radius", "bbox"];

    [Fact]
    public async Task Houston_metro_resolves_to_CBSA_26420_with_exactly_ten_counties()
    {
        var scope = await ResolveAsync(new Dictionary<string, object?> { ["query"] = "Houston metro" });

        scope.GetProperty("type").GetString().ShouldBe("cbsa");
        scope.GetProperty("cbsa").GetString().ShouldBe("26420");
        scope.GetProperty("label").GetString().ShouldNotBeNull().ShouldContain("Houston", Case.Sensitive);
        States(scope).ShouldBe(["TX"]);

        CountyFips(scope).ShouldBe(
            HoustonCbsaCounties,
            ignoreOrder: true,
            "the Houston CBSA has ten counties, including San Jacinto 48407 - verified against "
            + "list1_2023.xlsx in C2. Jefferson 48245 (Beaumont) is a different CBSA and must not appear.");

        scope.GetProperty("zips").EnumerateArray().ShouldBeEmpty("a CBSA scope has no ZIP list.");
        scope.GetProperty("radius").ValueKind.ShouldBe(JsonValueKind.Null, "a CBSA scope has no circle.");

        var bbox = scope.GetProperty("bbox").EnumerateArray().Select(value => value.GetDouble()).ToList();
        bbox.Count.ShouldBe(4, "bbox is [minLon, minLat, maxLon, maxLat] (technical-design §6.2).");
        bbox[0].ShouldBeLessThan(bbox[2]);
        bbox[1].ShouldBeLessThan(bbox[3]);
    }

    [Fact]
    public async Task Harris_County_TX_resolves_to_FIPS_48201()
    {
        var scope = await ResolveAsync(new Dictionary<string, object?> { ["query"] = "Harris County, TX" });

        scope.GetProperty("type").GetString().ShouldBe(
            "counties",
            "the type enum in $defs.geoScope is 'counties' - there is no singular 'county'.");
        CountyFips(scope).ShouldBe(["48201"]);
        States(scope).ShouldBe(["TX"]);
        scope.GetProperty("cbsa").ValueKind.ShouldBe(JsonValueKind.Null, "a county scope is not a CBSA.");
    }

    [Fact]
    public async Task A_list_of_counties_resolves_to_their_FIPS_codes()
    {
        var scope = await ResolveAsync(new Dictionary<string, object?>
        {
            ["type"] = "counties",
            ["values"] = new[] { "Harris County, TX", "Fort Bend County, TX" },
        });

        scope.GetProperty("type").GetString().ShouldBe("counties");
        CountyFips(scope).ShouldBe(["48201", "48157"], ignoreOrder: true);
    }

    [Fact]
    public async Task ZIP_77494_resolves_to_the_three_counties_it_spans()
    {
        var scope = await ResolveAsync(new Dictionary<string, object?>
        {
            ["type"] = "zips",
            ["values"] = new[] { "77494" },
        });

        scope.GetProperty("type").GetString().ShouldBe("zips");
        scope.GetProperty("zips").EnumerateArray().Select(value => value.GetString()).ShouldBe(
            ["77494"],
            "a ZIP scope keeps the ZIPs it was given, because the candidate filter uses them "
            + "(technical-design §6.2).");

        CountyFips(scope).ShouldBe(
            ["48157", "48201", "48473"],
            ignoreOrder: true,
            "ZCTA 77494 (Katy) genuinely spans Fort Bend, Harris and Waller - verified in C2.");
    }

    [Fact]
    public async Task A_ten_mile_radius_around_downtown_Houston_includes_Harris_County()
    {
        var scope = await ResolveAsync(new Dictionary<string, object?>
        {
            ["type"] = "radius",
            ["center"] = new Dictionary<string, object?> { ["lat"] = 29.7604, ["lon"] = -95.3698 },
            ["radiusMiles"] = 10,
        });

        scope.GetProperty("type").GetString().ShouldBe("radius");
        CountyFips(scope).ShouldContain("48201", "downtown Houston is in Harris County.");

        var radius = scope.GetProperty("radius");
        radius.ValueKind.ShouldBe(JsonValueKind.Object, "a radius scope carries the circle it resolved.");
        radius.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["lat", "lon", "miles"],
            ignoreOrder: true,
            $"$defs.geoScope defines radius as {{lat, lon, miles}}; got {radius}");

        radius.GetProperty("lat").GetDouble().ShouldBe(29.7604, 0.0001);
        radius.GetProperty("lon").GetDouble().ShouldBe(-95.3698, 0.0001);
        radius.GetProperty("miles").GetDouble().ShouldBe(10);
    }

    [Fact]
    public async Task Texas_resolves_to_a_state_scope_of_Texas_counties()
    {
        var scope = await ResolveAsync(new Dictionary<string, object?> { ["query"] = "Texas" });

        scope.GetProperty("type").GetString().ShouldBe("state");
        States(scope).ShouldBe(["TX"]);

        var counties = CountyFips(scope);
        counties.ShouldNotBeEmpty();
        counties.ShouldAllBe(fips => fips.StartsWith("48", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_ambiguous_query_comes_back_with_alternatives_as_a_sibling_key()
    {
        var payload = await CallAsync(new Dictionary<string, object?> { ["query"] = "Springfield" });

        payload.EnumerateObject().Select(property => property.Name).ShouldBe(
            [.. GeoScopeKeys, "alternatives"],
            ignoreOrder: true,
            $"the GeoScope is flat, with alternatives[] beside it; got {payload}");

        var candidates = payload.GetProperty("alternatives").EnumerateArray().ToList();
        candidates.Count.ShouldBeGreaterThanOrEqualTo(
            1,
            "the reference data holds four Springfield metros (IL, MA, MO, OH), so one of them being "
            + "picked silently would be a guess the user cannot see.");

        candidates.ShouldAllBe(candidate => candidate.GetProperty("label").GetString()!.Length > 0);
        candidates.Select(candidate => candidate.GetProperty("label").GetString() ?? string.Empty)
            .ShouldContain(label => label.Contains("Springfield", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_unambiguous_query_omits_alternatives_entirely()
    {
        var payload = await CallAsync(new Dictionary<string, object?> { ["query"] = "Houston metro" });

        payload.EnumerateObject().Select(property => property.Name).ShouldBe(
            GeoScopeKeys,
            ignoreOrder: true,
            "alternatives is omitted when empty, like details[] on an error, so an unambiguous answer "
            + $"is exactly the GeoScope; got {payload}");
    }

    [Fact]
    public async Task Several_metros_resolve_to_one_scope_whose_counties_are_the_union()
    {
        var scope = await ResolveAsync(new Dictionary<string, object?>
        {
            ["type"] = "cbsa",
            ["values"] = new[] { "Houston", "Beaumont" },
        });

        scope.GetProperty("type").GetString().ShouldBe("cbsa");

        CountyFips(scope).ShouldBe(
            [.. HoustonCbsaCounties, "48199", "48245", "48361"],
            ignoreOrder: true,
            "Houston's ten counties plus Beaumont-Port Arthur's three: Hardin 48199, Jefferson 48245 "
            + "and Orange 48361.");

        scope.GetProperty("cbsa").ValueKind.ShouldBe(
            JsonValueKind.Null,
            "no single CBSA code describes a union of metros, so reporting one of them would be a lie "
            + "about what was scoped.");

        scope.GetProperty("label").GetString().ShouldBe("2 metro areas");
        States(scope).ShouldBe(["TX"]);

        // Beaumont reaches further east than anything in the Houston metro, so the union's bbox has to
        // grow - a bbox still stopping at Houston's edge would silently clip the candidate pre-filter.
        var bbox = scope.GetProperty("bbox").EnumerateArray().Select(value => value.GetDouble()).ToList();
        bbox.Count.ShouldBe(4);
        bbox[2].ShouldBe(-93.84, 0.01, "Jefferson County's eastern edge, not Harris County's.");
    }

    [Fact]
    public async Task One_metro_still_reports_its_own_cbsa_code()
    {
        var payload = await CallAsync(new Dictionary<string, object?>
        {
            ["type"] = "cbsa",
            ["values"] = new[] { "Houston" },
        });

        payload.GetProperty("cbsa").GetString().ShouldBe(
            "26420",
            "the union branch must not swallow the single-value path, which still names one CBSA.");
        payload.GetProperty("label").GetString().ShouldNotBeNull().ShouldContain("Houston", Case.Sensitive);

        CountyFips(payload).ShouldBe(HoustonCbsaCounties, ignoreOrder: true);

        payload.EnumerateObject().Select(property => property.Name).ShouldBe(
            GeoScopeKeys,
            ignoreOrder: true,
            $"one unambiguous metro gets no alternatives; got {payload}");
    }

    [Fact]
    public async Task A_place_name_that_matches_nothing_is_NOT_FOUND()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "resolve_geography",
            new Dictionary<string, object?> { ["query"] = "Zzyzx Industrial Basin" },
            server.Diagnostics);

        error.Code.ShouldBe("NOT_FOUND");
        error.Hint.ShouldNotBeNullOrWhiteSpace(
            "the hint suggests a more specific form, such as 'Harris County, TX'.");
    }

    [Fact]
    public async Task No_recognizable_input_at_all_is_VALIDATION_FAILED()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "resolve_geography",
            new Dictionary<string, object?>(),
            server.Diagnostics);

        error.Code.ShouldBe(
            "VALIDATION_FAILED",
            "every parameter is optional on its own, but one of the five input forms has to be there.");
    }

    private async Task<JsonElement> CallAsync(Dictionary<string, object?> arguments) =>
        await ToolCall.OkAsync(server.Client, "resolve_geography", arguments, server.Diagnostics);

    /// <summary>
    /// Calls the tool and checks the flat <c>GeoScope</c> carries every documented key before a test
    /// reaches into it, so a missing key reads as a missing key rather than an exception.
    /// </summary>
    private async Task<JsonElement> ResolveAsync(Dictionary<string, object?> arguments)
    {
        var payload = await CallAsync(arguments);

        foreach (var key in GeoScopeKeys)
        {
            payload.TryGetProperty(key, out _).ShouldBeTrue(
                $"every GeoScope key is always present, using [] or null where it does not apply, so a "
                + $"caller never distinguishes absent from empty - '{key}' is missing from {payload}");
        }

        return payload;
    }

    private static List<string> CountyFips(JsonElement scope) =>
        [.. scope.GetProperty("countyFips").EnumerateArray().Select(value => value.GetString() ?? string.Empty)];

    private static List<string> States(JsonElement scope) =>
        [.. scope.GetProperty("states").EnumerateArray().Select(value => value.GetString() ?? string.Empty)];
}
