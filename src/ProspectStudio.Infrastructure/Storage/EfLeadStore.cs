using Microsoft.EntityFrameworkCore;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Domain;
using ProspectStudio.Core.Leads;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// EF Core implementation of <see cref="ILeadStore"/>: the <c>leads</c>, <c>research</c> and
/// <c>signals</c> tables behind mcp-tools.md §Leads. Every filter and sort runs on a real column
/// (CLAUDE.md), reads project straight into compact DTOs, and writes go through short-lived contexts in
/// batches (technical-design §5.3).
/// </summary>
public sealed class EfLeadStore(IDbContextFactory<ProspectDbContext> contextFactory) : ILeadStore
{
    /// <summary>Rows per <c>SaveChangesAsync</c> (technical-design §5.3).</summary>
    private const int BatchSize = 500;

    public async Task<LeadListPage> ListAsync(LeadQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var filtered = Filtered(context, query);

        var total = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await Sorted(filtered, query.Sort)
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(row => new LeadListRecord(
                row.Lead.Id,
                row.Site.Name,
                row.Site.City,
                row.Site.TaxonomyPrimary,
                row.Site.TaxonomyPath,
                row.Lead.Score,
                row.Lead.Tier,
                row.DealerName,
                row.Lead.Status,
                row.Lead.ResearchStatus))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new LeadListPage(total, rows);
    }

    public async Task<IReadOnlyList<SignalRow>> ReadSignalsAsync(
        string campaignId,
        IReadOnlyList<string> leadIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leadIds);

