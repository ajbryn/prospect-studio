using Microsoft.EntityFrameworkCore;
using ProspectStudio.Core.Dealers;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// EF Core implementation of <see cref="IDealerStore"/>: the reads behind <c>list_dealers</c>,
/// <c>get_status</c>'s list counts, <c>resolve_geography</c> type <c>dealer</c> and the inputs to
/// §7.3 and §7.4. A short-lived context per operation, projected and untracked (technical-design §5.3).
/// </summary>
public sealed class EfDealerStore(IDbContextFactory<ProspectDbContext> contextFactory) : IDealerStore
{
    public async Task<IReadOnlyList<DealerSummary>> ListDealersAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = from dealer in context.Dealers.AsNoTracking()
                    orderby dealer.Id
                    select new DealerSummary(
                        dealer.Id,
                        dealer.Name,
                        context.DealerBranches.Count(branch => branch.DealerId == dealer.Id),
                        context.Territories.Count(territory => territory.DealerId == dealer.Id));

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ListCounts> CountListsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return new ListCounts(
            await context.Dealers.CountAsync(cancellationToken).ConfigureAwait(false),
            await context.DealerBranches.CountAsync(cancellationToken).ConfigureAwait(false),
            await context.Territories.CountAsync(cancellationToken).ConfigureAwait(false),
            await context.Suppression.CountAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<TerritoryRule>> GetTerritoriesAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Ordered by id so §7.4's last tie-break is reached with the same list every time (NFR-3).
        return await context.Territories
            .AsNoTracking()
            .OrderBy(territory => territory.Id)
            .Select(territory => new TerritoryRule(
                territory.DealerId,
                territory.BranchId,
                territory.Level,
                territory.Code,
                territory.Priority))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BranchPoint>> GetBranchesAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.DealerBranches
            .AsNoTracking()
            .OrderBy(branch => branch.Id)
            .Select(branch => new BranchPoint(branch.DealerId, branch.Id, branch.Lat, branch.Lon))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SuppressionRule>> GetSuppressionAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Suppression
            .AsNoTracking()
            .OrderBy(row => row.Id)
            .Select(row => new SuppressionRule(row.Id, row.NameNorm, row.Domain, row.Zip, row.Reason))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DealerTerritoryScope?> FindTerritoryScopeAsync(
        string dealerId,
        CancellationToken cancellationToken)
    {
        var wanted = (dealerId ?? string.Empty).Trim().ToLowerInvariant();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // The dealer is looked up first: a dealer with no territory rows is a different answer from a
        // dealer that does not exist, and an empty scope would read as a real area.
        var dealer = await context.Dealers
            .AsNoTracking()
            .Where(row => row.Id == wanted)
            .Select(row => new { row.Id, row.Name })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (dealer is null)
        {
            return null;
        }

        var rules = await context.Territories
            .AsNoTracking()
            .Where(territory => territory.DealerId == wanted)
            .Select(territory => new { territory.Level, territory.Code })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new DealerTerritoryScope(
            dealer.Id,
            dealer.Name,
            [
                .. rules
                    .Where(rule => rule.Level == TerritoryLevels.Zip)
                    .Select(rule => rule.Code)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ],
            [
                .. rules
                    .Where(rule => rule.Level == TerritoryLevels.County)
                    .Select(rule => rule.Code)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ]);
    }
}
