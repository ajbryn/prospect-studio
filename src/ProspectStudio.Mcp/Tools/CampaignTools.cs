using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

/// <summary>
/// The campaign tools from mcp-tools.md §Campaign. Every rule lives in
/// <see cref="CampaignService"/>; this class only maps arguments in and results or error codes out.
/// </summary>
[McpServerToolType]
public sealed class CampaignTools(CampaignService campaigns)
{
    [McpServerTool(Name = "create_campaign")]
    [Description("Creates a campaign: a row in the Prospect Studio database and a folder named 'yyyy-MM <Name>' under the workspace's Campaigns folder. Call it before saving a search profile or finding candidates. Names are compared case-insensitively, so a name already in use fails with CONFLICT.")]
    public async ValueTask<CallToolResult> CreateCampaignAsync(
        [Description("Campaign name as the user would say it, for example 'Houston Scissor & Boom Lifts Q4'.")]
        string name,
        [Description("What is being sold, for example 'Scissor & boom lifts'.")]
        string? product = null,
        [Description("Anything worth remembering about the campaign.")]
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(await campaigns.CreateAsync(name, product, notes, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (DuplicateCampaignNameException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.Conflict,
                exception.Message,
                "Call list_campaigns to see what exists, then use the existing campaign or pick another name.");
        }
        catch (InvalidCampaignNameException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.ValidationFailed,
                exception.Message,
                "Pass a name with at least one letter or digit.");
        }
    }

    [McpServerTool(Name = "list_campaigns")]
    [Description("Lists campaigns, newest first, with their folder, status, product and lead count. Use it to find a campaignId before any other campaign tool.")]
    public async ValueTask<CallToolResult> ListCampaignsAsync(
        [Description("How many campaigns to return; 100 by default, 500 at most.")]
        int limit = CampaignService.DefaultPageSize,
        [Description("How many campaigns to skip, for paging through a long list.")]
        int offset = 0,
        CancellationToken cancellationToken = default) =>
        ToolResults.Ok(await campaigns.ListAsync(limit, offset, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "get_campaign")]
    [Description("Reports one campaign: its folder, status, saved search profile summary and lead counts by status, tier and dealer. Use it to check whether a campaign already has a profile or leads.")]
    public async ValueTask<CallToolResult> GetCampaignAsync(
        [Description("The campaign id from create_campaign or list_campaigns, for example 'cmp_7Q3KXM'.")]
        string campaignId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(await campaigns.GetAsync(campaignId, cancellationToken).ConfigureAwait(false));
        }
        catch (CampaignNotFoundException exception)
        {
            throw new McpToolException(ToolErrorCodes.NotFound, exception.Message, "Call list_campaigns for the ids that exist.");
        }
    }

    [McpServerTool(Name = "save_search_profile")]
    [Description("Validates a search profile against the search-profile schema and saves it to the campaign as search-profile.json. Call it once the user has confirmed the segments, geography and size filters. A profile that fails validation is rejected in full and any profile already saved is left untouched.")]
    public async ValueTask<CallToolResult> SaveSearchProfileAsync(
        [Description("The campaign id from create_campaign or list_campaigns.")]
        string campaignId,
        [Description("The search profile object: version, name, product, segments[], geography and the optional size, signals, exclusions, research and scoringWeights (which must sum to 1.0).")]
        JsonElement profile,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(await campaigns.SaveProfileAsync(campaignId, profile, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (CampaignNotFoundException exception)
        {
            throw new McpToolException(ToolErrorCodes.NotFound, exception.Message, "Call list_campaigns for the ids that exist.");
        }
        catch (CampaignFileLockedException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.FileLocked,
                exception.Message,
                $"Close '{exception.FilePath}' in whatever has it open, then call save_search_profile again.");
        }
        catch (SearchProfileInvalidException exception)
        {
            var problems = exception.Problems;
            throw new McpToolException(
                ToolErrorCodes.ValidationFailed,
                "The search profile is not valid.",
                $"Fix the {problems.Count} problem{(problems.Count == 1 ? string.Empty : "s")} listed in details and call save_search_profile again.",
                [.. problems.Select(problem => new ToolErrorDetail(problem.Pointer, problem.Message))]);
        }
    }
}
