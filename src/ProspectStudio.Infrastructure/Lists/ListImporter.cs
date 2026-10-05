using Microsoft.EntityFrameworkCore;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Core.Domain;
using ProspectStudio.Infrastructure.Storage;

namespace ProspectStudio.Infrastructure.Lists;

/// <summary>
/// <c>import_list</c> for the three business lists (mcp-tools.md §import_list): reads a CSV or XLSX,
/// reports the rows it could not use without abandoning the ones it could, and upserts the rest, so
/// importing the same file twice creates nothing (NFR-3).
/// </summary>
public sealed class ListImporter(IDbContextFactory<ProspectDbContext> contextFactory) : IListImporter
{
    /// <summary>Rows per <c>SaveChangesAsync</c>, with a fresh context each time (technical-design §5.3).</summary>
    private const int BatchSize = 500;

    public Task<ImportListResult> ImportAsync(
        string kind,
        string path,
        string? reason,
        CancellationToken cancellationToken) =>
        ImportAsync(kind, path, reason, replace: false, cancellationToken);

    public async Task<ImportListResult> ImportAsync(
        string kind,
        string path,
        string? reason,
        bool replace,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var wanted = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (!ImportListKinds.IsKnown(wanted))
        {
            throw new ImportListRequestException(
                $"'{kind}' is not a list kind.",
                $"Use one of {string.Join(", ", ImportListKinds.All)}.");
        }

        ImportListKindRules.RejectUnbuilt(wanted);

        if (!File.Exists(path))
        {
            throw new ImportListFileNotFoundException(
                $"There is no file at '{path}'.",
                $"Check the path. The server looked for '{Path.GetFileName(path)}' in "
                + $"'{Path.GetDirectoryName(Path.GetFullPath(path))}'.");
        }

        var file = await ListFileReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        Require(file, wanted, path);

        return wanted switch
        {
            ImportListKinds.Dealers => await DealersAsync(file, cancellationToken).ConfigureAwait(false),
            ImportListKinds.Territories => await TerritoriesAsync(file, cancellationToken).ConfigureAwait(false),
            _ => await SuppressionAsync(file, reason, path, replace, cancellationToken).ConfigureAwait(false),
        };
    }

    private static void Require(ListFile file, string kind, string path)
    {
        var missing = ListRows.ColumnsFor(kind)
            .Where(column => !file.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count > 0)
        {
            throw new ImportListRequestException(
                $"'{Path.GetFileName(path)}' is missing the column(s) {string.Join(", ", missing)}.",
                $"A {kind} list has the same headers as poc/fixtures/{kind}.csv. Found: "
                + string.Join(", ", file.Columns.Where(column => column.Length > 0)));
        }
    }

