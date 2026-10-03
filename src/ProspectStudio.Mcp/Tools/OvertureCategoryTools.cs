using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

/// <summary>
/// <c>lookup_overture_categories</c> from mcp-tools.md §lookup_overture_categories: the real category
/// names in a state's extract, with how many places carry each. It is what a skill uses to write a
/// search profile, so a name it has not seen in the data is never returned.
/// </summary>
[McpServerToolType]
public sealed class OvertureCategoryTools(CandidateSearchService candidates)
{
    [McpServerTool(Name = "lookup_overture_categories")]
    [Description("Searches the Overture place categories in a prepared state extract and returns each category's hierarchy path and how many places carry it in that state. Use it to turn a description such as 'warehouse' into the category names a search profile needs; every name comes from the data, so none is invented. Needs an Overture extract, so run prepare_data for the state first.")]
    public async ValueTask<CallToolResult> LookupOvertureCategoriesAsync(
        [Description("Words to search for in a category name or its hierarchy, for example 'warehouse' or 'contractor'. Omit it to list the commonest categories.")]
        string? query = null,
        [Description("The two-letter state code whose extract to count in, for example \"TX\". Defaults to the first prepared state.")]
        string? state = null,
        [Description("How many categories to return; 15 by default.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await candidates
                .LookupCategoriesAsync(query, state, limit, cancellationToken)
                .ConfigureAwait(false);

            return ToolResults.Ok(new OvertureCategoryResults(results));
        }
        catch (CandidatesNotReadyException exception)
        {
            throw new McpToolException(ToolErrorCodes.NotReady, exception.Message, exception.Hint);
        }
    }
}

/// <summary>The <c>{results:[...]}</c> envelope mcp-tools.md §lookup_overture_categories returns.</summary>
public sealed record OvertureCategoryResults(IReadOnlyList<OvertureCategoryCount> Results);
