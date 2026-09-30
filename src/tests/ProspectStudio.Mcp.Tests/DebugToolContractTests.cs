#if DEBUG
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ProspectStudio.Mcp.Errors;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

// debug_sleep and debug_fail exist only in Debug builds; the configuration-independent error
// contract lives in ErrorContractTests.
[Collection(McpServerCollection.Name)]
public class DebugToolContractTests(McpServerFixture server)
{
    [Fact]
    public async Task Debug_fail_returns_the_requested_code_over_the_wire()
    {
        var result = await CallAsync("debug_fail", new Dictionary<string, object?>
        {
            ["code"] = ToolErrorCodes.NotReady,
            ["message"] = "Reference data is missing.",
        });

        result.IsError.ShouldBe(true);

        using var document = JsonDocument.Parse(Text(result));
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("NOT_READY");
        error.GetProperty("message").GetString().ShouldBe("Reference data is missing.");
    }

    [Fact]
    public async Task An_unknown_error_code_is_rejected_as_a_validation_failure()
    {
        var result = await CallAsync("debug_fail", new Dictionary<string, object?> { ["code"] = "NOPE" });

        result.IsError.ShouldBe(true);

        using var document = JsonDocument.Parse(Text(result));
        document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("VALIDATION_FAILED");
    }

    [Fact]
    public async Task Debug_sleep_reports_how_long_it_slept()
    {
        var result = await CallAsync("debug_sleep", new Dictionary<string, object?> { ["seconds"] = 0 });

        result.IsError.ShouldNotBe(true, server.StandardError);

        using var document = JsonDocument.Parse(Text(result));
        document.RootElement.GetProperty("sleptSeconds").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Debug_sleep_validates_its_input_through_the_same_error_contract()
    {
        var result = await CallAsync("debug_sleep", new Dictionary<string, object?> { ["seconds"] = -1 });

        result.IsError.ShouldBe(true);

        using var document = JsonDocument.Parse(Text(result));
        document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("VALIDATION_FAILED");
    }

    private async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        using var timeout = TestTimeout.Start();
        return await server.Client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
    }

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Select(block => block.Text).ShouldHaveSingleItem();
}
#endif
