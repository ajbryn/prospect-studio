using System.Text.Json;
using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Json;
using ProspectStudio.Core.Market;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Market;

/// <summary>
/// The second form of <c>estimate_market</c>'s <c>geo</c> argument: a <c>GeoScope</c> that
/// <c>resolve_geography</c> already returned, handed straight back. mcp-tools.md §estimate_market says
/// "or <c>geo</c>: &lt;GeoScope&gt;", and this is the path that matters most — a multi-metro union has
/// every county it needs and a <c>cbsa</c> of <strong>null</strong>, so code reaching for <c>cbsa</c>
/// instead of <c>countyFips</c> sizes the wrong market, or none at all.
/// </summary>
/// <remarks>
/// The scopes here are built by serialising a real <see cref="ResolvedGeography"/> with the server's own
/// options and reading it back, so a property that <c>resolve_geography</c> emits but <c>geo</c> cannot
/// receive fails here rather than in a user's session.
/// </remarks>
public class MarketGeoInputTests
{
    [Fact]
    public void A_union_scope_with_no_cbsa_is_used_as_given()
    {
        var union = new ResolvedGeography(
            GeoScopeTypes.Cbsa,
            "2 metro areas",
            Cbsa: null,
            States: ["48"],
            CountyFips: CbpFixtures.HoustonCountyFips,
            Zips: [],
            Radius: null,
            Bbox: []);

        var scope = RoundTrip(union).AsScope()
            .ShouldNotBeNull("a scope that carries countyFips is usable as it stands, so nothing is re-resolved.");

        scope.CountyFips.ShouldBe(
            CbpFixtures.HoustonCountyFips,
            ignoreOrder: true,
            "all ten counties of the union survive the round trip; cbsa being null costs nothing.");
        scope.Cbsa.ShouldBeNull();
        scope.Label.ShouldBe("2 metro areas");
    }

    [Fact]
    public void A_scope_that_does_carry_a_cbsa_is_still_sized_from_its_counties()
    {
        var houston = new ResolvedGeography(
            GeoScopeTypes.Cbsa,
            "Houston-Pasadena-The Woodlands, TX",
            Cbsa: "26420",
            States: ["48"],
            CountyFips: CbpFixtures.HoustonCountyFips,
            Zips: [],
            Radius: null,
            Bbox: []);

        var scope = RoundTrip(houston).AsScope().ShouldNotBeNull();

        scope.CountyFips.ShouldBe(
            CbpFixtures.HoustonCountyFips,
            ignoreOrder: true,
            "countyFips wins whether or not a cbsa is present: CBP is queried by county and there is no "
            + "CBSA-shaped query to fall back to.");
    }

    [Fact]
    public void A_place_name_has_no_scope_of_its_own_so_it_gets_resolved()
    {
        var geo = new MarketGeoInput(Query: "Houston metro");

        geo.AsScope().ShouldBeNull(
            "{\"query\": \"Houston metro\"} is the other input form: there are no counties yet, so "
            + "resolve_geography has to run.");
        geo.AsResolveRequest().Query.ShouldBe("Houston metro");
    }

    [Fact]
    public void A_type_and_values_scope_has_no_counties_yet_either()
    {
        var geo = new MarketGeoInput(Type: GeoScopeTypes.Zips, Values: ["77494"]);

        geo.AsScope().ShouldBeNull("a ZIP is not a county, so this form still needs resolving.");

        var request = geo.AsResolveRequest();
        request.Type.ShouldBe(GeoScopeTypes.Zips);
        request.Values.ShouldBe(["77494"]);
    }

    [Fact]
    public void An_empty_county_list_is_not_a_usable_scope()
    {
        // Treating [] as a usable scope would size a market of nothing and report zero, which
        // mcp-tools.md §estimate_market refuses.
        new MarketGeoInput(Label: "nowhere", CountyFips: []).AsScope().ShouldBeNull();
    }

    /// <summary>
    /// What a client does with <c>resolve_geography</c>'s answer: pass the JSON back untouched.
    /// </summary>
    private static MarketGeoInput RoundTrip(ResolvedGeography scope)
    {
        var json = JsonSerializer.Serialize(scope, ProspectStudioJson.Options);

        return JsonSerializer.Deserialize<MarketGeoInput>(json, ProspectStudioJson.Options)
            .ShouldNotBeNull($"geo has to accept a scope resolve_geography emitted: {json}");
    }
}
