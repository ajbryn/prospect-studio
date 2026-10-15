using Microsoft.EntityFrameworkCore;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Candidates;
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

                // The real count, on the same basis as GetCountsAsync's byStatus - duplicates excluded -
                // so the two tools cannot disagree about how many leads a campaign has. A correlated
                // subquery in the projection keeps the page one round trip rather than one per row.
                context.Leads.Count(lead =>
                    lead.CampaignId == campaign.Id && lead.Status != LeadStatuses.Duplicate)))
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

    public async Task<bool> SaveScoringWeightsAsync(
        string campaignId,
        string? scoringWeightsJson,
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

        campaign.ScoringWeightsJson = scoringWeightsJson;
        campaign.UpdatedAt = updatedAt;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<CampaignCounts> GetCountsAsync(string campaignId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Duplicates are left out, as they are in list_leads: a 'duplicate' row is provenance for §7.2's
        // merge, not a prospect, and counting it would make this breakdown disagree with both
        // find_candidates' 'stored' and the list the marketer actually pages through.
        var byStatus = await context.Leads
            .AsNoTracking()
            .Where(lead => lead.CampaignId == campaignId && lead.Status != LeadStatuses.Duplicate)
            .GroupBy(lead => lead.Status)
            .Select(group => new { Status = group.Key, Leads = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Scored leads only: an unscored lead is in no tier, and a null key would read as one. Duplicates
        // are excluded for the same reason as in byStatus - today nothing scores one, so the filter is
        // belt-and-braces, but a breakdown that is correct only by coincidence is one chunk away from not
        // being correct.
        var byTier = await context.Leads
            .AsNoTracking()
            .Where(lead => lead.CampaignId == campaignId
                && lead.Tier != null
                && lead.Status != LeadStatuses.Duplicate)
            .GroupBy(lead => lead.Tier!)
            .Select(group => new { Tier = group.Key, Leads = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The same query find_candidates' byDealer uses, so the two breakdowns cannot drift apart.
        var byDealer = await EfLeadRoutingStore
            .LeadCountsByDealer(context, campaignId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CampaignCounts(
            byStatus.ToDictionary(row => row.Status, row => row.Leads, StringComparer.Ordinal),
            byTier.ToDictionary(row => row.Tier, row => row.Leads, StringComparer.Ordinal),
            byDealer);
    }
}
