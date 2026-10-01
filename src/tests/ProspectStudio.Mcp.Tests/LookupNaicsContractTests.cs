using System.Text.Json;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// POC-7 and mcp-tools.md §lookup_naics, against the real committed NAICS 2022 table. The two examples
/// are the ones implementation-plan C2 names: "electrical contractor" must reach 238210 and "warehouse"
/// must reach 4931 or 493110, both inside the top three.
/// </summary>
[Collection(McpServerCollection.Name)]
public class LookupNaicsContractTests(McpServerFixture server)
{
    [Fact]
    public async Task Electrical_contractor_returns_238210_in_the_top_three()
    {
        var results = await LookupAsync("electrical contractor");

        results.Take(3).Select(result => result.Code).ShouldContain(
            "238210",
            $"got [{string.Join(", ", results.Take(3))}]");
    }

    [Fact]
    public async Task Warehouse_returns_4931_or_493110_in_the_top_three()
    {
        var results = await LookupAsync("warehouse");

        results.Take(3).Select(result => result.Code).ShouldContain(
            code => code == "4931" || code == "493110",
            $"got [{string.Join(", ", results.Take(3))}]");
    }

    [Fact]
    public async Task Results_carry_the_code_title_and_level_the_contract_documents()
    {
        var payload = await CallAsync("electrical contractor", limit: 5);
        var first = payload.GetProperty("results").EnumerateArray().First();

        first.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["code", "title", "level"],
            ignoreOrder: true,
            $"mcp-tools.md §lookup_naics documents exactly these fields; got {first}");

        var code = first.GetProperty("code").GetString();
        code.ShouldNotBeNullOrWhiteSpace();
        first.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        first.GetProperty("level").GetInt32().ShouldBeInRange(2, 6);
    }

    [Fact]
    public async Task A_sector_whose_code_is_a_range_survives_as_a_string()
    {
        // 31-33, 44-45 and 48-49 are ranges in the Census NAICS file (verified in C2), so the code
        // column cannot be numeric anywhere along the way.
        var results = await LookupAsync("retail trade", limit: 25);

        results.Select(result => result.Code).ShouldContain(
            "44-45",
            $"'Retail Trade' is sector 44-45; got [{string.Join(", ", results.Take(5))}]");
    }

    [Fact]
    public async Task The_limit_caps_the_number_of_results()
    {
        (await LookupAsync("contractors", limit: 3)).Count.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task A_query_that_matches_nothing_returns_an_empty_list_rather_than_an_error()
    {
        var payload = await CallAsync("zymological blancmange", limit: 10);

        payload.GetProperty("results").EnumerateArray().ShouldBeEmpty();
    }

    private async Task<JsonElement> CallAsync(string query, int? limit = null)
    {
        var arguments = new Dictionary<string, object?> { ["query"] = query };
        if (limit is not null)
        {
            arguments["limit"] = limit;
        }

        return await ToolCall.OkAsync(server.Client, "lookup_naics", arguments, server.StandardError);
    }

    private async Task<List<NaicsResult>> LookupAsync(string query, int? limit = null)
    {
        var payload = await CallAsync(query, limit);

        return [.. payload.GetProperty("results").EnumerateArray().Select(result => new NaicsResult(
            result.GetProperty("code").GetString() ?? string.Empty,
            result.GetProperty("title").GetString() ?? string.Empty,
            result.GetProperty("level").GetInt32()))];
    }

    private sealed record NaicsResult(string Code, string Title, int Level)
    {
        public override string ToString() => $"{Code} {Title}";
    }
}
