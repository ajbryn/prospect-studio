using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Naics;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

/// <summary>
/// The lookup tools from mcp-tools.md §lookup_naics and §resolve_geography. Every rule lives in
/// <see cref="NaicsLookupService"/> and <see cref="GeographyService"/>; this class maps arguments in and
/// results or error codes out.
/// </summary>
[McpServerToolType]
public sealed class LookupTools(NaicsLookupService naics, GeographyService geography)
{
    [McpServerTool(Name = "lookup_naics")]
    [Description("Searches the NAICS 2022 industry classification by keyword and returns matching codes with their titles and level. Use it to turn a description such as 'electrical contractor' into the codes a search profile or market estimate needs.")]
    public async ValueTask<CallToolResult> LookupNaicsAsync(
        [Description("Words to search for in industry titles, for example 'electrical contractor' or 'warehouse'.")]
        string query,
        [Description("How many results to return; 10 by default, 50 at most.")]
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        ToolResults.Ok(await naics.LookupAsync(query, limit, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "resolve_geography")]
    [Description("Turns a place into a search scope: a state, a list of counties, a metro area (CBSA), a list of ZIP codes, or a radius around a point or address. Returns the counties, states, ZIPs and bounding box it resolved to, plus alternatives when the name is ambiguous. Needs reference data, so run prepare_data first.")]
    public async ValueTask<CallToolResult> ResolveGeographyAsync(
        [Description("A place name such as 'Houston metro', 'Harris County, TX' or 'Texas'. Use this when you do not know which kind of place it is.")]
        string? query = null,
        [Description("The kind of scope to resolve: state, counties, cbsa, zips or radius.")]
        string? type = null,
        [Description("The places for that type, as a JSON array of strings: [\"Harris County, TX\"] or [\"77494\", \"77449\"].")]
        string[]? values = null,
        [Description("The centre of a radius scope: either lat and lon, or a one-line address to geocode.")]
        GeoCenter? center = null,
        [Description("The radius in miles, required when type is 'radius'.")]
        double? radiusMiles = null,
        CancellationToken cancellationToken = default)
    {
        var request = new GeoResolveRequest(query, type, values, center, radiusMiles);

        try
        {
            return ToolResults.Ok(await geography.ResolveAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (GeographyNotReadyException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.NotReady,
                exception.Message,
                exception.Hint
                ?? "Run prepare_data first, or 'ProspectStudio.Mcp setup --states TX' from a terminal.");
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
                "Pass one input form: query, or type with values, or type 'radius' with center and radiusMiles.");
        }
        catch (GeographyUnsupportedException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.Unsupported,
                exception.Message,
                "Dealer territories arrive in chunk C5; resolve the dealer's counties or ZIPs instead.");
        }
        catch (GeographyExternalException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.ExternalApi,
                exception.Message,
                "Pass center.lat and center.lon instead of an address, or try again later.");
        }
    }
}
