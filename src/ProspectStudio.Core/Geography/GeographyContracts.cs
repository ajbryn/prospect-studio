using System.Text.Json.Serialization;

namespace ProspectStudio.Core.Geography;

/// <summary>The <c>type</c> enum of <c>$defs.geoScope</c> in <c>schemas/search-profile.schema.json</c>.</summary>
/// <remarks>It is <c>counties</c>, plural; there is no singular <c>county</c>.</remarks>
public static class GeoScopeTypes
{
    public const string State = "state";
    public const string Counties = "counties";
    public const string Cbsa = "cbsa";
    public const string Zips = "zips";
    public const string Radius = "radius";

    /// <summary>A dealer's territory. Arrives in chunk C5 (mcp-tools.md §resolve_geography).</summary>
    public const string Dealer = "dealer";

    public static bool IsKnown(string type) =>
        type is State or Counties or Cbsa or Zips or Radius or Dealer;
}

/// <summary>The circle of a <c>radius</c> scope, exactly as <c>$defs.geoScope</c> defines it.</summary>
public sealed record GeoRadius(double Lat, double Lon, double Miles);

/// <summary>One of several places a query could have meant (mcp-tools.md §resolve_geography).</summary>
public sealed record GeoAlternative(string Type, string Label, string? Cbsa, IReadOnlyList<string> CountyFips);

/// <summary>
/// What <c>resolve_geography</c> returns: a flat <c>GeoScope</c> with <see cref="Alternatives"/> as a
/// sibling key. <strong>Every <c>GeoScope</c> key is always present</strong>, using <c>[]</c> or
/// <c>null</c> where it does not apply, so a caller never has to tell "absent" from "empty";
/// <see cref="Alternatives"/> is the one exception and is omitted when there are none.
/// </summary>
/// <param name="Bbox"><c>[minLon, minLat, maxLon, maxLat]</c>, empty when no geometry is known.</param>
/// <param name="Warnings">
/// Part of the input that resolved to nothing - ZIPs with no ZCTA, say. Omitted when empty, like
/// <paramref name="Alternatives"/>. It exists so a mostly-good list is neither silently trimmed nor
/// rejected whole over one typo.
/// </param>
public sealed record ResolvedGeography(
    string Type,
    string Label,
    string? Cbsa,
    IReadOnlyList<string> States,
    IReadOnlyList<string> CountyFips,
    IReadOnlyList<string> Zips,
    GeoRadius? Radius,
    IReadOnlyList<double> Bbox,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GeoAlternative>? Alternatives = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Warnings = null);

/// <summary>The centre of a <c>radius</c> request: a point, or an address for the Census geocoder.</summary>
public sealed record GeoCenter(double? Lat = null, double? Lon = null, string? Address = null);

/// <summary>
/// The five input forms of <c>resolve_geography</c>. Every field is optional on its own; one of the
/// forms has to be present.
/// </summary>
public sealed record GeoResolveRequest(
    string? Query = null,
    string? Type = null,
    IReadOnlyList<string>? Values = null,
    GeoCenter? Center = null,
    double? RadiusMiles = null);

/// <summary>One county from <c>counties.parquet</c>, without its geometry.</summary>
public sealed record CountyRecord(string Fips, string Name, string StateFips);

/// <summary>One row of <c>cbsa.csv</c>: a county inside a CBSA (technical-design §6.1).</summary>
public sealed record CbsaCountyRecord(
    string CbsaCode,
    string CbsaTitle,
    string AreaType,
    string CountyFips,
    string CountyName,
    string StateName,
    string CentralOutlying);

/// <summary>One row of <c>zcta_county.csv</c>. A ZCTA can span several counties, so ZIPs repeat.</summary>
public sealed record ZctaCountyRecord(string Zcta5, string CountyFips);

/// <summary>A bounding box in degrees, in the <c>[minLon, minLat, maxLon, maxLat]</c> order of §6.2.</summary>
public sealed record GeoBounds(double MinLon, double MinLat, double MaxLon, double MaxLat)
{
    public IReadOnlyList<double> ToArray() => [MinLon, MinLat, MaxLon, MaxLat];
}

public sealed record GeoPoint(double Lat, double Lon);

/// <summary>
/// The prepared reference data as geography resolution needs it. Core defines it; Infrastructure reads
/// <c>counties.parquet</c>, <c>cbsa.csv</c> and <c>zcta_county.csv</c> out of <c>refdata\</c>.
/// </summary>
public interface IGeographyReference
{
    /// <summary>False until <c>prepare_data</c> has written every reference file, which is <c>NOT_READY</c>.</summary>
    bool IsReady { get; }

    Task<IReadOnlyList<CountyRecord>> GetCountiesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CbsaCountyRecord>> GetCbsaCountiesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ZctaCountyRecord>> GetZctaCountiesAsync(CancellationToken cancellationToken);

    /// <summary>The box around these counties' geometry, or null when none of them has any.</summary>
    Task<GeoBounds?> GetBoundsAsync(IReadOnlyCollection<string> countyFips, CancellationToken cancellationToken);

    /// <summary>The FIPS of every county whose geometry meets the closed ring <paramref name="boundary"/>.</summary>
    Task<IReadOnlyList<string>> FindCountiesIntersectingAsync(
        IReadOnlyList<GeoPoint> boundary,
        CancellationToken cancellationToken);
}

/// <param name="MatchedAddress">What the geocoder matched, which is a better label than the input.</param>
public sealed record GeocodedAddress(string MatchedAddress, double Lat, double Lon);

/// <summary>
/// Turns a one-line address into a point. The POC uses the Census geocoder, whose
/// <c>coordinates</c> are <c>x</c> for longitude and <c>y</c> for latitude.
/// </summary>
public interface IAddressGeocoder
{
    /// <summary>Null when the geocoder matched nothing, which is a <c>NOT_FOUND</c> rather than a failure.</summary>
    Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken cancellationToken);
}
