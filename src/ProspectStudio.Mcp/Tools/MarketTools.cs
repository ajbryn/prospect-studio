using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Market;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

/// <summary>
/// The market tool from mcp-tools.md §estimate_market. The sizing rules live in
/// <see cref="MarketSizingService"/> and the Census calls in <c>CensusCbpClient</c>; this class resolves
/// the geography, maps arguments in, and maps failures onto the error codes of §Errors.
/// </summary>
[McpServerToolType]
public sealed class MarketTools(GeographyService geography, MarketSizingService market)
{
    [McpServerTool(Name = "estimate_market")]
    [Description("Counts the business establishments in an area by industry, from Census County Business Patterns, with a breakdown of how many employ at least a given number of people. Use it to size a territory before searching for candidates. Needs reference data and a CENSUS_API_KEY.")]
    public async ValueTask<CallToolResult> EstimateMarketAsync(
        [Description("The NAICS industry codes to count, as a JSON array of strings such as [\"4931\", \"238210\"]. Use lookup_naics to find them. A code already covers its sub-industries, so pass the level you want.")]
        string[] naics,
        [Description("The area to size: {\"query\": \"Houston metro\"} for a place name, a scope such as {\"type\": \"counties\", \"values\": [\"Harris County, TX\"]} or {\"type\": \"zips\", \"values\": [\"77494\"]}, or a scope resolve_geography already returned, passed back unchanged.")]
        MarketGeoInput geo,
        [Description("Count only establishments with at least this many employees. Census publishes size bands, so a threshold inside a band rounds up to the next band edge and the response says so.")]
        int? minEmployees = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveAsync(geo, cancellationToken).ConfigureAwait(false);

        try
        {
            var request = new MarketEstimateRequest(naics ?? [], scope, minEmployees);
            return ToolResults.Ok(await market.EstimateAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (MarketNotReadyException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.NotReady,
                exception.Message,
                exception.Hint ?? "Check the server's configuration and try again.");
        }
        catch (MarketRequestException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.ValidationFailed,
                exception.Message,
                exception.Hint
                ?? "Pass NAICS codes of 2 to 6 digits (lookup_naics finds them) and a geography that covers "
                + "at least one county.");
        }
        catch (MarketRateLimitedException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.RateLimited,
                exception.Message,
                "Wait a few minutes and try again.");
        }
        catch (MarketExternalException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.ExternalApi,
                exception.Message,
                "The Census API is unavailable or answered unexpectedly; try again in a few minutes.");
        }
    }

    private async Task<ResolvedGeography> ResolveAsync(MarketGeoInput? geo, CancellationToken cancellationToken)
    {
        if (geo?.AsScope() is { } resolved)
        {
            return resolved;
        }

        try
        {
            return await geography
                .ResolveAsync(geo?.AsResolveRequest() ?? new GeoResolveRequest(), cancellationToken)
                .ConfigureAwait(false);
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
            // An area that matches nothing has no counties, and mcp-tools.md §estimate_market refuses an
            // empty scope rather than reporting a market of zero.
            throw new McpToolException(
                ToolErrorCodes.ValidationFailed,
                exception.Message,
                "Resolve the area with resolve_geography first, or name it more precisely, such as "
                + "'Harris County, TX' or 'Houston metro'.");
        }
        catch (GeographyRequestException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.ValidationFailed,
                exception.Message,
                "Pass geo as {\"query\": \"Houston metro\"}, or as a type with values.");
        }
        catch (GeographyUnsupportedException exception)
        {
            throw new McpToolException(
                ToolErrorCodes.Unsupported,
                exception.Message,
                "Dealer territories arrive in chunk C5; size the dealer's counties or ZIPs instead.");
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