        if (leadIds.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var ids = leadIds.ToList();

        return await context.Signals
            .AsNoTracking()
            .Where(signal => signal.CampaignId == campaignId && ids.Contains(signal.LeadId))
            .OrderBy(signal => signal.LeadId)
            .ThenBy(signal => signal.Id)
            .Select(signal => new SignalRow(signal.LeadId, signal.Type, signal.Text, signal.Url, signal.Date))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LeadRecord?> FindAsync(string campaignId, string leadId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var found = await (
                from lead in context.Leads.AsNoTracking()
                join site in context.Sites.AsNoTracking() on lead.SiteId equals site.Id
                where lead.CampaignId == campaignId && lead.Id == leadId
                select new
                {
                    Lead = lead,
                    Site = site,
                    DealerName = context.Dealers
                        .Where(dealer => dealer.Id == lead.DealerId)
                        .Select(dealer => dealer.Name)
                        .FirstOrDefault(),
                    BranchName = context.DealerBranches
                        .Where(branch => branch.Id == lead.BranchId)
                        .Select(branch => branch.Name)
                        .FirstOrDefault(),
                    ResearchJson = context.Research
                        .Where(row => row.CampaignId == lead.CampaignId && row.LeadId == lead.Id)
                        .Select(row => row.ResearchJson)
                        .FirstOrDefault(),
                })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (found is null)
        {
            return null;
        }

        var signals = await context.Signals
            .AsNoTracking()
            .Where(signal => signal.CampaignId == campaignId && signal.LeadId == leadId)
            .OrderBy(signal => signal.Id)
            .Select(signal => new SignalRow(signal.LeadId, signal.Type, signal.Text, signal.Url, signal.Date))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new LeadRecord(
            found.Lead.Id,
            found.Lead.Status,
            found.Site.Name,
            found.Site.Address,
            found.Site.City,
            found.Site.State,
            found.Site.Zip,
            found.Site.Website,
            found.Site.Phone,
            found.Site.TaxonomyPrimary,
            found.Site.TaxonomyPath,
            found.Site.Confidence,
            found.Site.OvertureId,
            found.Site.Release,
            found.Lead.FeaturesJson,
            found.Lead.Score,
            found.Lead.Tier,
            found.Lead.ScoreBreakdownJson,
            found.Lead.ResearchStatus,
            found.Lead.Notes,
            found.Lead.ContactName,
            found.Lead.ContactTitle,
            found.Lead.Cohort,
            found.Lead.DealerId,
            found.DealerName,
            found.Lead.BranchId,
            found.BranchName,
            found.Lead.Assignment,
            found.Lead.SuppressionReason,
            found.ResearchJson,
            signals,
            found.Lead.UpdatedAt);
    }

    public async Task<LeadName?> FindPrimaryAsync(
        string campaignId,
        string duplicateLeadId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // §7.2 collapses a dedupe group into one companies row, so the duplicate's own site names the
        // company and the lead that was kept is the non-duplicate lead on another site of it.
        var companyId = await (
                from lead in context.Leads.AsNoTracking()
                join site in context.Sites.AsNoTracking() on lead.SiteId equals site.Id
                where lead.CampaignId == campaignId && lead.Id == duplicateLeadId
                select site.CompanyId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (companyId is null)
        {
            return null;
        }

        return await (
                from lead in context.Leads.AsNoTracking()
                join site in context.Sites.AsNoTracking() on lead.SiteId equals site.Id
                where lead.CampaignId == campaignId
                    && site.CompanyId == companyId
                    && lead.Status != LeadStatuses.Duplicate
                orderby lead.Id
                select new LeadName(lead.Id, site.Name))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LeadEvidence>> ReadEvidenceAsync(
        string campaignId,
        string? leadId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var leads = await (
                from lead in context.Leads.AsNoTracking()
                join site in context.Sites.AsNoTracking() on lead.SiteId equals site.Id
                where lead.CampaignId == campaignId
                    && (leadId == null || lead.Id == leadId)

                    // A 'duplicate' row is the same company as another lead, kept so §7.2's merge can be
                    // explained. Scoring it would double-count a company in the tier counts a marketer
                    // reads as a work queue, so it is not part of the campaign's lead universe.
                    && lead.Status != LeadStatuses.Duplicate
                orderby lead.Id
                select new
                {
                    lead.Id,
                    lead.Status,
                    site.Name,
                    site.TaxonomyPrimary,
                    site.TaxonomyPath,
                    site.Confidence,
                    site.Lat,
                    site.Lon,
                    lead.DealerId,
                    lead.BranchId,
                    lead.Score,
                    lead.Tier,
                    lead.FeaturesJson,
                    lead.ScoreBreakdownJson,
                    Branch = context.DealerBranches
                        .Where(branch => branch.Id == lead.BranchId && branch.DealerId == lead.DealerId)
                        .Select(branch => new { branch.Lat, branch.Lon })
                        .FirstOrDefault(),
                    Research = context.Research
                        .Where(row => row.CampaignId == lead.CampaignId && row.LeadId == lead.Id)
                        .Select(row => new { row.ResearchJson, row.LlmAdjustment })
                        .FirstOrDefault(),
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // One query for every signal in play rather than one per lead: a Houston campaign is thousands
        // of leads, and the research that carries signals is a small fraction of them.
        var signals = await context.Signals
            .AsNoTracking()
            .Where(signal => signal.CampaignId == campaignId && (leadId == null || signal.LeadId == leadId))
            .OrderBy(signal => signal.Id)
            .Select(signal => new { signal.LeadId, signal.Type, signal.Date })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byLead = signals
            .GroupBy(signal => signal.LeadId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(signal => new SignalFact(signal.Type, signal.Date)).ToList(),
                StringComparer.Ordinal);

        return
        [
            .. leads.Select(lead => new LeadEvidence(
                lead.Id,
                lead.Status,
                lead.Name,
                lead.TaxonomyPrimary,
                lead.TaxonomyPath,
                lead.Confidence,
                lead.Lat,
                lead.Lon,
                lead.DealerId,
                lead.BranchId,
                lead.Branch?.Lat,
                lead.Branch?.Lon,
                lead.Research?.ResearchJson,
                lead.Research?.LlmAdjustment ?? 0,
                byLead.TryGetValue(lead.Id, out var facts) ? facts : [],
                new StoredScore(lead.Score, lead.Tier, lead.FeaturesJson, lead.ScoreBreakdownJson))),
        ];
    }

    public async Task<int> SaveScoresAsync(
        string campaignId,
        IReadOnlyList<LeadScoreWrite> scores,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scores);

        var written = 0;

        for (var offset = 0; offset < scores.Count; offset += BatchSize)
        {
            var slice = scores.Skip(offset).Take(BatchSize).ToList();
            var ids = slice.Select(score => score.LeadId).ToList();

            await using var context = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            var rows = await context.Leads
                .Where(lead => lead.CampaignId == campaignId && ids.Contains(lead.Id))
                .ToDictionaryAsync(lead => lead.Id, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

            foreach (var score in slice)
            {
                if (!rows.TryGetValue(score.LeadId, out var lead))
                {
                    continue;
                }

                lead.Score = score.Score;
                lead.Tier = score.Tier;
                lead.FeaturesJson = score.FeaturesJson;
                lead.ScoreBreakdownJson = score.ScoreBreakdownJson;

                // Every row that reaches here is a row whose score changed - the caller filters the no-ops
                // out - so stamping it is honest, and a re-run that changes nothing stamps nothing.
                lead.UpdatedAt = updatedAt;
                written++;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    public async Task<IReadOnlyList<LeadMutationTarget>> FindTargetsAsync(
        string campaignId,
        IReadOnlyList<string> leadIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leadIds);

        if (leadIds.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var ids = leadIds.ToList();

        return await (
                from lead in context.Leads.AsNoTracking()
                join site in context.Sites.AsNoTracking() on lead.SiteId equals site.Id
                where lead.CampaignId == campaignId && ids.Contains(lead.Id)
                orderby lead.Id
                select new LeadMutationTarget(
                    lead.Id,
                    lead.Status,
                    lead.DealerId,
                    lead.BranchId,
                    lead.Assignment,
                    site.Lat,
                    site.Lon))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> ApplyMutationsAsync(
        string campaignId,
        IReadOnlyList<LeadMutation> mutations,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutations);

        var applied = 0;

        for (var offset = 0; offset < mutations.Count; offset += BatchSize)
        {
            var slice = mutations.Skip(offset).Take(BatchSize).ToList();
            var ids = slice.Select(mutation => mutation.LeadId).ToList();

            await using var context = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            var rows = await context.Leads
                .Where(lead => lead.CampaignId == campaignId && ids.Contains(lead.Id))
                .ToDictionaryAsync(lead => lead.Id, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

            foreach (var mutation in slice)
            {
                if (!rows.TryGetValue(mutation.LeadId, out var lead))
                {
                    continue;
                }

                // A null member is "leave it alone": update_leads changes only what the caller named.
                lead.Status = mutation.Status ?? lead.Status;
                lead.DealerId = mutation.DealerId ?? lead.DealerId;
                lead.BranchId = mutation.BranchId ?? lead.BranchId;
                lead.Assignment = mutation.Assignment ?? lead.Assignment;
                lead.Notes = mutation.Notes ?? lead.Notes;
                lead.ContactName = mutation.ContactName ?? lead.ContactName;
                lead.ContactTitle = mutation.ContactTitle ?? lead.ContactTitle;

                // From the caller's clock, like save_research's saved_at: two writers stamping the same
                // column from different clocks is how a lead ends up looking edited before it was created
                // under a test clock.
                lead.UpdatedAt = updatedAt;
                applied++;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return applied;
    }

    public async Task SaveResearchAsync(
        string campaignId,
        string leadId,
        ResearchWrite research,
        LeadScoreWrite score,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(research);
        ArgumentNullException.ThrowIfNull(score);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var existing = await context.Research
            .FirstOrDefaultAsync(
                row => row.CampaignId == campaignId && row.LeadId == leadId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            context.Research.Add(new Research
            {
                CampaignId = campaignId,
                LeadId = leadId,
                ResearchJson = research.ResearchJson,
                LlmAdjustment = research.LlmAdjustment,
                SavedAt = research.SavedAt,
            });
        }
        else
        {
            existing.ResearchJson = research.ResearchJson;
            existing.LlmAdjustment = research.LlmAdjustment;
            existing.SavedAt = research.SavedAt;
        }

        // The previous document's signals go with it. Leaving them behind would keep scoring evidence
        // the researcher has withdrawn, and a second save of the same document would double the
        // signals §7.6 counts.
        await context.Signals
            .Where(signal => signal.CampaignId == campaignId && signal.LeadId == leadId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var signal in research.Signals)
        {
            context.Signals.Add(new Signal
            {
                Id = signal.Id,
                CampaignId = campaignId,
                LeadId = leadId,
                Type = signal.Type,
                Text = signal.Text,
                Url = signal.Url,
                Date = signal.Date,
            });
        }

        var lead = await context.Leads
            .FirstOrDefaultAsync(row => row.CampaignId == campaignId && row.Id == leadId, cancellationToken)
            .ConfigureAwait(false);

        if (lead is not null)
        {
            lead.ResearchStatus = research.ResearchStatus;

            // In the same transaction as the document it was computed from: research stored against a
            // score that was never updated is a lead whose number disagrees with its own evidence.
            lead.Score = score.Score;
            lead.Tier = score.Tier;
            lead.FeaturesJson = score.FeaturesJson;
            lead.ScoreBreakdownJson = score.ScoreBreakdownJson;
            lead.UpdatedAt = research.SavedAt;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The campaign's leads with the filters applied, joined to the site they describe and the dealer
    /// they are routed to. The dealer's <em>name</em> comes along because that is what a person reads in
    /// a <c>list_leads</c> row.
    /// </summary>
    private static IQueryable<LeadListJoin> Filtered(ProspectDbContext context, LeadQuery query)
    {
        var rows = from lead in context.Leads.AsNoTracking()
                   join site in context.Sites.AsNoTracking() on lead.SiteId equals site.Id
                   join company in context.Companies.AsNoTracking() on site.CompanyId equals company.Id
                   where lead.CampaignId == query.CampaignId
                   select new LeadListJoin
                   {
                       Lead = lead,
                       Site = site,

                       // §7.1's normalized name, which is what name_asc orders on: all name matching in
                       // this codebase happens on the normalized column (CLAUDE.md), and so does this
                       // ordering, because the raw name's order depends on the provider's collation.
                       NameNorm = company.NameNorm,
                       DealerName = context.Dealers
                           .Where(dealer => dealer.Id == lead.DealerId)
                           .Select(dealer => dealer.Name)
                           .FirstOrDefault(),
                   };

        if (query.Statuses.Count > 0)
        {
            var statuses = query.Statuses.ToList();
            rows = rows.Where(row => statuses.Contains(row.Lead.Status));
        }
        else
        {
            // A 'duplicate' row is provenance for §7.2's merge, not a prospect: it is the same company
            // as another lead in the list. Showing it unasked would offer the marketer the same company
            // twice and make the total disagree with the campaign's own lead count. A caller who wants
            // them names the status.
            rows = rows.Where(row => row.Lead.Status != LeadStatuses.Duplicate);
        }

        if (query.Tiers.Count > 0)
        {
            var tiers = query.Tiers.ToList();
            rows = rows.Where(row => row.Lead.Tier != null && tiers.Contains(row.Lead.Tier));
        }

        if (query.DealerId is { Length: > 0 } dealerId)
        {
            rows = rows.Where(row => row.Lead.DealerId == dealerId);
        }

        if (query.MinScore is { } minimum)
        {
            // An unscored lead has no score to compare, so it is not "at least" anything.
            rows = rows.Where(row => row.Lead.Score != null && row.Lead.Score >= minimum);
        }

        if (query.ResearchStatus is { Length: > 0 } researchStatus)
        {
            rows = rows.Where(row => row.Lead.ResearchStatus == researchStatus);
        }

        return rows;
    }

    /// <summary>
    /// The documented sorts. Unscored leads come last whichever way the score sorts - they are not
    /// comparable - and every sort ends on the lead id, because paging that is not totally ordered both
    /// repeats and skips rows.
    /// </summary>
    private static IQueryable<LeadListJoin> Sorted(IQueryable<LeadListJoin> rows, string sort) => sort switch
    {
        LeadSorts.ScoreAsc => rows
            .OrderBy(row => row.Lead.Score == null)
            .ThenBy(row => row.Lead.Score)
            .ThenBy(row => row.Lead.Id),
        LeadSorts.NameAsc => rows
            .OrderBy(row => row.NameNorm)
            .ThenBy(row => row.Lead.Id),
        _ => rows
            .OrderBy(row => row.Lead.Score == null)
            .ThenByDescending(row => row.Lead.Score)
            .ThenBy(row => row.Lead.Id),
    };

    /// <summary>
    /// The join <see cref="Filtered"/> builds. A named type rather than an anonymous one so
    /// <see cref="Sorted"/> can take it as a parameter.
    /// </summary>
    private sealed class LeadListJoin
    {
        public required Lead Lead { get; init; }

        public required Site Site { get; init; }

        /// <summary>
        /// The company's <c>name_norm</c> (§7.1). <c>name_asc</c> orders on this rather than on
        /// <c>sites.name</c>: SQLite's default text comparison is case-sensitive where SQL Server's is
        /// not, so ordering on the raw name would reorder the list under exactly the provider swap
        /// CLAUDE.md's neutrality rule exists to keep cheap. <c>name_norm</c> is lower-case ASCII by
        /// construction, so every collation agrees about it.
        /// </summary>
        public required string NameNorm { get; init; }

        public string? DealerName { get; init; }
    }
}
