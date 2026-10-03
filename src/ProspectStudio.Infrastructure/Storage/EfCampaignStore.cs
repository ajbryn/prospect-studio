using Microsoft.EntityFrameworkCore;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Domain;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// EF Core implementation of <see cref="ICampaignStore"/>. A short-lived context per operation from
/// the factory, reads projected and untracked (technical-design §5.3).
/// </summary>
public sealed class EfCampaignStore(IDbContextFactory<ProspectDbContext> contextFactory) : ICampaignStore
{
    public async Task<Campaign?> FindAsync(string campaignId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Campaigns
            .AsNoTracking()
            .FirstOrDefaultAsync(campaign => campaign.Id == campaignId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string campaignId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Campaigns
            .AsNoTracking()
            .AnyAsync(campaign => campaign.Id == campaignId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Campaign?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Campaigns
            .AsNoTracking()
            .FirstOrDefaultAsync(campaign => campaign.Slug == slug, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.Campaigns.Add(campaign);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A race lost on the unique slug index is a duplicate name, not an internal failure. Asking
            // the store again keeps this provider-neutral: no SQLite error codes, no message matching.
            var existing = await FindBySlugAsync(campaign.Slug, cancellationToken).ConfigureAwait(false);
            if (existing is not null && !string.Equals(existing.Id, campaign.Id, StringComparison.Ordinal))
            {
                throw new DuplicateCampaignNameException(existing.Name, existing.Id);
            }

            throw;
        }
    }

    public async Task<CampaignPage> ListAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var total = await context.Campaigns.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.Campaigns
            .AsNoTracking()
            .OrderByDescending(campaign => campaign.CreatedAt)
            .ThenByDescending(campaign => campaign.Id)
            .Skip(offset)
            .Take(limit)
            .Select(campaign => new CampaignRow(
                campaign.Id,
                campaign.Name,
                campaign.FolderPath,
                campaign.Status,
                campaign.Product,
                campaign.CreatedAt,
                0))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CampaignPage(total, rows);
    }

    public async Task<bool> SaveProfileAsync(
        string campaignId,
        string? profileJson,
        DateTimeOffset? profileSavedAt,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var campaign = await context.Campaigns
            .FirstOrDefaultAsync(row => row.Id == campaignId, cancellationToken)
            .ConfigureAwait(false);
        if (campaign is null)
        {
            return false;
        }

        campaign.ProfileJson = profileJson;
        campaign.ProfileSavedAt = profileSavedAt;
        campaign.UpdatedAt = updatedAt;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SaveGeographyAsync(
        string campaignId,
        string? geoJson,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var campaign = await context.Campaigns
            .FirstOrDefaultAsync(row => row.Id == campaignId, cancellationToken)
            .ConfigureAwait(false);
        if (campaign is null)
        {
            return false;
        }

        campaign.GeoJson = geoJson;
        campaign.UpdatedAt = updatedAt;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<CampaignCounts> GetCountsAsync(string campaignId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var byStatus = await context.Leads
            .AsNoTracking()
            .Where(lead => lead.CampaignId == campaignId)
            .GroupBy(lead => lead.Status)
            .Select(group => new { Status = group.Key, Leads = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // byTier and byDealer stay empty until scoring (C6) and dealer assignment (C5) fill them.
        return new CampaignCounts(
            byStatus.ToDictionary(row => row.Status, row => row.Leads, StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            []);
    }
}
