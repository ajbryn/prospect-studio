using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Core.Reference;

namespace ProspectStudio.Core.Status;

/// <param name="referenceData">
/// Looks at <c>refdata\</c> so <c>ready.referenceData</c> is a fact rather than a placeholder. Optional:
/// without it the report says reference data is not ready, which is the honest answer when nothing is
/// there to ask.
/// </param>
/// <param name="overture">
/// Looks at <c>overture\&lt;release&gt;\</c> so <c>ready.overture</c> reports the release and the states
/// actually on disk from C4 onward, instead of the placeholder C0 shipped (mcp-tools.md
/// §find_candidates). Optional: without it the report says no extract is prepared.
/// </param>
/// <param name="dealers">
/// Counts the three business lists, so <c>ready.dealers</c>, <c>ready.territories</c> and
/// <c>ready.suppression</c> are facts from C5 onward instead of the zeroes C0 shipped - it is how a
/// skill knows whether <c>import_list</c> still has to run. Optional, and only
/// <see cref="GetStatusAsync"/> reads it: <see cref="GetStatus"/> stays synchronous and reports zeroes.
/// </param>
public sealed class StatusService(
    PsOptions options,
    IReferenceDataInventory? referenceData = null,
    IOvertureDataInventory? overture = null,
    IDealerStore? dealers = null)
{
    /// <summary>The report with the list counts read from the database (mcp-tools.md §get_status).</summary>
    public async Task<StatusReport> GetStatusAsync(CancellationToken cancellationToken)
    {
        var report = GetStatus();
        if (dealers is null)
        {
            return report;
        }

        var counts = await dealers.CountListsAsync(cancellationToken).ConfigureAwait(false);

        return report with
        {
            Ready = report.Ready with
            {
                Dealers = counts.Dealers,
                Territories = counts.Territories,
                Suppression = counts.Suppression,
            },
        };
    }

    public StatusReport GetStatus()
    {
        var warnings = new List<string>();
        if (!options.Keys.HasCensus)
        {
            warnings.Add(
                $"No {PsOptionsFactory.CensusKeyVariable}: estimate_market cannot run. "
                + "Get a free key at https://api.census.gov/data/key_signup.html");
        }

        if (string.Equals(options.TrackingBaseUrl, PsOptionsFactory.DefaultTrackingBaseUrl, StringComparison.Ordinal))
        {
            warnings.Add($"No {PsOptionsFactory.TrackingBaseUrlVariable}: postcards would use the placeholder {PsOptionsFactory.DefaultTrackingBaseUrl}");
        }

        // No extract means no release: a release reported beside an empty state list would read as
        // though data for it had been prepared.
        var states = overture?.States ?? [];

        return new StatusReport(
            Version: ProductVersion.Current,
            Home: options.Home,
            Data: options.Data,
            Ready: new ReadinessReport(
                ReferenceData: referenceData?.IsComplete ?? false,
                Overture: new OvertureReadiness(states.Count == 0 ? null : overture?.Release, states),
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
