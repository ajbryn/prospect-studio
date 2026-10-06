using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Core.Domain;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// EF Core implementation of <see cref="ILeadRoutingStore"/>: <c>assign_dealers</c> (technical-design
/// §7.4) and <c>apply_suppression</c> (§7.3) over a campaign's leads. Both decide what to write in
/// Core and then write it in batches from a short-lived context (§5.3).
/// </summary>
public sealed class EfLeadRoutingStore(
    IDbContextFactory<ProspectDbContext> contextFactory,
    IDealerStore dealers) : ILeadRoutingStore
{
    /// <summary>Rows per <c>SaveChangesAsync</c> (technical-design §5.3).</summary>
    private const int BatchSize = 500;

    public async Task<AssignDealersResult> AssignDealersAsync(
        string campaignId,
        CancellationToken cancellationToken)
    {
        await EnsureCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        var territories = await dealers.GetTerritoriesAsync(cancellationToken).ConfigureAwait(false);
        var branches = await dealers.GetBranchesAsync(cancellationToken).ConfigureAwait(false);

        // Candidates only: a suppressed lead is never mailed, so routing it would overstate a dealer's
        // list, and a status somebody decided is not this tool's to revisit.
        var leads = await LeadsAsync(
                campaignId,
                lead => lead.Status == LeadStatuses.Candidate,
                cancellationToken)
            .ConfigureAwait(false);

        var changes = new List<LeadChange>();
        var overrides = 0;
        var gaps = 0;
        var assigned = 0;

        foreach (var lead in leads)
        {
            if (string.Equals(lead.Assignment, Assignments.Override, StringComparison.Ordinal))
            {
                // §7.4: a manual override is never overwritten by re-assignment, and it counts under the
                // dealer it was moved to.
                overrides++;
                assigned += lead.DealerId is null ? 0 : 1;
                continue;
            }

            var routed = TerritoryAssigner.Assign(
                new AssignmentSubject(lead.Zip, lead.CountyFips, lead.Lat, lead.Lon),
                territories,
                branches);

            if (string.Equals(routed.Assignment, Assignments.Gap, StringComparison.Ordinal))
            {
                gaps++;
            }
            else
            {
                assigned++;
            }

            if (!string.Equals(lead.DealerId, routed.DealerId, StringComparison.Ordinal)
                || !string.Equals(lead.BranchId, routed.BranchId, StringComparison.Ordinal)
                || !string.Equals(lead.Assignment, routed.Assignment, StringComparison.Ordinal))
            {
                changes.Add(new LeadChange(
                    lead.LeadId,
                    row =>
                    {
                        row.DealerId = routed.DealerId;
                        row.BranchId = routed.BranchId;
                        row.Assignment = routed.Assignment;
                    }));
            }
        }

        await ApplyAsync(campaignId, changes, cancellationToken).ConfigureAwait(false);

        return new AssignDealersResult(
            assigned,
            changes.Count,
            gaps,
            overrides,
            await ByDealerAsync(campaignId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ApplySuppressionResult> ApplySuppressionAsync(
        string campaignId,
        CancellationToken cancellationToken)
    {
        await EnsureCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        var rules = await dealers.GetSuppressionAsync(cancellationToken).ConfigureAwait(false);
        var leads = await SuppressibleLeadsAsync(campaignId, cancellationToken).ConfigureAwait(false);

        var changes = new List<LeadChange>();
        var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
        var approved = 0;
        var restored = 0;

        foreach (var lead in leads)
        {
            var hit = SuppressionMatcher.Match(
                new SuppressionSubject(NameNormalizer.Normalize(lead.Name), DomainKey.For(lead.Website), lead.Zip),
                rules);

            var wasSuppressed = string.Equals(lead.Status, LeadStatuses.Suppressed, StringComparison.Ordinal);

            // An override is a dealer somebody chose by hand, and mcp-tools.md is explicit that neither
            // routing tool ever clears it. Keeping it is also what makes the two tools reach the same
            // state in either order. PARTNER: LeadCountsByDealer's status filter is then the only thing
            // keeping a suppressed override out of a dealer's list - change one, check the other.
            var keepsDealer = string.Equals(lead.Assignment, Assignments.Override, StringComparison.Ordinal);

            if (hit is null)
            {
                // Only reachable once a row has actually left the list, which an ordinary upsert import
                // never does - it takes import_list's replace. Suppression is kept deliberately
                // one-way otherwise: a stale entry costs a lead, a dropped dnc row costs a promise.
                if (wasSuppressed)
                {
                    // Back to whatever the lead was before, not to 'candidate': suppressing an approved
                    // company and then dropping the row from the list must not quietly spend the
                    // marketer's approval. Null covers every lead suppressed before this column existed.
                    var release = lead.PreSuppressionStatus ?? LeadStatuses.Candidate;

                    if (!string.Equals(release, LeadStatuses.Candidate, StringComparison.Ordinal))
                    {
                        restored++;
                    }

                    changes.Add(new LeadChange(lead.LeadId, row =>
                    {
                        row.Status = release;
                        row.SuppressionReason = null;
                        row.SuppressionId = null;
                        row.PreSuppressionStatus = null;
                    }));
                }

                continue;
            }

            byReason[hit.Reason] = byReason.GetValueOrDefault(hit.Reason) + 1;

            if (wasSuppressed
                && string.Equals(lead.SuppressionReason, hit.Reason, StringComparison.Ordinal)
                && string.Equals(lead.SuppressionId, hit.SuppressionId, StringComparison.Ordinal)
                && (keepsDealer || (lead.DealerId is null && lead.Assignment is null)))
            {
                continue;
            }

            if (string.Equals(lead.Status, LeadStatuses.Approved, StringComparison.Ordinal))
            {
                approved++;
            }

            // A lead already suppressed keeps the status it is remembering: re-running this tool must
            // not overwrite the remembered 'approved' with 'suppressed' and lose it after all.
            var remembered = wasSuppressed ? lead.PreSuppressionStatus : lead.Status;

            // A suppressed company is never mailed, so an automatically routed lead loses its dealer:
            // counting it under one would overstate that dealer's list and its packet.
            changes.Add(new LeadChange(lead.LeadId, row =>
            {
                row.Status = LeadStatuses.Suppressed;
                row.SuppressionReason = hit.Reason;
                row.SuppressionId = hit.SuppressionId;
                row.PreSuppressionStatus = remembered;

                if (!keepsDealer)
                {
                    row.DealerId = null;
                    row.BranchId = null;
                    row.Assignment = null;
                }
            }));
        }

        await ApplyAsync(campaignId, changes, cancellationToken).ConfigureAwait(false);

        return new ApplySuppressionResult(byReason.Values.Sum(), changes.Count, byReason, approved, restored);
    }

    public async Task<IReadOnlyList<RoutedLead>> ListRoutedLeadsAsync(
        string campaignId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = from lead in context.Leads.AsNoTracking()
                    join site in context.Sites.AsNoTracking() on lead.SiteId equals site.Id
                    where lead.CampaignId == campaignId
                    orderby lead.Id
                    select new RoutedLead(
                        lead.Id,
                        site.OvertureId,
                        lead.Status,
                        lead.DealerId,
                        lead.BranchId,
                        lead.Assignment,
                        lead.SuppressionReason,
                        lead.SuppressionId);

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureCampaignAsync(string campaignId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (!await context.Campaigns
                .AsNoTracking()
                .AnyAsync(campaign => campaign.Id == campaignId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new CampaignNotFoundException(campaignId);
        }
    }

    /// <summary>
    /// Every lead suppression may act on, which is <strong>every status but <c>duplicate</c></strong>.
    /// A duplicate is kept for provenance (§7.2) and is not a mailable lead, so suppressing it would
    /// only inflate the counts. Everything else is in scope <em>including <c>approved</c></em>: a
    /// do-not-contact row imported after a lead was approved has to be able to take it off the mailing
    /// list, which is the case the suppression list exists for. The run reports how many approved leads
    /// it removed, because that is a decision being overridden rather than a routine filter.
    /// </summary>
    private Task<List<RoutableLead>> SuppressibleLeadsAsync(
        string campaignId,
        CancellationToken cancellationToken) =>
        LeadsAsync(campaignId, lead => lead.Status != LeadStatuses.Duplicate, cancellationToken);

    private async Task<List<RoutableLead>> LeadsAsync(
        string campaignId,
        Expression<Func<Lead, bool>> status,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = from lead in context.Leads.AsNoTracking().Where(status)
                    join site in context.Sites.AsNoTracking() on lead.SiteId equals site.Id
                    where lead.CampaignId == campaignId
                    orderby lead.Id
                    select new RoutableLead(
                        lead.Id,
                        lead.Status,
                        lead.SuppressionReason,
                        lead.SuppressionId,
                        lead.PreSuppressionStatus,
                        lead.DealerId,
                        lead.BranchId,
                        lead.Assignment,
                        site.Name,
                        site.Website,
                        site.Zip,
                        site.CountyFips,
                        site.Lat,
                        site.Lon);

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Leads per dealer, with the dealer's name, in <c>get_campaign</c>'s own shape. Only the leads
    /// still in play are counted: a suppressed lead has no dealer and a duplicate never had one.
    /// </summary>
    private async Task<IReadOnlyList<DealerLeadCount>> ByDealerAsync(
        string campaignId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await LeadCountsByDealer(context, campaignId).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>byDealer</c> query, shared with <see cref="EfCampaignStore.GetCountsAsync"/> so the
    /// breakdown <c>find_candidates</c> reports and the one <c>get_campaign</c> reports cannot drift.
    /// </summary>
    /// <remarks>
    /// The status filter is the rule, not the nulled dealer columns: a suppressed lead that was routed
    /// by hand keeps its dealer so the override survives, and only this filter keeps it out of the
    /// dealer's list and packet. PARTNER: it works with <c>keepsDealer</c> in
    /// <see cref="ApplySuppressionAsync"/>, and neither half fails visibly on its own - dropping this
    /// filter would quietly mail a suppressed company, dropping that branch would quietly discard a
    /// dealer somebody chose. Change one, check the other.
    /// </remarks>
    internal static IQueryable<DealerLeadCount> LeadCountsByDealer(ProspectDbContext context, string campaignId) =>
        from lead in context.Leads.AsNoTracking()
        join dealer in context.Dealers.AsNoTracking() on lead.DealerId equals dealer.Id
        where lead.CampaignId == campaignId && lead.Status == LeadStatuses.Candidate
        group lead by new { DealerId = dealer.Id, dealer.Name } into grouped
        orderby grouped.Key.DealerId
        select new DealerLeadCount(grouped.Key.DealerId, grouped.Key.Name, grouped.Count());

    private async Task ApplyAsync(
        string campaignId,
        List<LeadChange> changes,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        for (var offset = 0; offset < changes.Count; offset += BatchSize)
        {
            var slice = changes.GetRange(offset, Math.Min(BatchSize, changes.Count - offset));
            var ids = slice.Select(change => change.LeadId).ToList();

            await using var context = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            var rows = await context.Leads
                .Where(lead => lead.CampaignId == campaignId && ids.Contains(lead.Id))
                .ToDictionaryAsync(lead => lead.Id, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

            foreach (var change in slice)
            {
                if (!rows.TryGetValue(change.LeadId, out var lead))
                {
                    continue;
                }

                change.Apply(lead);
                lead.UpdatedAt = now;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record LeadChange(string LeadId, Action<Lead> Apply);

    private sealed record RoutableLead(
        string LeadId,
        string Status,
        string? SuppressionReason,
        string? SuppressionId,
        string? PreSuppressionStatus,
        string? DealerId,
        string? BranchId,
        string? Assignment,
        string Name,
        string? Website,
        string? Zip,
        string CountyFips,
        double Lat,
        double Lon);
}
