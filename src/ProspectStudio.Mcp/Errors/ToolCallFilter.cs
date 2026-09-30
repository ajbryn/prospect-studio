using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ProspectStudio.Mcp.Errors;

public static class ToolCallFilter
{
    /// <summary>
    /// The one place where a <see cref="McpToolException"/> thrown by a tool becomes the structured
    /// error contract, and where every tool call is logged (name, duration, outcome). The filter
    /// runs inside the SDK's own exception handling, which would otherwise replace the message with
    /// a bare "An error occurred.", so the exception must never escape it.
    /// </summary>
    public static IMcpServerBuilder WithProspectStudioToolFilter(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithRequestFilters(filters =>
            filters.AddCallToolFilter(next => (context, cancellationToken) => InvokeAsync(next, context, cancellationToken)));
    }

    private static async ValueTask<CallToolResult> InvokeAsync(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        var tool = context.Params?.Name ?? "(unknown)";
        var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger("ProspectStudio.Tools");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            logger?.LogInformation(
                "Tool {Tool} finished in {ElapsedMs} ms (isError={IsError})",
                tool,
                stopwatch.ElapsedMilliseconds,
                result.IsError == true);
            return result;
        }
        catch (McpToolException exception)
        {
            logger?.LogWarning(
                "Tool {Tool} failed in {ElapsedMs} ms with {Code}: {Reason}",
                tool,
                stopwatch.ElapsedMilliseconds,
                exception.Code,
                exception.Message);
            return ToolResults.Error(exception);
        }
        catch (OperationCanceledException)
        {
            logger?.LogInformation("Tool {Tool} cancelled after {ElapsedMs} ms", tool, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            // The detail stays in the log: the client gets a code it can branch on and nothing else.
            logger?.LogError(exception, "Tool {Tool} threw after {ElapsedMs} ms", tool, stopwatch.ElapsedMilliseconds);
            return ToolResults.Error(new McpToolException(
                ToolErrorCodes.Internal,
                $"The tool '{tool}' failed unexpectedly.",
                "Check the Prospect Studio log under PROSPECT_STUDIO_DATA\\logs, then try again."));
        }
    }
}
