using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The five tools chunk C6 adds, against mcp-tools.md §Leads: that each one is advertised and takes the
/// parameters the contract documents, under the <c>camelCase</c> names it gives them.
/// </summary>
/// <remarks>
/// A skill reaches a parameter only if it is advertised. A tool that silently drops
/// <c>researchStatus</c> would look healthy in every behaviour test that happened not to pass it.
/// </remarks>
[Collection(LeadServerCollection.Name)]
public class C6ToolContractTests(LeadServerFixture server)
{
    [Theory]
    [InlineData("list_leads")]
    [InlineData("get_lead")]
    [InlineData("update_leads")]
    [InlineData("score_leads")]
    [InlineData("save_research")]
    public async Task The_tool_is_advertised_with_a_description(string tool)
    {
        var listed = await ToolSchemas.FindAsync(server.Client, tool);

        listed.Description.ShouldNotBeNullOrWhiteSpace(
            "Claude picks tools from their descriptions, so every tool needs one.");
    }

    [Fact]
    public async Task List_leads_takes_the_documented_filters()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "list_leads");

        ToolSchemas.Properties(tool).ShouldBe(
            ["campaignId", "status", "tier", "dealerId", "minScore", "researchStatus", "sort", "limit", "offset"],
            ignoreOrder: true,
            "mcp-tools.md §list_leads: { campaignId, status[], tier[], dealerId, minScore, researchStatus, "
            + $"sort, limit, offset }}. {ToolSchemas.Describe(tool)}");

        ToolSchemas.Required(tool).ShouldBe(
            ["campaignId"],
            ignoreOrder: true,
            $"every filter is optional; the contract shows defaults for all of them. {ToolSchemas.Describe(tool)}");
    }

    [Theory]
    [InlineData("status")]
    [InlineData("tier")]
    public async Task List_leads_takes_status_and_tier_as_arrays(string parameter)
    {
        // The contract passes ["review","approved"] and ["A","B"]. A string parameter would make
        // "approved or review" impossible in one call, and a skill would page the whole list to do it.
        var tool = await ToolSchemas.FindAsync(server.Client, "list_leads");
        var schema = ToolSchemas.Describe(tool);

        using var parsed = System.Text.Json.JsonDocument.Parse(schema);
        var property = parsed.RootElement.GetProperty("properties").GetProperty(parameter);

        Types(property).ShouldContain(
            "array",
            $"mcp-tools.md §list_leads passes '{parameter}' as an array. {schema}");
    }

    [Fact]
    public async Task Get_lead_takes_a_lead_and_an_optional_web_excerpt_flag()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "get_lead");

        ToolSchemas.Properties(tool).ShouldBe(
            ["campaignId", "leadId", "includeWebExcerpt"],
            ignoreOrder: true,
            $"mcp-tools.md §get_lead. {ToolSchemas.Describe(tool)}");
        ToolSchemas.Required(tool).ShouldBe(
            ["campaignId", "leadId"],
            ignoreOrder: true,
            $"includeWebExcerpt defaults to false. {ToolSchemas.Describe(tool)}");
    }

    [Fact]
    public async Task Update_leads_takes_a_batch_of_updates()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "update_leads");

        ToolSchemas.Properties(tool).ShouldBe(
            ["campaignId", "updates"],
            ignoreOrder: true,
            "mcp-tools.md §update_leads: { campaignId, updates: [ { leadId, status, dealerId, notes } ] }. "
            + ToolSchemas.Describe(tool));
        ToolSchemas.Required(tool).ShouldBe(["campaignId", "updates"], ignoreOrder: true, ToolSchemas.Describe(tool));

        using var parsed = System.Text.Json.JsonDocument.Parse(ToolSchemas.Describe(tool));
        var updates = parsed.RootElement.GetProperty("properties").GetProperty("updates");

        Types(updates).ShouldContain(
            "array",
            "updates is a batch: approving forty leads in one call is the whole point of the plural name. "
            + ToolSchemas.Describe(tool));
    }

    [Fact]
    public async Task Score_leads_takes_an_optional_weights_override()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "score_leads");

        ToolSchemas.Properties(tool).ShouldBe(
            ["campaignId", "weights"],
            ignoreOrder: true,
            $"mcp-tools.md §score_leads: {{ campaignId, weights: null }}. {ToolSchemas.Describe(tool)}");
        ToolSchemas.Required(tool).ShouldBe(
            ["campaignId"],
            ignoreOrder: true,
            $"weights defaults to the profile's, or §7.6's. {ToolSchemas.Describe(tool)}");
    }

    [Fact]
    public async Task Save_research_takes_a_lead_and_a_research_document()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "save_research");

        ToolSchemas.Properties(tool).ShouldBe(
            ["campaignId", "leadId", "research"],
            ignoreOrder: true,
            $"mcp-tools.md §save_research. {ToolSchemas.Describe(tool)}");
        ToolSchemas.Required(tool).ShouldBe(
            ["campaignId", "leadId", "research"],
            ignoreOrder: true,
            $"all three are needed; there is no default research. {ToolSchemas.Describe(tool)}");
    }

    private static IReadOnlyList<string> Types(System.Text.Json.JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type))
        {
            return [];
        }

        return type.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => [type.GetString() ?? string.Empty],
            System.Text.Json.JsonValueKind.Array =>
                [.. type.EnumerateArray().Select(value => value.GetString() ?? string.Empty)],
            _ => [],
        };
    }
}
