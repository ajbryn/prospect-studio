using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Reference;

namespace ProspectStudio.Core.Status;

/// <param name="referenceData">
/// Looks at <c>refdata\</c> so <c>ready.referenceData</c> is a fact rather than a placeholder. Optional:
/// without it the report says reference data is not ready, which is the honest answer when nothing is
/// there to ask.
/// </param>
public sealed class StatusService(PsOptions options, IReferenceDataInventory? referenceData = null)
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
                ReferenceData: referenceData?.IsComplete ?? false,
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
