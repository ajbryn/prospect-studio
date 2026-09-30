using ProspectStudio.Core.Configuration;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

internal static class TestEnvironment
{
    /// <summary>
    /// Environment for a server under test: its own workspace and data folders, and every other
    /// Prospect Studio variable blanked, so the developer's own environment cannot change what the
    /// contract tests see. A blank value falls back to the documented default.
    /// </summary>
    public static Dictionary<string, string?> For(string home, string data) => new(StringComparer.OrdinalIgnoreCase)
    {
        [PsOptionsFactory.HomeVariable] = home,
        [PsOptionsFactory.DataVariable] = data,
        [PsOptionsFactory.TrackingBaseUrlVariable] = string.Empty,
        [PsOptionsFactory.OfferPrefixVariable] = string.Empty,
        [PsOptionsFactory.UserAgentVariable] = string.Empty,
        [PsOptionsFactory.OvertureReleaseVariable] = string.Empty,
        [PsOptionsFactory.CbpYearVariable] = string.Empty,
        [PsOptionsFactory.CensusKeyVariable] = string.Empty,
        [PsOptionsFactory.OpenAiKeyVariable] = string.Empty,
        [PsOptionsFactory.GeminiKeyVariable] = string.Empty,
        [PsOptionsFactory.GoogleMapsKeyVariable] = string.Empty,
        [PsOptionsFactory.HubSpotKeyVariable] = string.Empty,
    };
}
