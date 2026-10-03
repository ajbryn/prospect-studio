using Microsoft.EntityFrameworkCore;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Domain;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// EF Core implementation of <see cref="ICandidateStore"/>. Writes go in batches of about 500 with
/// change tracking off and a fresh context per batch (technical-design §5.3): 5,000 candidates have to
/// land in under ten seconds, which rules out a <c>SaveChangesAsync</c> per row.
/// </summary>
public sealed class EfCandidateStore(IDbContextFactory<ProspectDbContext> contextFactory) : ICandidateStore
{
    /// <summary>Rows per <c>SaveChangesAsync</c> (technical-design §5.3).</summary>
    private const int BatchSize = 500;

    /// <summary>Ids per lookup query, so a 5,000-row call never builds a 5,000-parameter <c>IN</c>.</summary>
    private const int LookupSize = 500;

    public async Task<CandidateStoreResult> StoreAsync(
        string campaignId,
        IReadOnlyList<CandidateGroup> groups,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        ArgumentNullException.ThrowIfNull(groups);

        if (groups.Count == 0)
        {
            return new CandidateStoreResult(0, 0, 0, 0, 0, 0);
        }

        var members = groups
            .SelectMany((group, index) => group.Duplicates
                .Select(site => new Member(index, site, IsDuplicate: true))
                .Prepend(new Member(index, group.Primary, IsDuplicate: false)))
            .ToList();

        var existingSites = await ExistingSitesAsync(
            [.. members.Select(member => member.Site.OvertureId).Distinct(StringComparer.Ordinal)],
            cancellationToken).ConfigureAwait(false);

        var existingLeads = await ExistingLeadsAsync(campaignId, cancellationToken).ConfigureAwait(false);
        var nextLeadNumber = existingLeads.Count == 0
            ? 1
            : existingLeads.Values.Max(CandidateIds.LeadNumber) + 1;

        var now = DateTimeOffset.UtcNow;
        var companies = new List<Company>();
        var sites = new List<Site>();
        var records = new List<SourceRecord>();
        var leads = new List<Lead>();
        var companyIds = new string[groups.Count];
        var leadCount = 0;

        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];

            // A group that already has a site keeps that site's company, which is what makes a re-run
            // reuse the identity rather than mint a second one for the same business.
            var known = group.Duplicates.Prepend(group.Primary)
                .Select(site => existingSites.GetValueOrDefault(site.OvertureId))
                .FirstOrDefault(row => row is not null);

