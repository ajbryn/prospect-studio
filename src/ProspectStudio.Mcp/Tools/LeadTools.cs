using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Leads;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

/// <summary>
/// The five lead tools from mcp-tools.md §Leads. Every rule lives in <see cref="LeadService"/>; this
/// class maps arguments in and results or error codes out.
/// </summary>
[McpServerToolType]
public sealed class LeadTools(LeadService leads)
{
    [McpServerTool(Name = "list_leads")]
    [Description("Lists a campaign's leads as compact rows - id, company, city, segment, score, tier, dealer, status, research state and its strongest cited signal - highest score first by default. Filter by status, tier, dealer, minimum score or research state, and page with limit and offset; the total counts every matching lead, not just the page. Use get_lead for everything about one lead.")]
    public async ValueTask<CallToolResult> ListLeadsAsync(
        [Description("The campaign whose leads to list, for example \"cmp_7Q3KXM\".")]
        string campaignId,
        [Description("Keep only these lead statuses, as a JSON array: [\"review\", \"approved\"]. Omit for every status. One of candidate, suppressed, duplicate, review, approved, rejected, hold.")]
        string[]? status = null,
        [Description("Keep only these tiers, as a JSON array: [\"A\", \"B\"]. Omit for every tier. Unscored leads are in no tier.")]
        string[]? tier = null,
        [Description("Keep only the leads routed to this dealer id, for example \"gulf\".")]
        string? dealerId = null,
        [Description("Keep only leads scoring at least this much, 0 to 100. Unscored leads are left out.")]
        int? minScore = null,
        [Description("Keep only leads in this research state: \"none\", \"saved\" or \"no_signal\".")]
        string? researchStatus = null,
        [Description("Row order: \"score_desc\" (the default), \"score_asc\" or \"name_asc\".")]
        string? sort = null,
        [Description("How many rows to return; 25 by default, 100 at most.")]
        int? limit = null,
        [Description("How many rows to skip, for paging through the list.")]
        int? offset = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ListLeadsRequest(
            campaignId,
            status,
            tier,
            dealerId,
            minScore,
            researchStatus,
            sort,
            limit,
            offset);

        try
        {
            return ToolResults.Ok(await leads.ListAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (CampaignNotFoundException exception)
        {
            throw NotFound(exception);
        }
        catch (LeadRequestException exception)
        {
            throw Invalid(exception);
        }
    }

    [McpServerTool(Name = "get_lead")]
    [Description("Reports everything about one lead: the company's address and website, its provenance and confidence, the features and per-feature score breakdown that explain its score, the saved research with its cited signals, the dealer and branch it is routed to, notes and contacts. Use it before writing outreach copy about a lead.")]
    public async ValueTask<CallToolResult> GetLeadAsync(
        [Description("The campaign the lead belongs to, for example \"cmp_7Q3KXM\".")]
        string campaignId,
        [Description("The lead id from list_leads, for example \"L0001\".")]
        string leadId,
        [Description("Include the lead's website text excerpt. Nothing has been fetched until websites are prefetched, so it is null for now.")]
        bool includeWebExcerpt = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(
                await leads.GetAsync(campaignId, leadId, includeWebExcerpt, cancellationToken).ConfigureAwait(false));
        }
        catch (CampaignNotFoundException exception)
        {
            throw NotFound(exception);
        }
        catch (LeadNotFoundException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.NotFound,
                exception.Message,
                "Call list_leads for the lead ids in this campaign.");
        }
    }

