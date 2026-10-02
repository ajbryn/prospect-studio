using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Market;

namespace ProspectStudio.Infrastructure.Census;

/// <summary>
/// Registers market sizing: the CBP client and the service behind <c>estimate_market</c>, so the MCP
/// layer only maps the tool onto the service (CLAUDE.md §Hard rules).
/// </summary>
public static class CensusServiceCollectionExtensions
{
    public static IServiceCollection AddProspectStudioMarketSizing(
        this IServiceCollection services,
        PsOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<ICbpDataSource>(provider => new CensusCbpClient(
            options,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<MarketSizingService>();

        return services;
    }
}
