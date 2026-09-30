using System.Globalization;

namespace ProspectStudio.Core.Configuration;

/// <summary>
/// Binds <see cref="PsOptions"/> from a key/value lookup. Pure: it never reads the process
/// environment or the file system, so tests can supply a lookup instead of mutating the environment.
/// </summary>
public static class PsOptionsFactory
{
    public const string HomeVariable = "PROSPECT_STUDIO_HOME";
    public const string DataVariable = "PROSPECT_STUDIO_DATA";
    public const string TrackingBaseUrlVariable = "PS_TRACKING_BASE_URL";
    public const string OfferPrefixVariable = "PS_OFFER_PREFIX";
    public const string UserAgentVariable = "PS_USER_AGENT";
    public const string OvertureReleaseVariable = "PS_OVERTURE_RELEASE";
    public const string CbpYearVariable = "PS_CBP_YEAR";
    public const string CensusKeyVariable = "CENSUS_API_KEY";
    public const string OpenAiKeyVariable = "OPENAI_API_KEY";
    public const string GeminiKeyVariable = "GEMINI_API_KEY";
    public const string GoogleMapsKeyVariable = "GOOGLE_MAPS_API_KEY";
    public const string HubSpotKeyVariable = "HUBSPOT_TOKEN";

    public const string UserProfileVariable = "USERPROFILE";
    public const string LocalAppDataVariable = "LOCALAPPDATA";

    public const string DefaultTrackingBaseUrl = "https://example.com/lp?code={code}";
    public const string DefaultUserAgent = "ProspectStudioBot/0.1 (+mailto:marketing@example.com)";
    public const string DefaultOvertureRelease = "2026-09-23.1";
    public const string FallbackOfferPrefix = "LIFT";
    public const string HomeFolderName = "Prospect Studio";
    public const string DataFolderName = "ProspectStudio";

    public static PsOptions Create(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var userProfile = Value(environment, UserProfileVariable) ?? string.Empty;
        var localAppData = Value(environment, LocalAppDataVariable)
            ?? (userProfile.Length == 0 ? string.Empty : Path.Combine(userProfile, "AppData", "Local"));

        return new PsOptions
        {
            Home = Value(environment, HomeVariable) ?? Path.Combine(userProfile, "Documents", HomeFolderName),
            Data = Value(environment, DataVariable) ?? Path.Combine(localAppData, DataFolderName),
            TrackingBaseUrl = Value(environment, TrackingBaseUrlVariable) ?? DefaultTrackingBaseUrl,
            OfferPrefix = Value(environment, OfferPrefixVariable),
            UserAgent = Value(environment, UserAgentVariable) ?? DefaultUserAgent,
            OvertureRelease = Value(environment, OvertureReleaseVariable) ?? DefaultOvertureRelease,
            CbpYear = Year(Value(environment, CbpYearVariable)),
            Keys = new ApiKeys
            {
                Census = Value(environment, CensusKeyVariable),
                OpenAi = Value(environment, OpenAiKeyVariable),
                Gemini = Value(environment, GeminiKeyVariable),
                GoogleMaps = Value(environment, GoogleMapsKeyVariable),
                HubSpot = Value(environment, HubSpotKeyVariable),
            },
        };
    }

    private static string? Value(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int? Year(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year is >= 1900 and <= 2999
            ? year
            : null;
}
