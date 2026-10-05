using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Geography;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

/// <summary>
/// <c>find_candidates</c> from mcp-tools.md §find_candidates. Every rule lives in
/// <see cref="CandidateSearchService"/>; this class maps arguments in and results or error codes out.
/// </summary>
[McpServerToolType]
public sealed class CandidateTools(CandidateSearchService candidates)
{
    [McpServerTool(Name = "find_candidates")]
    [Description("Finds candidate companies for a campaign from the local Overture Places extract: scopes them to the campaign's geography, matches the search profile's Overture categories and name keywords, applies the profile's exclusions, deduplicates, and stores them as leads. Returns counts and a small sample, not every row. Re-running with the same inputs changes nothing. Needs an Overture extract, so run prepare_data for the states first.")]
    public async ValueTask<CallToolResult> FindCandidatesAsync(
        [Description("The campaign to store the candidates in, for example \"cmp_7Q3KXM\".")]
        string campaignId,
        [Description("Where to search, in the same shape resolve_geography takes. Omit it to use the saved search profile's geography.")]
        GeoResolveRequest? geo = null,
        [Description("Overture categories to match, as a JSON array of strings: [\"warehouse\", \"electrician\"]. Omit to use the profile's segments. Use lookup_overture_categories to find real names.")]
        string[]? categories = null,
        [Description("Words to look for in a company name, as a JSON array of strings: [\"fulfillment\", \"racking\"]. Omit to use the profile's segments. A place matched only by a keyword is still included.")]
        string[]? keywords = null,
        [Description("Overture categories to drop even when a keyword matched, as a JSON array of strings: [\"restaurant\"]. Replaces the profile's exclusions.overtureCategories; pass [] to exclude nothing.")]
        string[]? excludedCategories = null,
        [Description("The lowest Overture confidence to accept, 0 to 1. Defaults to the profile's minConfidence, or 0.6.")]
        double? minConfidence = null,
        [Description("The most places to read from each state's extract; 5000 by default.")]
        int? limit = null,
        [Description("Clear the campaign's existing candidates (those with no saved research) before storing.")]
        bool replace = false,
        CancellationToken cancellationToken = default)
    {
        var request = new FindCandidatesRequest(
            campaignId,
            geo,
            categories,
            keywords,
            excludedCategories,
            minConfidence,
            limit,
            replace);

        try
        {
            return ToolResults.Ok(await candidates.FindAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (CampaignNotFoundException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.NotFound,
                exception.Message,
                "Use list_campaigns to see the campaigns that exist.");
        }
        catch (CandidatesNotReadyException exception)
        {
            throw new McpToolException(ToolErrorCodes.NotReady, exception.Message, exception.Hint);
        }
        catch (CandidateRequestException exception)
        {
            throw new McpToolException(ToolErrorCodes.ValidationFailed, exception.Message, exception.Hint);
        }
        catch (GeographyNotFoundException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.NotFound,
                exception.Message,
                "Try a more specific form, such as 'Harris County, TX', 'Houston metro' or a ZIP code.");
        }
        catch (GeographyRequestException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.ValidationFailed,
                exception.Message,
                "Pass geo as one input form: query, or type with values, or type 'radius' with center and radiusMiles.");
        }
        catch (GeographyUnsupportedException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.Unsupported,
                exception.Message,
                "Dealer territories arrive in chunk C5; pass the dealer's counties or ZIPs instead.");
        }
        catch (GeographyExternalException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.ExternalApi,
                exception.Message,
                "Pass geo.center.lat and geo.center.lon instead of an address, or try again later.");
        }
    }
}