    private async Task<ImportListResult> DealersAsync(ListFile file, CancellationToken cancellationToken)
    {
        var errors = new List<ImportRowError>();
        var imported = 0;
        var updated = 0;

        foreach (var batch in Batches(file.Rows))
        {
            var parsed = new List<(int Number, Dealer Dealer, DealerBranch? Branch)>();
            foreach (var row in batch)
            {
                try
                {
                    parsed.Add((row.Number, ListRows.ReadDealer(row), ListRows.ReadBranch(row)));
                }
                catch (ImportRowException exception)
                {
                    errors.Add(new ImportRowError(row.Number, exception.Message));
                }
            }

            if (parsed.Count == 0)
            {
                continue;
            }

            await using var context = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            var dealerIds = parsed.Select(entry => entry.Dealer.Id).Distinct(StringComparer.Ordinal).ToList();
            var stored = await context.Dealers
                .Where(dealer => dealerIds.Contains(dealer.Id))
                .ToDictionaryAsync(dealer => dealer.Id, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

            var branchIds = parsed
                .Where(entry => entry.Branch is not null)
                .Select(entry => entry.Branch!.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var storedBranches = await context.DealerBranches
                .Where(branch => branchIds.Contains(branch.Id))
                .ToDictionaryAsync(branch => branch.Id, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

            foreach (var (number, dealer, branch) in parsed)
            {
                // A branch id already owned by another dealer would otherwise be re-parented in
                // silence, moving every lead that branch serves to the wrong business.
                if (branch is not null
                    && storedBranches.TryGetValue(branch.Id, out var owned)
                    && !string.Equals(owned.DealerId, branch.DealerId, StringComparison.Ordinal))
                {
                    errors.Add(new ImportRowError(
                        number,
                        $"Branch '{branch.Id}' already belongs to dealer '{owned.DealerId}'."));
                    continue;
                }

                if (stored.TryGetValue(dealer.Id, out var existing))
                {
                    Copy(dealer, existing);
                    updated++;
                }
                else
                {
                    context.Dealers.Add(dealer);
                    stored[dealer.Id] = dealer;
                    imported++;
                }

                if (branch is null)
                {
                    continue;
                }

                if (storedBranches.TryGetValue(branch.Id, out var existingBranch))
                {
                    Copy(branch, existingBranch);
                }
                else
                {
                    context.DealerBranches.Add(branch);
                    storedBranches[branch.Id] = branch;
                }
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return ImportListResult.From(imported, updated, errors);
    }

    private async Task<ImportListResult> TerritoriesAsync(ListFile file, CancellationToken cancellationToken)
    {
        var (dealerIds, branchDealers) = await DealersAndBranchesAsync(cancellationToken).ConfigureAwait(false);
        var errors = new List<ImportRowError>();
        var imported = 0;
        var updated = 0;

        foreach (var batch in Batches(file.Rows))
        {
            var parsed = new List<Territory>();
            foreach (var row in batch)
            {
                try
                {
                    parsed.Add(ListRows.ReadTerritory(row, dealerIds, branchDealers));
                }
                catch (ImportRowException exception)
                {
                    errors.Add(new ImportRowError(row.Number, exception.Message));
                }
            }

            if (parsed.Count == 0)
            {
                continue;
            }

            await using var context = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            var ids = parsed.Select(territory => territory.Id).Distinct(StringComparer.Ordinal).ToList();
            var stored = await context.Territories
                .Where(territory => ids.Contains(territory.Id))
                .ToDictionaryAsync(territory => territory.Id, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

            foreach (var territory in parsed)
            {
                if (stored.TryGetValue(territory.Id, out var existing))
                {
                    existing.BranchId = territory.BranchId;
                    existing.Priority = territory.Priority;
                    updated++;
                }
                else
                {
                    context.Territories.Add(territory);
                    stored[territory.Id] = territory;
                    imported++;
                }
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return ImportListResult.From(imported, updated, errors);
    }

    private async Task<ImportListResult> SuppressionAsync(
        ListFile file,
        string? reason,
        string path,
        bool replace,
        CancellationToken cancellationToken)
    {
        if (reason is { Length: > 0 } given && !SuppressionReasons.IsKnown(given.Trim().ToLowerInvariant()))
        {
            throw new ImportListRequestException(
                $"'{reason}' is not a suppression reason.",
                $"Use one of {string.Join(", ", SuppressionReasons.All)}, or leave reason out and let "
                + "each row's own column decide.");
        }

        var source = Path.GetFullPath(path);
        var errors = new List<ImportRowError>();
        var imported = 0;
        var updated = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var batch in Batches(file.Rows))
        {
            var parsed = new List<SuppressionRow>();
            foreach (var row in batch)
            {
                try
                {
                    parsed.Add(ListRows.ReadSuppression(row, reason, source));
                }
                catch (ImportRowException exception)
                {
                    errors.Add(new ImportRowError(row.Number, exception.Message));
                }
            }

            if (parsed.Count == 0)
            {
                continue;
            }

            await using var context = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            var ids = parsed.Select(row => row.Id).Distinct(StringComparer.Ordinal).ToList();
            var stored = await context.Suppression
                .Where(row => ids.Contains(row.Id))
                .ToDictionaryAsync(row => row.Id, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in parsed)
            {
                seen.Add(row.Id);

                if (stored.TryGetValue(row.Id, out var existing))
                {
                    existing.CompanyName = row.CompanyName;
                    existing.Reason = row.Reason;
                    existing.SourceFile = row.SourceFile;
                    updated++;
                }
                else
                {
                    context.Suppression.Add(row);
                    stored[row.Id] = row;
                    imported++;
                }
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var removed = replace
            ? await RemoveMissingAsync(seen, cancellationToken).ConfigureAwait(false)
            : 0;

        return ImportListResult.From(imported, updated, errors, removed);
    }

    /// <summary>
    /// Deletes the suppression rows <paramref name="seen"/> does not name, for an import that was asked
    /// to make the file authoritative. Rows the file still names are matched by the id
    /// <see cref="DealerIds.Suppression"/> derives from the company they identify, so an unedited row
    /// is never deleted and re-added.
    /// </summary>
    private async Task<int> RemoveMissingAsync(IReadOnlySet<string> seen, CancellationToken cancellationToken)
    {
        List<string> stale;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var ids = await context.Suppression
                .AsNoTracking()
                .Select(row => row.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Diffed here rather than as a NOT IN over the whole file: a list of tens of thousands of
            // rows would otherwise build a parameter list past the provider's limit.
            stale = [.. ids.Where(id => !seen.Contains(id))];
        }

        var removed = 0;

        // Set-based deletes in §5.3's batch size. The leads those rows suppressed keep their
        // suppression_id until apply_suppression next runs, which is what releases them.
        for (var offset = 0; offset < stale.Count; offset += BatchSize)
        {
            var slice = stale.GetRange(offset, Math.Min(BatchSize, stale.Count - offset));

            await using var context = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            removed += await context.Suppression
                .Where(row => slice.Contains(row.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return removed;
    }

    private async Task<(IReadOnlySet<string> DealerIds, IReadOnlyDictionary<string, string> BranchDealers)>
        DealersAndBranchesAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var ids = await context.Dealers
            .AsNoTracking()
            .Select(dealer => dealer.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var branches = await context.DealerBranches
            .AsNoTracking()
            .Select(branch => new { branch.Id, branch.DealerId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (
            ids.ToHashSet(StringComparer.Ordinal),
            branches.ToDictionary(branch => branch.Id, branch => branch.DealerId, StringComparer.Ordinal));
    }

    private static IEnumerable<List<ListRow>> Batches(IReadOnlyList<ListRow> rows)
    {
        for (var offset = 0; offset < rows.Count; offset += BatchSize)
        {
            yield return [.. rows.Skip(offset).Take(BatchSize)];
        }
    }

    private static void Copy(Dealer from, Dealer to)
    {
        to.Name = from.Name;
        to.Website = from.Website;
        to.AlertEmail = from.AlertEmail;
        to.LogoFile = from.LogoFile;
    }

    private static void Copy(DealerBranch from, DealerBranch to)
    {
        to.DealerId = from.DealerId;
        to.Name = from.Name;
        to.Address = from.Address;
        to.City = from.City;
        to.State = from.State;
        to.Zip = from.Zip;
        to.Lat = from.Lat;
        to.Lon = from.Lon;
        to.Phone = from.Phone;
        to.TrackingPhone = from.TrackingPhone;
    }
}
