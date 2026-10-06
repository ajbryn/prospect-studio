using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

/// <summary>
/// <c>assign_dealers</c> and <c>apply_suppression</c> from mcp-tools.md §assign_dealers /
/// apply_suppression. <c>find_candidates</c> runs both already; these exist so either can be re-run on
/// its own after a list changes. Every rule lives in <see cref="ILeadRoutingStore"/>.
/// </summary>
[McpServerToolType]
public sealed class RoutingTools(ILeadRoutingStore routing)
{
    [McpServerTool(Name = "assign_dealers")]
    [Description("Routes a campaign's candidate leads to their local dealer using the imported territories: a ZIP rule wins over a county rule, and a lead in a county no territory covers is reported as a coverage gap. Returns how many leads are routed, how many changed, the gaps and the counts per dealer. Safe to re-run, and a lead moved to another dealer by hand is left alone.")]
    public async ValueTask<CallToolResult> AssignDealersAsync(
        [Description("The campaign whose leads to route, for example \"cmp_7Q3KXM\".")]
        string campaignId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(
                await routing.AssignDealersAsync(campaignId, cancellationToken).ConfigureAwait(false));
        }
        catch (CampaignNotFoundException exception)
        {
            throw NotFound(exception);
        }
    }

    [McpServerTool(Name = "apply_suppression")]
    [Description("Removes a campaign's leads that appear on the imported suppression list - existing customers, dealers, competitors and do-not-contact entries - matching by website domain, by normalized company name in the same ZIP, or by a close name in the same ZIP. Returns how many leads are suppressed, how many changed and the counts per reason. Safe to re-run after the list changes.")]
    public async ValueTask<CallToolResult> ApplySuppressionAsync(
        [Description("The campaign whose leads to check against the suppression list, for example \"cmp_7Q3KXM\".")]
        string campaignId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(
                await routing.ApplySuppressionAsync(campaignId, cancellationToken).ConfigureAwait(false));
        }
        catch (CampaignNotFoundException exception)
        {
            throw NotFound(exception);
        }
    }

    private static McpToolException NotFound(CampaignNotFoundException exception) =>
        new(ToolErrorCodes.NotFound, exception.Message, "Use list_campaigns to see the campaigns that exist.");
}
