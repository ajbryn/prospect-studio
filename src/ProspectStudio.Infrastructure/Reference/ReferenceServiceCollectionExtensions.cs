using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Naics;
using ProspectStudio.Core.Reference;

namespace ProspectStudio.Infrastructure.Reference;

/// <summary>
/// Registers reference data, NAICS lookup and geography resolution, so the MCP layer only has to map
/// tools onto the services (CLAUDE.md §Hard rules).
/// </summary>
public static class ReferenceServiceCollectionExtensions
{
    /// <summary>Where the raw Census downloads are kept between runs.</summary>
    public const string SourceCacheFolder = "refsources";

    public static IServiceCollection AddProspectStudioReferenceData(
        this IServiceCollection services,
        PsOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(new ReferenceDataFiles(options.ReferenceDataDirectory));
        services.AddSingleton<IReferenceDataInventory>(
            provider => provider.GetRequiredService<ReferenceDataFiles>());

        services.AddSingleton<ProspectStudioHttpClient>();
        services.AddSingleton<IReferenceFileSource>(provider => new CensusReferenceFileSource(
            provider.GetRequiredService<ProspectStudioHttpClient>(),
            Path.Combine(options.CacheDirectory, SourceCacheFolder),
            provider.GetRequiredService<TimeProvider>()));

        services.AddSingleton(provider => new ReferenceDataPreparer(
            options.ReferenceDataDirectory,
            provider.GetRequiredService<IReferenceFileSource>(),
            provider.GetRequiredService<TimeProvider>()));

        services.AddSingleton<INaicsCatalog, EmbeddedNaicsCatalog>();
        services.AddSingleton<NaicsLookupService>();

        services.AddSingleton<IGeographyReference, DuckDbGeographyReference>();
        services.AddSingleton<IAddressGeocoder, CensusGeocoder>();
        services.AddSingleton<GeographyService>();

        return services;
    }
}
