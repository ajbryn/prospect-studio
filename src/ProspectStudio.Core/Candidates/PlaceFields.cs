using System.Text.Json;
using ProspectStudio.Core.Json;

namespace ProspectStudio.Core.Candidates;

/// <summary>
/// Turning a raw Overture row into the <c>sites</c> columns of technical-design §5.2. Every one of
/// these is a place a plausible-looking shortcut goes wrong: <c>websites</c> and <c>phones</c> are
/// lists that are often empty, <c>addresses</c> is a struct list, the postcode is sometimes ZIP+4, and
/// a missing <c>freeform</c> must not take the locality and postcode with it.
/// </summary>
public static class PlaceFields
{
    /// <summary>How the whole hierarchy is kept in one <c>taxonomy_path</c> column, root first.</summary>
    public const char PathSeparator = '|';

    /// <summary>The first website, or <c>null</c> when the place lists none.</summary>
    public static string? FirstWebsite(OverturePlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        return First(place.Websites);
    }

    /// <summary>The first phone, or <c>null</c> when the place lists none.</summary>
    public static string? FirstPhone(OverturePlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        return First(place.Phones);
    }

    /// <summary>
    /// The first address, or <c>null</c> when the list is empty. Texas never has more than one, but
    /// the column is a list and an empty one is common.
    /// </summary>
    public static PlaceAddress? FirstAddress(OverturePlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        return place.Addresses is { Count: > 0 } addresses ? addresses[0] : null;
    }

    /// <summary>
    /// The first five digits of a postcode: Overture publishes ZIP+4 (<c>77064-3335</c>) on some rows
    /// and plain ZIP5 on others, and territory routing compares ZIP5.
    /// </summary>
    public static string? Zip5(string? postcode)
    {
        if (string.IsNullOrWhiteSpace(postcode))
        {
            return null;
        }

        var trimmed = postcode.Trim();
        return trimmed.Length <= 5 ? trimmed : trimmed[..5];
    }

    /// <summary>
    /// Everything above, as the <c>sites</c> row to store. <paramref name="release"/> is recorded so a
    /// site can be traced back to the extract it came from.
    /// </summary>
    public static CandidateSite ToSite(OverturePlace place, string release)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentException.ThrowIfNullOrWhiteSpace(release);

        var address = FirstAddress(place);
        var hierarchy = place.Taxonomy?.Hierarchy ?? [];

        return new CandidateSite(
            OvertureId: place.Id,
            Name: place.Name ?? string.Empty,
            NameNorm: NameNormalizer.Normalize(place.Name),
            Address: Trimmed(address?.Freeform),
            City: Trimmed(address?.Locality),
            State: Trimmed(address?.Region),
            Zip: Zip5(address?.Postcode),
            CountyFips: place.CountyFips,
            Lat: place.Lat,
            Lon: place.Lon,
            Phone: FirstPhone(place),
            Website: FirstWebsite(place),
            TaxonomyPrimary: Trimmed(place.Taxonomy?.Primary),
            TaxonomyPath: hierarchy.Count == 0 ? null : string.Join(PathSeparator, hierarchy),
            BasicCategory: Trimmed(place.BasicCategory),
            Confidence: place.Confidence,
            Release: release,
            PayloadJson: JsonSerializer.Serialize(place, ProspectStudioJson.Options));
    }

    private static string? First(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } && !string.IsNullOrWhiteSpace(values[0]) ? values[0] : null;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
