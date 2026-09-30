using System.Text.Json;
using ModelContextProtocol.Protocol;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

[Collection(McpServerCollection.Name)]
public class GetStatusContractTests(McpServerFixture server)
{
    [Fact]
    public async Task Tools_list_advertises_get_status_with_a_description()
    {
        using var timeout = TestTimeout.Start();
        var tools = await server.Client.ListToolsAsync(cancellationToken: timeout.Token);

        var status = tools.Where(tool => tool.Name == "get_status").ShouldHaveSingleItem();
        status.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Get_status_returns_the_contract_shape()
    {
        var payload = await CallGetStatusAsync();

        payload.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["version", "home", "data", "ready", "keys", "trackingBaseUrl", "warnings"],
            ignoreOrder: true);

        payload.GetProperty("version").GetString().ShouldNotBeNullOrWhiteSpace();
        payload.GetProperty("home").GetString().ShouldBe(Path.GetFullPath(server.Home));
        payload.GetProperty("data").GetString().ShouldBe(Path.GetFullPath(server.Data));
        payload.GetProperty("trackingBaseUrl").GetString().ShouldNotBeNullOrWhiteSpace();
        payload.GetProperty("warnings").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task Get_status_reports_readiness_placeholders()
    {
        var ready = (await CallGetStatusAsync()).GetProperty("ready");

        ready.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["referenceData", "overture", "brandKit", "dealers", "territories", "suppression"],
            ignoreOrder: true);

        ready.GetProperty("referenceData").GetBoolean().ShouldBeFalse();
        ready.GetProperty("brandKit").GetBoolean().ShouldBeFalse();
        ready.GetProperty("dealers").GetInt32().ShouldBe(0);
        ready.GetProperty("territories").GetInt32().ShouldBe(0);
        ready.GetProperty("suppression").GetInt32().ShouldBe(0);

        var overture = ready.GetProperty("overture");
        overture.GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
        overture.GetProperty("states").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_status_reports_keys_as_booleans_only()
    {
        var keys = (await CallGetStatusAsync()).GetProperty("keys");

        keys.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["census", "openai", "gemini", "googleMaps", "hubspot"],
            ignoreOrder: true);

        foreach (var key in keys.EnumerateObject())
        {
            key.Value.ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        }
    }

    private async Task<JsonElement> CallGetStatusAsync()
    {
        using var timeout = TestTimeout.Start();
        var result = await server.Client.CallToolAsync(
            "get_status",
            new Dictionary<string, object?>(),
            cancellationToken: timeout.Token);

        result.IsError.ShouldNotBe(true, server.StandardError);

        var text = result.Content.OfType<TextContentBlock>().Select(block => block.Text).ShouldHaveSingleItem();
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
