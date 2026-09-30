using System.Text.Json;
using ModelContextProtocol.Protocol;
using ProspectStudio.Mcp.Errors;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

public class ErrorContractTests
{
    [Fact]
    public void ToolResults_error_is_exactly_the_documented_envelope()
    {
        var result = ToolResults.Error(new McpToolException("NOT_READY", "Reference data is missing.", "Run prepare_data for TX first."));

        result.IsError.ShouldBe(true);
        var text = Text(result);
        text.ShouldBe("""{"error":{"code":"NOT_READY","message":"Reference data is missing.","hint":"Run prepare_data for TX first."}}""");
    }

    [Fact]
    public void ToolResults_error_keeps_a_null_hint_as_a_field()
    {
        var result = ToolResults.Error(new McpToolException("CONFLICT", "That name is taken."));

        Text(result).ShouldBe("""{"error":{"code":"CONFLICT","message":"That name is taken.","hint":null}}""");
    }

    [Fact]
    public async Task A_thrown_tool_exception_reaches_the_client_as_the_structured_error()
    {
        await using var server = await StartAsync();

        var result = await CallAsync(server, "throws_tool_exception");

        result.IsError.ShouldBe(true);

        var text = Text(result);
        text.Contains("An error occurred", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
            "the SDK rewrites an escaping exception into its own 'An error occurred...' message");

        using var document = JsonDocument.Parse(text);
        document.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["error"]);

        var error = document.RootElement.GetProperty("error");
        error.EnumerateObject().Select(property => property.Name).ShouldBe(["code", "message", "hint"], ignoreOrder: true);
        error.GetProperty("code").GetString().ShouldBe("NOT_READY");
        error.GetProperty("message").GetString().ShouldBe("Reference data is missing.");
        error.GetProperty("hint").GetString().ShouldBe("Run prepare_data for TX first.");
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_INTERNAL_without_leaking_detail()
    {
        await using var server = await StartAsync();

        var result = await CallAsync(server, "throws_unexpected");

        result.IsError.ShouldBe(true);

        var text = Text(result);
        text.ShouldNotContain(ThrowingTools.UnexpectedMessage);
        text.ShouldNotContain("InvalidOperationException");
        text.ShouldNotContain("   at ");

        using var document = JsonDocument.Parse(text);
        var error = document.RootElement.GetProperty("error");
        error.EnumerateObject().Select(property => property.Name).ShouldBe(["code", "message", "hint"], ignoreOrder: true);
        error.GetProperty("code").GetString().ShouldBe("INTERNAL");
        error.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        error.GetProperty("hint").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_successful_tool_is_not_marked_as_an_error()
    {
        await using var server = await StartAsync();

        var result = await CallAsync(server, "returns_payload");

        result.IsError.ShouldNotBe(true);
        Text(result).ShouldBe("""{"ok":true}""");
    }

    private static async Task<InProcessMcpServer> StartAsync()
    {
        using var timeout = TestTimeout.Start();
        return await InProcessMcpServer.StartAsync<ThrowingTools>(timeout.Token);
    }

    private static async Task<CallToolResult> CallAsync(InProcessMcpServer server, string tool)
    {
        using var timeout = TestTimeout.Start();
        return await server.Client.CallToolAsync(tool, new Dictionary<string, object?>(), cancellationToken: timeout.Token);
    }

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Select(block => block.Text).ShouldHaveSingleItem();
}
