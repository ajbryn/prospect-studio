using System.Globalization;
using System.Text;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Domain;

namespace ProspectStudio.Core.Dealers;

/// <summary>
/// One row of a list file, as <c>import_list</c> sees it.
/// </summary>
/// <param name="Number">
/// The <strong>spreadsheet</strong> row: the header is row 1, so the first data row is row 2. It is
/// the number the user sees when they open the file, which is the only number an error message can
/// usefully name (mcp-tools.md §import_list).
/// </param>
public sealed record ListRow(int Number, IReadOnlyDictionary<string, string> Cells)
{
    /// <summary>The trimmed cell, or the empty string when the column is absent or blank.</summary>
    public string Text(string column) =>
        Cells.TryGetValue(column, out var value) ? value.Trim() : string.Empty;
}

/// <summary>
/// Turning a list row into the rows technical-design §5.2 stores. Pure: the file reading and the
/// database writes are Infrastructure's, so these rules can be read and tested on their own.
/// </summary>
public static class ListRows
{
    /// <summary>The headers of <c>poc/fixtures/dealers.csv</c> that must be present.</summary>
    public static IReadOnlyList<string> DealerColumns { get; } = ["dealer_id", "dealer_name"];

    /// <summary>The headers of <c>poc/fixtures/territories.csv</c> that must be present.</summary>
    public static IReadOnlyList<string> TerritoryColumns { get; } = ["dealer_id", "level", "code"];

    /// <summary>The headers of <c>poc/fixtures/suppression.csv</c> that must be present.</summary>
    public static IReadOnlyList<string> SuppressionColumns { get; } = ["company_name"];

    /// <summary>The required headers for one <see cref="ImportListKinds"/> value.</summary>
    public static IReadOnlyList<string> ColumnsFor(string kind) => kind switch
    {
        ImportListKinds.Dealers => DealerColumns,
        ImportListKinds.Territories => TerritoryColumns,
        ImportListKinds.Suppression => SuppressionColumns,
        _ => [],
    };

    public static Dealer ReadDealer(ListRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new Dealer
        {
            Id = Key(row, "dealer_id"),
            Name = Required(row, "dealer_name"),
            Website = Optional(row, "website"),
            AlertEmail = Optional(row, "alert_email"),
            LogoFile = Optional(row, "logo_file"),
        };
    }

    /// <summary>The row's branch, or null when it names none - a dealer may be listed without one.</summary>
    public static DealerBranch? ReadBranch(ListRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var branchId = row.Text("branch_id");
        if (branchId.Length == 0)
        {
            return null;
        }

        return new DealerBranch
        {
            Id = branchId.ToLowerInvariant(),
            DealerId = Key(row, "dealer_id"),
            Name = row.Text("branch_name") is { Length: > 0 } name ? name : branchId,
            Address = Optional(row, "address"),
            City = Optional(row, "city"),
            State = Optional(row, "state")?.ToUpperInvariant(),
            Zip = Zip5(row.Text("zip")),
            Lat = Coordinate(row, "lat", 90),
            Lon = Coordinate(row, "lon", 180),
            Phone = Optional(row, "phone"),
            TrackingPhone = Optional(row, "tracking_phone"),
        };
    }

    /// <param name="branchDealers">
    /// Every known branch id and the dealer it belongs to. A territory row naming a branch that does
    /// not exist is a row error rather than something to store: §7.4 would silently lose its
    /// nearest-branch tie-break on that rule, and C11's dealer packets have nowhere to send the lead.
    /// </param>
    public static Territory ReadTerritory(
        ListRow row,
        IReadOnlySet<string> dealerIds,
        IReadOnlyDictionary<string, string> branchDealers)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(dealerIds);
        ArgumentNullException.ThrowIfNull(branchDealers);

        var dealerId = Key(row, "dealer_id");
        if (!dealerIds.Contains(dealerId))
        {
            throw new ImportRowException(
                $"Unknown dealerId '{row.Text("dealer_id")}'. Import dealers.csv first, or correct the id.");
        }

        var level = row.Text("level").ToLowerInvariant();
        if (!TerritoryLevels.IsKnown(level))
        {
            throw new ImportRowException(
                $"Level '{row.Text("level")}' is not '{TerritoryLevels.Zip}' or '{TerritoryLevels.County}'.");
        }

        var code = row.Text("code");
        if (code.Length != 5 || !code.All(char.IsAsciiDigit))
        {
            throw new ImportRowException(
                $"Code '{code}' is not five digits. A '{TerritoryLevels.Zip}' row takes a ZIP5 and a "
                + $"'{TerritoryLevels.County}' row a county FIPS; a spreadsheet that stored it as a number "
                + "may have dropped a leading zero.");
        }

