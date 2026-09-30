using ProspectStudio.Core.Configuration;

namespace ProspectStudio.Core.Status;

public sealed class StatusService(PsOptions options)
{
    public StatusReport GetStatus()
    {
        var warnings = new List<string>();
        if (!options.Keys.HasCensus)
        {
            warnings.Add($"No {PsOptionsFactory.CensusKeyVariable}: limited to 500 calls/day");
        }

        if (string.Equals(options.TrackingBaseUrl, PsOptionsFactory.DefaultTrackingBaseUrl, StringComparison.Ordinal))
        {
            warnings.Add($"No {PsOptionsFactory.TrackingBaseUrlVariable}: postcards would use the placeholder {PsOptionsFactory.DefaultTrackingBaseUrl}");
        }

        return new StatusReport(
            Version: ProductVersion.Current,
            Home: options.Home,
            Data: options.Data,
            Ready: new ReadinessReport(
                ReferenceData: false,
                Overture: new OvertureReadiness(Release: null, States: []),
                BrandKit: false,
                Dealers: 0,
                Territories: 0,
                Suppression: 0),
            Keys: new ConfiguredKeys(
                Census: options.Keys.HasCensus,
                OpenAi: options.Keys.HasOpenAi,
                Gemini: options.Keys.HasGemini,
                GoogleMaps: options.Keys.HasGoogleMaps,
                HubSpot: options.Keys.HasHubSpot),
            TrackingBaseUrl: options.TrackingBaseUrl,
            Warnings: warnings);
    }
}
