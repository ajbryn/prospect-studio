#if DEBUG
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

[McpServerToolType]
public sealed class DebugTools
{
    private const int MaxSleepSeconds = 600;

    [McpServerTool(Name = "debug_sleep")]
    [Description("Debug builds only. Sleeps for the given number of seconds, so a client's tool-call timeout can be measured. Do not use it for anything else.")]
    public static async ValueTask<CallToolResult> DebugSleepAsync(
        [Description("Seconds to sleep, 0 to 600.")] int seconds,
        CancellationToken cancellationToken)
    {
        if (seconds is < 0 or > MaxSleepSeconds)
        {
            throw new McpToolException(
                ToolErrorCodes.ValidationFailed,
                $"seconds must be between 0 and {MaxSleepSeconds}.",
                "Try debug_sleep with seconds=30.");
        }

        var startedUtc = DateTimeOffset.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        return ToolResults.Ok(new DebugSleepResult(seconds, startedUtc, DateTimeOffset.UtcNow));
    }

    [McpServerTool(Name = "debug_fail")]
    [Description("Debug builds only. Throws the given structured error code, so the error contract can be tested. Do not use it for anything else.")]
    public static CallToolResult DebugFail(
        [Description("Error code from the tool contract, for example NOT_READY.")] string code,
        [Description("Optional message to return.")] string? message = null)
    {
        if (!ToolErrorCodes.All.Contains(code))
        {
            throw new McpToolException(
                ToolErrorCodes.ValidationFailed,
                $"Unknown error code '{code}'.",
                $"Use one of: {string.Join(", ", ToolErrorCodes.All)}.");
        }

        throw new McpToolException(
            code,
            message ?? $"Simulated {code} from debug_fail.",
            "This tool always fails on purpose.");
    }

    private sealed record DebugSleepResult(int SleptSeconds, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc);
}
#endif
