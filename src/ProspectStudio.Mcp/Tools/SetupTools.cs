using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Status;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

[McpServerToolType]
public sealed class SetupTools(StatusService status)
{
    [McpServerTool(Name = "get_status")]
    [Description("Reports the Prospect Studio server version, the workspace and data folders, which reference data and lists are ready, which API keys are configured, and any warnings. Use it first when asked whether Prospect Studio is set up, or before a tool that needs prepared data.")]
    public async ValueTask<CallToolResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ToolResults.Ok(await status.GetStatusAsync(cancellationToken).ConfigureAwait(false));
}