        // Lower-cased before it is hashed as well as before it is stored, or the same rule typed
        // 'Bay-Pas' would hash to a second id and import as a duplicate territory row.
        var branchId = row.Text("branch_id").ToLowerInvariant();
        if (branchId.Length > 0)
        {
            if (!branchDealers.TryGetValue(branchId, out var owner))
            {
                throw new ImportRowException(
                    $"Unknown branchId '{row.Text("branch_id")}'. Import dealers.csv first, or leave the "
                    + "column blank to route to the dealer without naming a branch.");
            }

            if (!string.Equals(owner, dealerId, StringComparison.Ordinal))
            {
                throw new ImportRowException(
                    $"Branch '{row.Text("branch_id")}' belongs to dealer '{owner}', not "
                    + $"'{row.Text("dealer_id")}'.");
            }
        }

        return new Territory
        {
            Id = DealerIds.Territory(dealerId, branchId, level, code),
            DealerId = dealerId,
            BranchId = branchId.Length == 0 ? null : branchId,
            Level = level,
            Code = code,
            Priority = Priority(row),
        };
    }

    /// <param name="defaultReason">
    /// The <c>reason</c> <c>import_list</c> was called with, used when the row's own column is blank
    /// (mcp-tools.md §import_list).
    /// </param>
    public static SuppressionRow ReadSuppression(ListRow row, string? defaultReason, string sourceFile)
    {
        ArgumentNullException.ThrowIfNull(row);

        var companyName = Required(row, "company_name");
        var nameNorm = NameNormalizer.Normalize(companyName);
        if (nameNorm.Length == 0)
        {
            throw new ImportRowException(
                $"'{companyName}' normalizes to nothing, so it could never match a lead (§7.1).");
        }

        var reason = Reason(row, defaultReason);
        var domain = DomainKey.For(row.Text("domain"));
        var addressNorm = Flatten(row.Text("address"));
        var zip = Zip5(row.Text("zip"));

        return new SuppressionRow
        {
            Id = DealerIds.Suppression(nameNorm, domain, addressNorm, zip),
            CompanyName = companyName,
            NameNorm = nameNorm,
            Domain = domain,
            AddressNorm = addressNorm,
            Zip = zip,
            Reason = reason,
            SourceFile = sourceFile,
        };
    }

    /// <summary>Five digits: a ZIP+4 is cut back, and a blank cell is no ZIP at all.</summary>
    public static string? Zip5(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? null : trimmed.Length > 5 ? trimmed[..5] : trimmed;
    }

    /// <summary>
    /// A plain lowercase, punctuation-free form of an address. It is deliberately not an address
    /// algorithm: §7.9's warranty matchback defines that in C13, and this only has to give
    /// <c>address_norm</c> a stable value in the meantime - §7.3 never matches on it.
    /// </summary>
    private static string? Flatten(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (char.IsWhiteSpace(character) && builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        var flattened = builder.ToString().TrimEnd();
        return flattened.Length == 0 ? null : flattened;
    }

    private static string Reason(ListRow row, string? defaultReason)
    {
        var reason = row.Text("reason");
        if (reason.Length == 0)
        {
            reason = defaultReason?.Trim() ?? string.Empty;
        }

        if (reason.Length == 0)
        {
            return SuppressionReasons.Other;
        }

        reason = reason.ToLowerInvariant();
        return SuppressionReasons.IsKnown(reason)
            ? reason
            : throw new ImportRowException(
                $"Reason '{reason}' is not one of {string.Join(", ", SuppressionReasons.All)}.");
    }

    private static int Priority(ListRow row)
    {
        var value = row.Text("priority");
        if (value.Length == 0)
        {
            return 1;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority)
            ? priority
            : throw new ImportRowException($"Priority '{value}' is not a whole number.");
    }

    private static double Coordinate(ListRow row, string column, double limit)
    {
        var value = row.Text(column);
        if (value.Length == 0)
        {
            throw new ImportRowException(
                $"Column '{column}' is empty, so the branch has no location and §7.4's nearest-branch "
                + "tie-break could not run.");
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed)
            || Math.Abs(parsed) > limit)
        {
            throw new ImportRowException($"Column '{column}' is '{value}', which is not a coordinate.");
        }

        return parsed;
    }

    /// <summary>A machine key (a dealer or branch id), lower-cased so matching never relies on the provider's text comparison.</summary>
    private static string Key(ListRow row, string column) => Required(row, column).ToLowerInvariant();

    private static string Required(ListRow row, string column) =>
        row.Text(column) is { Length: > 0 } value
            ? value
            : throw new ImportRowException($"Column '{column}' is required and is empty.");

    private static string? Optional(ListRow row, string column) =>
        row.Text(column) is { Length: > 0 } value ? value : null;
}
