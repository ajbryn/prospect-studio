using ProspectStudio.Core.Geography;

namespace ProspectStudio.Core.Market;

/// <summary>
/// One data row of a Census CBP response, read <strong>by index</strong> out of the array-of-arrays
/// body (mcp-tools.md §estimate_market). Every value in that body is a string, and naming a variable in
/// <c>get=</c> as well as filtering on it duplicates its header column, so the column names cannot be
/// trusted to be unique.
/// </summary>
/// <param name="Naics">The value of the vintage's NAICS column (<c>NAICS2017</c> for 2023).</param>
/// <param name="CountyFips">
/// The five-digit GEOID: the response's trailing <c>state</c> and <c>county</c> columns concatenated,
/// which is the form <see cref="ResolvedGeography.CountyFips"/> uses.
/// </param>
/// <param name="SizeBandCode"><c>EMPSZES</c>: <c>001</c> for all establishments, else a size class.</param>
/// <param name="SizeBandLabel"><c>EMPSZES_LABEL</c>, the only published source of each band's bounds.</param>
/// <param name="Establishments"><c>ESTAB</c>, which is never empty, null or negative.</param>
public sealed record CbpEstablishmentRow(
    string Naics,
    string CountyFips,
    string SizeBandCode,
    string SizeBandLabel,
    int Establishments);

/// <summary>
/// An <c>EMPSZES</c> band with its lower bound parsed out of <c>EMPSZES_LABEL</c>. The 2023 metadata
/// publishes no value list for <c>EMPSZES</c> and the codes have moved between vintages, so the bands
/// are whatever a given response contains.
/// </summary>
/// <param name="MinEmployees">
/// The band's lower bound, or null for <see cref="CbpSizeBands.AllEstablishmentsCode"/>, which spans
/// every size and is therefore not comparable with a threshold.
/// </param>
/// <param name="MaxEmployees">
/// The band's upper bound, or null when the label says "or more". Both bounds are needed because some
/// bands are <em>nested inside</em> others: <c>262</c>, <c>263</c>, <c>271</c> and <c>273</c> subdivide
/// <c>260</c> ("1,000 employees or more") and sum to it exactly, so summing them alongside it
/// double-counts. Two bands can both be open-ended (<c>260</c> and <c>273</c>), so a missing upper bound
/// is infinity rather than a marker for "the top band".
/// </param>
public sealed record CbpSizeBand(string Code, string Label, int? MinEmployees, int? MaxEmployees = null)
{
    public bool IsAllEstablishments => Code == CbpSizeBands.AllEstablishmentsCode;
}

/// <summary>
/// Which bands a <c>minEmployees</c> threshold selects, and whether the threshold had to move.
/// </summary>
/// <param name="RequestedMinEmployees">What the caller asked for.</param>
/// <param name="EffectiveMinEmployees">
/// The band edge actually used. A threshold inside a band rounds <em>up</em> to the next edge, because
/// a band cannot be split and over-reporting the market is the worse error.
/// </param>
/// <param name="Codes">The selected band codes, lowest bound first. Never <c>001</c>.</param>
public sealed record SizeBandSelection(
    int RequestedMinEmployees,
    int EffectiveMinEmployees,
    IReadOnlyList<string> Codes)
{
    public bool RoundedUp => EffectiveMinEmployees != RequestedMinEmployees;
}

/// <summary>
/// The input of <c>estimate_market</c> after the geography has been resolved.
/// </summary>
/// <param name="Naics">
/// The requested codes. Overlapping codes are dropped before summing (<see cref="NaicsCodeSet"/>),
/// because the CBP table is hierarchical.
/// </param>
/// <param name="Geography">
/// A resolved scope. Sizing reads <see cref="ResolvedGeography.CountyFips"/> and never
/// <see cref="ResolvedGeography.Cbsa"/>, which is deliberately null for a multi-metro union.
/// </param>
public sealed record MarketEstimateRequest(
    IReadOnlyList<string> Naics,
    ResolvedGeography Geography,
    int? MinEmployees = null);

/// <summary>
/// The <c>geo</c> argument of <c>estimate_market</c>, which mcp-tools.md accepts in either of two forms:
/// a place to resolve (<c>{"query": "Houston metro"}</c>) or a <c>GeoScope</c> that
/// <c>resolve_geography</c> already returned, handed back unchanged.
/// </summary>
/// <param name="Label">A resolved scope's label, which becomes the estimate's <c>geoLabel</c>.</param>
/// <param name="CountyFips">
/// A resolved scope's counties. Present means the scope is used as given, because <c>countyFips</c> is
/// the only field CBP can be queried with: a multi-metro union has every county and a null
/// <see cref="ResolvedGeography.Cbsa"/>.
/// </param>
public sealed record MarketGeoInput(
    string? Query = null,
    string? Type = null,
    string[]? Values = null,
    GeoCenter? Center = null,
    double? RadiusMiles = null,
    string? Label = null,
    string[]? CountyFips = null)
{
    /// <summary>
    /// The scope as the caller gave it, or null when there is a place name to resolve instead.
    /// </summary>
    public ResolvedGeography? AsScope() =>
        CountyFips is { Length: > 0 } counties
            ? new ResolvedGeography(
                Type ?? GeoScopeTypes.Counties,
                Label ?? Query ?? $"{counties.Length} counties",
                Cbsa: null,
                States: [],
                CountyFips: [.. counties],
                Zips: [],
                Radius: null,
                Bbox: [])
            : null;

    public GeoResolveRequest AsResolveRequest() => new(Query, Type, Values, Center, RadiusMiles);
}

/// <param name="Establishments">The <c>001</c> total, which already includes every size band.</param>
/// <param name="WithMinEmployees">
/// The sum of the bands at or above the threshold, or null when no threshold was asked for. A lower
/// bound whenever any band row or county is missing, which the notes say.
/// </param>
public sealed record NaicsMarketEstimate(
    string Naics,
    string Title,
    int Establishments,
    int? WithMinEmployees);

public sealed record MarketTotals(int Establishments, int? WithMinEmployees);

/// <summary>What <c>estimate_market</c> returns (mcp-tools.md §estimate_market).</summary>
public sealed record MarketEstimate(
    int CbpYear,
    string GeoLabel,
    IReadOnlyList<NaicsMarketEstimate> ByNaics,
    MarketTotals Total,
    IReadOnlyList<string> Notes);

/// <summary>
/// Where establishment counts come from. Core defines it; <c>Infrastructure/Census/CensusCbpClient</c>
/// queries <c>api.census.gov</c> with a 30-day disk cache.
/// </summary>
public interface ICbpDataSource
{
    /// <summary>
    /// The CBP vintage to read: <c>PS_CBP_YEAR</c> when set, otherwise the newest year whose
    /// <c>…/cbp/variables.json</c> answers. Metadata is probed because a data query cannot be: with a
    /// key a missing year is a 404, but without one every year is a 302.
    /// </summary>
    Task<int> GetYearAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every published row for these NAICS codes in these counties, including the <c>001</c> rows, so
    /// the caller can tell a suppressed band from an empty one.
    /// </summary>
    /// <remarks>
    /// <paramref name="countyFips"/> may span states. Implementations must issue <strong>one request
    /// per state</strong>: naming two states in one <c>in=state:</c> is HTTP 400 "wildcard mismatch in
    /// geography hierarchy".
    /// </remarks>
    Task<IReadOnlyList<CbpEstablishmentRow>> GetEstablishmentsAsync(
        IReadOnlyList<string> naicsCodes,
        IReadOnlyList<string> countyFips,
        CancellationToken cancellationToken);
}
