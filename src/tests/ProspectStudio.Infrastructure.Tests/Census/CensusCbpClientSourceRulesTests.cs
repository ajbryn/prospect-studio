using System.Reflection;
using ProspectStudio.Core.Market;
using System.Text.RegularExpressions;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Census;

/// <summary>
/// One rule that cannot be checked behaviourally. The unit tests inject their own
/// <see cref="HttpMessageHandler"/>, and redirect following lives in the handler - so an injected one
/// never follows a 302 whatever the client does, and the production handler's configuration would go
/// unchecked until someone ran it against the live API with a bad key and got a JSON parse error.
/// </summary>
public partial class CensusCbpClientSourceRulesTests
{
    [Fact]
    public void The_cbp_client_disables_automatic_redirects()
    {
        var source = File.ReadAllText(ClientSource());

        AutoRedirectDisabled().IsMatch(StripComments(source)).ShouldBeTrue(
            "The CBP client's own handler must set AllowAutoRedirect = false. Every data query without a "
            + "usable key answers 302 to missing_key.html or invalid_key.html; followed, that lands on a "
            + "200 HTML page and the key problem surfaces as an inexplicable JSON parse error.");
    }

    [Fact]
    public void The_cbp_client_accepts_an_injected_handler()
    {
        var source = File.ReadAllText(ClientSource());

        source.ShouldContain(
            "HttpMessageHandler", Case.Sensitive,
            "the handler is the seam the tests answer recorded bodies through. Without it the only way to "
            + "cover the client is to call api.census.gov from a unit test.");
    }

    [Fact]
    public void A_rate_limit_is_mapped_to_the_RATE_LIMITED_code()
    {
        // The client raising MarketRateLimitedException on a 429 is covered behaviourally; this is the
        // other half, that the tool turns it into the code §Errors documents instead of folding it into
        // EXTERNAL_API. Checked at source because reaching the mapping needs a real 429 from Census.
        var tool = StripComments(File.ReadAllText(ToolSource()));

        tool.ShouldContain(
            nameof(MarketRateLimitedException),
            Case.Sensitive,
            "estimate_market has to catch the rate-limit failure rather than let it fall through to the "
            + "central filter, which would report INTERNAL.");

        RateLimitMapping().IsMatch(tool).ShouldBeTrue(
            "mcp-tools.md §Errors lists RATE_LIMITED with the hint 'Add CENSUS_API_KEY or wait', so a 429 "
            + "has to surface under that code. Being told to investigate an outage when the remedy is to "
            + "wait sends the user the wrong way.");
    }

    private static string ToolSource()
    {
        var path = Path.Combine(SourceRoot, "ProspectStudio.Mcp", "Tools", "MarketTools.cs");
        File.Exists(path).ShouldBeTrue($"The market tool was not found at '{path}'.");
        return path;
    }

    private static string ClientSource()
    {
        var path = Path.Combine(SourceRoot, "ProspectStudio.Infrastructure", "Census", "CensusCbpClient.cs");
        File.Exists(path).ShouldBeTrue($"The CBP client was not found at '{path}'.");
        return path;
    }

    private static string SourceRoot { get; } = typeof(CensusCbpClientSourceRulesTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(attribute => attribute.Key == "ProspectStudioSourceRoot")
        .Value!;

    private static string StripComments(string text)
    {
        var withoutBlocks = BlockComment().Replace(text, " ");
        return string.Join(
            '\n',
            withoutBlocks.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"AllowAutoRedirect\s*=\s*false")]
    private static partial Regex AutoRedirectDisabled();

    [GeneratedRegex(@"ToolErrorCodes\s*\.\s*RateLimited")]
    private static partial Regex RateLimitMapping();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();
}