            if (known is not null)
            {
                companyIds[index] = known.CompanyId;
            }
            else
            {
                companyIds[index] = CandidateIds.Company();
                companies.Add(new Company
                {
                    Id = companyIds[index],
                    Name = group.Primary.Name,
                    NameNorm = group.Primary.NameNorm,
                    Domain = DomainKey.For(group.Primary.Website),
                });
            }
        }

        foreach (var member in members)
        {
            var site = member.Site;
            var companyId = companyIds[member.Group];

            if (!existingSites.TryGetValue(site.OvertureId, out var stored))
            {
                var siteId = CandidateIds.Site();
                stored = new StoredSite(siteId, companyId);
                existingSites[site.OvertureId] = stored;

                sites.Add(new Site
                {
                    Id = siteId,
                    CompanyId = companyId,
                    OvertureId = site.OvertureId,
                    Name = site.Name,
                    Address = site.Address,
                    City = site.City,
                    State = site.State,
                    Zip = site.Zip,
                    CountyFips = site.CountyFips,
                    Lat = site.Lat,
                    Lon = site.Lon,
                    Phone = site.Phone,
                    Website = site.Website,
                    TaxonomyPrimary = site.TaxonomyPrimary,
                    TaxonomyPath = site.TaxonomyPath,
                    BasicCategory = site.BasicCategory,
                    Confidence = site.Confidence,
                    Release = site.Release,
                });

                records.Add(new SourceRecord
                {
                    Id = CandidateIds.SourceRecord(),
                    SiteId = siteId,
                    Source = OvertureSource,
                    SourceId = site.OvertureId,
                    RetrievedAt = now,
                    License = OvertureLicense,
                    PayloadJson = site.PayloadJson,
                });
            }

            leadCount++;
            if (existingLeads.ContainsKey(stored.Id))
            {
                continue;
            }

            var leadId = CandidateIds.Lead(nextLeadNumber++);
            existingLeads[stored.Id] = leadId;

            leads.Add(new Lead
            {
                CampaignId = campaignId,
                Id = leadId,
                SiteId = stored.Id,
                Status = member.IsDuplicate ? LeadStatuses.Duplicate : LeadStatuses.Candidate,
                UpdatedAt = now,
            });
        }

        // Parents before children: foreign_keys is ON (technical-design §5.3), so companies, then
        // sites, then the rows that point at a site.
        await InsertAsync(companies, cancellationToken).ConfigureAwait(false);
        await InsertAsync(sites, cancellationToken).ConfigureAwait(false);
        await InsertAsync(records, cancellationToken).ConfigureAwait(false);
        await InsertAsync(leads, cancellationToken).ConfigureAwait(false);

        // Rows written, not rows seen: on an idempotent re-run these are all zero while Leads still
        // reports what the campaign holds.
        return new CandidateStoreResult(
            Companies: companies.Count,
            Sites: sites.Count,
            SourceRecords: records.Count,
            Leads: leadCount,
            NewLeads: leads.Count,
            Duplicates: groups.Sum(group => group.Duplicates.Count));
    }

    public async Task<IReadOnlyList<StoredLead>> ListLeadsAsync(
        string campaignId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = from lead in context.Leads.AsNoTracking()
                    join site in context.Sites.AsNoTracking() on lead.SiteId equals site.Id
                    where lead.CampaignId == campaignId
                    orderby lead.Id
                    select new StoredLead(lead.Id, site.OvertureId, lead.Status, site.Name);

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> FindLeadIdsAsync(
        string campaignId,
        IReadOnlyList<string> overtureIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overtureIds);

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (overtureIds.Count == 0)
        {
            return found;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        for (var offset = 0; offset < overtureIds.Count; offset += LookupSize)
        {
            var slice = overtureIds.Skip(offset).Take(LookupSize).ToList();

            var query = from lead in context.Leads.AsNoTracking()
                        join site in context.Sites.AsNoTracking() on lead.SiteId equals site.Id
                        where lead.CampaignId == campaignId && slice.Contains(site.OvertureId)
                        select new { site.OvertureId, lead.Id };

            foreach (var row in await query.ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                found[row.OvertureId] = row.Id;
            }
        }

        return found;
    }

    public async Task<int> ClearCandidatesWithoutResearchAsync(
        string campaignId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Set-based, per technical-design §5.3. The research table arrives in C6; until then every lead
        // find_candidates created is one without research.
        return await context.Leads
            .Where(lead => lead.CampaignId == campaignId
                && (lead.Status == LeadStatuses.Candidate || lead.Status == LeadStatuses.Duplicate))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private const string OvertureSource = "overture";

    /// <summary>Overture Places is ODbL; recorded per row so provenance survives a release change.</summary>
    private const string OvertureLicense = "ODbL-1.0";

    private async Task<Dictionary<string, StoredSite>> ExistingSitesAsync(
        IReadOnlyList<string> overtureIds,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, StoredSite>(StringComparer.Ordinal);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        for (var offset = 0; offset < overtureIds.Count; offset += LookupSize)
        {
            var slice = overtureIds.Skip(offset).Take(LookupSize).ToList();

            var rows = await context.Sites
                .AsNoTracking()
                .Where(site => slice.Contains(site.OvertureId))
                .Select(site => new { site.OvertureId, site.Id, site.CompanyId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                found[row.OvertureId] = new StoredSite(row.Id, row.CompanyId);
            }
        }

        return found;
    }

    private async Task<Dictionary<string, string>> ExistingLeadsAsync(
        string campaignId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await context.Leads
            .AsNoTracking()
            .Where(lead => lead.CampaignId == campaignId)
            .Select(lead => new { lead.SiteId, lead.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var leads = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            leads[row.SiteId] = row.Id;
        }

        return leads;
    }

    /// <summary>
    /// Technical-design §5.3's bulk-write shape: about 500 rows per <c>SaveChangesAsync</c>, change
    /// detection off for the batch, and a fresh context each time so the change tracker never grows
    /// past one batch.
    /// </summary>
    private async Task InsertAsync<TEntity>(List<TEntity> entities, CancellationToken cancellationToken)
        where TEntity : class
    {
        for (var offset = 0; offset < entities.Count; offset += BatchSize)
        {
            await using var context = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            context.ChangeTracker.AutoDetectChangesEnabled = false;
            context.Set<TEntity>().AddRange(entities.GetRange(offset, Math.Min(BatchSize, entities.Count - offset)));

            // acceptAllChangesOnSuccess: false skips the post-save fixup of 500 tracked entities that
            // this context is about to throw away anyway.
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record StoredSite(string Id, string CompanyId);

    private sealed record Member(int Group, CandidateSite Site, bool IsDuplicate);
}
