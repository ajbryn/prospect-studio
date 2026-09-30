using ProspectStudio.Core.Configuration;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

internal static class TestEnvironment
{
    /// <summary>
    /// The dev-convenience switch from implementation-plan C1 that seeds <c>poc/fixtures/brand-kit</c>
    /// into an empty <c>Brand Kit</c>. The server reads it through <see cref="PsOptionsFactory"/>, so
    /// the tests name the same constant instead of repeating the string.
    /// </summary>
    public const string SeedFixturesVariable = PsOptionsFactory.SeedFixturesVariable;

    /// <summary>
    /// Environment for a server under test: its own workspace and data folders, and every other
    /// Prospect Studio variable blanked, so the developer's own environment cannot change what the
    /// contract tests see. A blank value falls back to the documented default.
    /// </summary>
    public static Dictionary<string, string?> For(
        string home,
        string data,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
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
            [SeedFixturesVariable] = string.Empty,
        };

        if (overrides is not null)
        {
            foreach (var (name, value) in overrides)
            {
                environment[name] = value;
            }
        }

        return environment;
    }
}
