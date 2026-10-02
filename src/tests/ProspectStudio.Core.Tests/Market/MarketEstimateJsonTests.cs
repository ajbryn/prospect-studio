using System.Text.Json;
using ProspectStudio.Core.Json;
using ProspectStudio.Core.Market;
using Shouldly;

namespace ProspectStudio.Core.Tests.Market;

/// <summary>
/// The wire shape of <c>estimate_market</c>'s payload, against the example in mcp-tools.md. A skill
/// branches on these key names, and a successful tool call needs a key and the network, so this is the
/// only place the names are pinned without one.
/// </summary>
public class MarketEstimateJsonTests
{
    [Fact]
    public void The_payload_uses_the_documented_camelCase_keys()
    {
        var json = Serialize(Example());
        var root = JsonDocument.Parse(json).RootElement;

        root.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["cbpYear", "geoLabel", "byNaics", "total", "notes"],
            ignoreOrder: true,
            json);

        root.GetProperty("byNaics")[0].EnumerateObject().Select(property => property.Name).ShouldBe(
            ["naics", "title", "establishments", "withMinEmployees"],
            ignoreOrder: true,
            json);

        root.GetProperty("total").EnumerateObject().Select(property => property.Name).ShouldBe(
            ["establishments", "withMinEmployees"],
            ignoreOrder: true,
            json);
    }

    [Fact]
    public void The_notes_array_is_present_even_when_nothing_is_suppressed()
    {
        var json = Serialize(Example() with { Notes = [] });

        JsonDocument.Parse(json).RootElement.GetProperty("notes").EnumerateArray().ShouldBeEmpty(
            "notes is always a key, so a skill never has to tell 'absent' from 'empty' (mcp-tools.md "
            + $"§estimate_market): {json}");
    }

    private static string Serialize(MarketEstimate estimate) =>
        JsonSerializer.Serialize(estimate, ProspectStudioJson.Options);

    /// <summary>The example payload from mcp-tools.md §estimate_market, numbers and all.</summary>
    private static MarketEstimate Example() => new(
        2023,
        "Houston-Pasadena-The Woodlands, TX",
        [
            new("4931", "Warehousing and Storage", 462, 152),
            new("238210", "Electrical Contractors and Other Wiring Installation Contractors", 1_310, 217),
        ],
        new MarketTotals(1_772, 369),
        [
            "4931: 23 of 462 establishments have no size band published, and Liberty and San Jacinto "
            + "counties are absent entirely, so withMinEmployees is a lower bound.",
        ]);
}
