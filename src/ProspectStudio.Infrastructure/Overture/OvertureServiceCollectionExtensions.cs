using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Reference;
using ProspectStudio.Infrastructure.Reference;

namespace ProspectStudio.Infrastructure.Overture;

/// <summary>
/// Registers the Overture extracts, candidate search and the Overture step of the setup pipeline, so
/// the MCP layer only has to map tools onto services (CLAUDE.md §Hard rules).
/// </summary>
public static class OvertureServiceCollectionExtensions
{
    public static IServiceCollection AddProspectStudioOverture(
        this IServiceCollection services,
        PsOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(new OvertureDataFiles(options.OvertureDirectory, options.OvertureRelease));
        services.AddSingleton<IOvertureDataInventory>(
            provider => provider.GetRequiredService<OvertureDataFiles>());

        // Singleton on purpose: lookup_overture_categories caches a full scan of the extract per state
        // (mcp-tools.md calls them "counts from the extract, cached"), and a per-call instance would
        // rescan a 205 MB file every time.
        services.AddSingleton<IPlacesSource>(provider => new DuckDbPlacesSource(
            provider.GetRequiredService<OvertureDataFiles>(),
            provider.GetRequiredService<ReferenceDataFiles>()));

        services.AddSingleton<IOvertureExtractSource>(provider => new S3OvertureExtractSource(
            provider.GetRequiredService<IGeographyReference>(),
            provider.GetRequiredService<ProspectStudioHttpClient>().Client));

        services.AddSingleton(provider => new OvertureDataPreparer(
            provider.GetRequiredService<OvertureDataFiles>(),
            provider.GetRequiredService<IOvertureExtractSource>()));

        services.AddSingleton<CandidateSearchService>();
        return services;
    }
}
