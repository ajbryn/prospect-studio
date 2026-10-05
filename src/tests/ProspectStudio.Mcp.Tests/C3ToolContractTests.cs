using System.Text.Json;
using ModelContextProtocol.Client;
using ProspectStudio.Core.Configuration;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The tool chunk C3 adds, checked against mcp-tools.md §estimate_market: it is advertised with a
/// description, takes the documented <c>camelCase</c> parameters, and fails with a code from §Errors.
/// This server has reference data but no <c>CENSUS_API_KEY</c>, which is the state almost every user
/// starts in.
/// </summary>
[Collection(ReferenceDataServerCollection.Name)]
public class C3ToolContractTests(ReferenceDataServerFixture server)
{
    [Fact]
    public async Task Tools_list_advertises_estimate_market_with_a_description()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "estimate_market");

        tool.Description.ShouldNotBeNullOrWhiteSpace(
            "Claude picks tools from their descriptions, so every tool needs one (mcp-tools.md §Conventions).");
    }

    [Fact]
    public async Task Estimate_market_takes_naics_geo_and_minEmployees()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "estimate_market");

        ToolSchemas.Properties(tool).ShouldBe(
            ["naics", "geo", "minEmployees"],
            ignoreOrder: true,
            ToolSchemas.Describe(tool));
    }

    [Fact]
    public async Task Estimate_market_requires_the_naics_codes_and_the_geography()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "estimate_market");

        ToolSchemas.Required(tool).ShouldBe(
            ["naics", "geo"],
            ignoreOrder: true,
            "mcp-tools.md marks an optional parameter as optional where it is one (find_candidates's geo "
            + "says '<optional; defaults to profile geography>'); estimate_market's naics and geo carry no "
            + $"such note, and minEmployees is the only one its example could be read without. {ToolSchemas.Describe(tool)}");
    }

    [Fact]
    public async Task Estimate_market_accepts_a_scope_that_resolve_geography_returned()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "estimate_market");
        var geo = GeoSchema(tool);

        // mcp-tools.md §estimate_market takes `geo` as a place to look up *or* as a GeoScope handed back
        // unchanged. Without countyFips in the schema a client cannot pass a resolved scope back at all,
        // and a multi-metro union - whose cbsa is deliberately null - becomes unusable.
        geo.ShouldContain("countyFips", $"the scope form needs its counties: {ToolSchemas.Describe(tool)}");
        geo.ShouldContain("query", $"the place-name form has to stay: {ToolSchemas.Describe(tool)}");

        foreach (var member in new[] { "type", "values" })
        {
            geo.ShouldContain(member, $"'{member}' is part of a GeoScope: {ToolSchemas.Describe(tool)}");
        }
    }

    [Fact]
    public async Task Estimate_market_with_a_bogus_naics_code_is_VALIDATION_FAILED()
    {
        // NAICS is validated before Census is ever consulted, so this reaches the real mapping without a
        // key and without the network - unlike the request cap, which lives behind the key check.
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "estimate_market",
            new Dictionary<string, object?>
            {
                ["naics"] = new[] { "warehouse" },
                ["geo"] = new Dictionary<string, object?> { ["query"] = "Houston metro" },
            },
            server.Diagnostics);

        error.Code.ShouldBe(
            "VALIDATION_FAILED",
            "mcp-tools.md §estimate_market: VALIDATION_FAILED for a bogus NAICS code. Raw: " + error.RawJson);
        error.Message.ShouldNotBeNull($"the envelope must carry a message: {error.RawJson}");
        error.Message.ShouldContain(
            "warehouse",
            Case.Insensitive,
            $"say which value was rejected: {error.RawJson}");
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Estimate_market_with_an_empty_scope_is_VALIDATION_FAILED_not_a_market_of_zero()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "estimate_market",
            new Dictionary<string, object?>
            {
                ["naics"] = new[] { "4931" },
                ["geo"] = new Dictionary<string, object?> { ["type"] = "counties", ["values"] = Array.Empty<string>() },
            },
            server.Diagnostics);

        error.Code.ShouldBe(
            "VALIDATION_FAILED",
            "'reporting a market of zero for an empty scope is the dangerous reading, so refuse instead'. "
            + "Raw: " + error.RawJson);
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Estimate_market_without_a_census_key_is_NOT_READY_naming_the_variable()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "estimate_market",
            new Dictionary<string, object?>
            {
                ["naics"] = new[] { "4931" },
                ["geo"] = new Dictionary<string, object?> { ["query"] = "Houston metro" },
                ["minEmployees"] = 20,
            },
            server.Diagnostics);

        error.Code.ShouldBe(
            "NOT_READY",
            "CENSUS_API_KEY is required, not optional: since May 2026 every data query without one is a "
            + "302 (mcp-tools.md §Errors, technical-design §4).");

        $"{error.Message} {error.Hint}".ShouldContain(
            PsOptionsFactory.CensusKeyVariable, Case.Sensitive,
            "the user cannot act on 'not ready' unless it names the variable to set. Raw: " + error.RawJson);

        // The key is checked before anything is requested, which is also what keeps this test off the
        // network: there is no point sending a query that can only 302.
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The property names of the <c>geo</c> argument's own object schema, following a <c>$ref</c> into
    /// <c>$defs</c> when the SDK factors the type out.
    /// </summary>
    private static List<string> GeoSchema(McpClientTool tool)
    {
        var schema = tool.ProtocolTool.InputSchema;
        schema.TryGetProperty("properties", out var properties).ShouldBeTrue(ToolSchemas.Describe(tool));
        properties.TryGetProperty("geo", out var geo).ShouldBeTrue(
            $"estimate_market takes a 'geo' argument: {ToolSchemas.Describe(tool)}");

        if (geo.TryGetProperty("$ref", out var reference)
            && reference.GetString() is { } pointer
            && pointer.StartsWith("#/$defs/", StringComparison.Ordinal)
            && schema.TryGetProperty("$defs", out var definitions)
            && definitions.TryGetProperty(pointer["#/$defs/".Length..], out var resolved))
        {
            geo = resolved;
        }

        return geo.TryGetProperty("properties", out var members) && members.ValueKind == JsonValueKind.Object
            ? [.. members.EnumerateObject().Select(member => member.Name)]
            : [];
    }
}
