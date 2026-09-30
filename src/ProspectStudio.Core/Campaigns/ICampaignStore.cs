using ProspectStudio.Core.Domain;

namespace ProspectStudio.Core.Campaigns;

/// <summary>
/// Campaign persistence. Core defines it, Infrastructure implements it with EF Core
/// (technical-design §5.3), so the desktop app can reuse this project unchanged.
/// </summary>
public interface ICampaignStore
{
    Task<Campaign?> FindAsync(string campaignId, CancellationToken cancellationToken);

    /// <summary>Whether an id is taken, without fetching the row it belongs to.</summary>
    Task<bool> ExistsAsync(string campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds a campaign by its normalized slug, which is how duplicate names are detected without
    /// relying on the database's text comparison (CLAUDE.md §Conventions).
    /// </summary>
    Task<Campaign?> FindBySlugAsync(string slug, CancellationToken cancellationToken);

    Task AddAsync(Campaign campaign, CancellationToken cancellationToken);

    Task<CampaignPage> ListAsync(int limit, int offset, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the profile document and when it was saved, or clears both when
    /// <paramref name="profileJson"/> is null, which is how a half-done save is undone. False when the
    /// campaign is gone.
    /// </summary>
    Task<bool> SaveProfileAsync(
        string campaignId,
        string? profileJson,
        DateTimeOffset? profileSavedAt,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
}
