namespace ProspectStudio.Core.Domain;

/// <summary>
/// A dealer: the local business a lead is routed to (technical-design §5.2). Persistence-ignorant -
/// the mapping lives in an <c>IEntityTypeConfiguration&lt;Dealer&gt;</c> in Infrastructure/Storage.
/// </summary>
public sealed class Dealer
{
    /// <summary>The short id the lists use (<c>gulf</c>, <c>bay</c>, <c>pine</c>).</summary>
    public required string Id { get; set; }

    public required string Name { get; set; }

    public string? Website { get; set; }

    /// <summary>Where new leads for this dealer are announced.</summary>
    public string? AlertEmail { get; set; }

    /// <summary>A co-branding logo in the workspace, relative to the Brand Kit folder.</summary>
    public string? LogoFile { get; set; }
}

/// <summary>
/// One of a dealer's branches (technical-design §5.2). The coordinates are what §7.4's nearest-branch
/// tie-break measures against.
/// </summary>
public sealed class DealerBranch
{
    public required string Id { get; set; }

    public required string DealerId { get; set; }

    public required string Name { get; set; }

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Zip { get; set; }

    public double Lat { get; set; }

    public double Lon { get; set; }

    public string? Phone { get; set; }

    /// <summary>A per-dealer tracking number printed on the postcard, when there is one.</summary>
    public string? TrackingPhone { get; set; }
}

/// <summary>
/// One territory rule (technical-design §5.2, §7.4): a ZIP5 or a county FIPS that routes to a branch.
/// </summary>
public sealed class Territory
{
    public required string Id { get; set; }

    public required string DealerId { get; set; }

    /// <summary>The branch that serves this code; null routes to the dealer without naming a branch.</summary>
    public string? BranchId { get; set; }

    /// <summary>One of <see cref="Dealers.TerritoryLevels"/>: <c>zip</c> or <c>county</c>.</summary>
    public required string Level { get; set; }

    /// <summary>A ZIP5 when <see cref="Level"/> is <c>zip</c>, a county FIPS when it is <c>county</c>.</summary>
    public required string Code { get; set; }

    /// <summary>Lower wins a tie (§7.4). <c>territories.csv</c> gives Harris to <c>gulf</c> at 2.</summary>
    public int Priority { get; set; }
}

/// <summary>
/// One suppression row (technical-design §5.2, §7.3): a company that must never be mailed, whatever
/// the reason. Dealers and competitors are reasons like any other.
/// </summary>
public sealed class SuppressionRow
{
    public required string Id { get; set; }

    public required string CompanyName { get; set; }

    /// <summary>Normalized name (§7.1). §7.3's exact and fuzzy rules both compare this column.</summary>
    public required string NameNorm { get; set; }

    /// <summary>Registrable domain (§7.2's rule), or null when the row has no website.</summary>
    public string? Domain { get; set; }

    /// <summary>Normalized address, for the warranty matchback in C13; §7.3 does not match on it.</summary>
    public string? AddressNorm { get; set; }

    /// <summary>ZIP5. §7.3's name rules require it to match when the row has one.</summary>
    public string? Zip { get; set; }

    /// <summary>One of <see cref="Dealers.SuppressionReasons"/>.</summary>
    public required string Reason { get; set; }

    /// <summary>The file the row was imported from, so a bad list can be traced back.</summary>
    public string? SourceFile { get; set; }
}