    [McpServerTool(Name = "update_leads")]
    [Description("Changes a batch of leads: status, the dealer they are routed to, notes and contact details. Moving a lead to another dealer marks it as a manual override, which re-routing never undoes. A row that cannot be applied - an unknown lead, an unknown dealer, an unknown status, or a status change on a suppressed lead - is reported in errors while the rest of the batch still lands.")]
    public async ValueTask<CallToolResult> UpdateLeadsAsync(
        [Description("The campaign the leads belong to, for example \"cmp_7Q3KXM\".")]
        string campaignId,
        [Description("The changes to make, as a JSON array of objects: [{\"leadId\": \"L0007\", \"status\": \"approved\", \"dealerId\": \"bay\", \"notes\": \"Call first\"}]. Only leadId is required; every other field left out is left alone.")]
        LeadUpdate[] updates,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(
                await leads.UpdateAsync(campaignId, updates, cancellationToken).ConfigureAwait(false));
        }
        catch (CampaignNotFoundException exception)
        {
            throw NotFound(exception);
        }
        catch (LeadRequestException exception)
        {
            throw Invalid(exception);
        }
    }

    [McpServerTool(Name = "score_leads")]
    [Description("Scores every lead in a campaign 0-100 with the deterministic model - segment fit, size, facility, cited buying signals, distance to the dealer branch and source confidence - and stores a breakdown that explains each score. Suppressed leads are skipped and reported as a count; duplicate rows are not leads and appear in neither number. The weights used are remembered on the campaign, so save_research scores on the same scale. Safe to re-run: the same evidence always gives the same scores. Tier A needs a cited buying signal, so run it again after save_research.")]
    public async ValueTask<CallToolResult> ScoreLeadsAsync(
        [Description("The campaign whose leads to score, for example \"cmp_7Q3KXM\".")]
        string campaignId,
        [Description("Scoring weights to use instead of the campaign profile's, as a JSON object that sums to 1.0: {\"segmentFit\": 0.25, \"sizeFit\": 0.15, \"facilityFit\": 0.20, \"signals\": 0.25, \"proximity\": 0.05, \"confidence\": 0.10}.")]
        JsonElement? weights = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(await leads.ScoreAsync(campaignId, weights, cancellationToken).ConfigureAwait(false));
        }
        catch (CampaignNotFoundException exception)
        {
            throw NotFound(exception);
        }
        catch (LeadRequestException exception)
        {
            throw Invalid(exception);
        }
        catch (ScoringWeightsInvalidException exception)
        {
            throw Details(
                "The scoring weights are not valid.",
                "Every key must be a scoring feature and every value a number from 0 to 1, and they must "
                + "sum to 1.0. A feature you leave out counts as 0. Omit weights to use the campaign "
                + "profile's.",
                exception.Problems);
        }
    }

    [McpServerTool(Name = "save_research")]
    [Description("Saves your research for one lead - what the company does, its size, how well its facility fits, the buying signals you found with a URL and a date for each, a suggested angle and one personal line for the postcard - then re-scores the lead and reports what the research was worth. Every signal needs a source URL and a date; use status \"no_signal\" with no signals when you found nothing. Business-level public facts only: never cite safety records or citations.")]
    public async ValueTask<CallToolResult> SaveResearchAsync(
        [Description("The campaign the lead belongs to, for example \"cmp_7Q3KXM\".")]
        string campaignId,
        [Description("The lead id from list_leads, for example \"L0001\".")]
        string leadId,
        [Description("The research document: status, summary, rationale, and optionally segment, facilityFit, employeeEstimate, signals[], suggestedAngle, personalLine, contactRoles, llmAdjustment (-15 to 15), sources and cautions.")]
        JsonElement research,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(
                await leads.SaveResearchAsync(campaignId, leadId, research, cancellationToken).ConfigureAwait(false));
        }
        catch (CampaignNotFoundException exception)
        {
            throw NotFound(exception);
        }
        catch (LeadNotFoundException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.NotFound,
                exception.Message,
                "Call list_leads for the lead ids in this campaign.");
        }
        catch (LeadRequestException exception)
        {
            // A duplicate row: it exists and is listable, so this is a rule violation rather than a
            // NOT_FOUND, and the message names the lead to research instead.
            throw Invalid(exception);
        }
        catch (ResearchInvalidException exception)
        {
            var problems = exception.Problems;
            throw Details(
                "The research document is not valid.",
                $"Fix the {problems.Count} problem{(problems.Count == 1 ? string.Empty : "s")} listed in "
                + "details and call save_research again.",
                problems);
        }
    }

    private static McpToolException NotFound(CampaignNotFoundException exception) =>
        new(ToolErrorCodes.NotFound, exception.Message, "Use list_campaigns to see the campaigns that exist.");

    private static McpToolException Invalid(LeadRequestException exception) =>
        new(ToolErrorCodes.ValidationFailed, exception.Message, exception.Hint);

    private static McpToolException Details(
        string message,
        string hint,
        IReadOnlyList<Core.SearchProfiles.ValidationProblem> problems) =>
        new(
            ToolErrorCodes.ValidationFailed,
            message,
            hint,
            [.. problems.Select(problem => new ToolErrorDetail(problem.Pointer, problem.Message))]);
}
