using System.Text.Json;
using ProspectStudio.Core.Geography;

namespace ProspectStudio.Core.Candidates;

/// <summary>
/// What candidate search needs out of a saved search profile: the union of every segment's Overture
/// categories and keywords, the exclusion lists, the confidence floor and the geography. The profile
/// itself stays an opaque document - it is read here, never queried in the database (CLAUDE.md).
/// </summary>
/// <remarks>
/// <c>exclusions.overtureCategories</c> and <c>exclusions.keywords</c> are declared profile fields that
/// nothing consumed before C4; <c>find_candidates</c> is their only consumer (mcp-tools.md
/// §find_candidates).
/// </remarks>
public static class SearchProfileTargets
{
    /// <summary>The default confidence floor, which is also the schema's default.</summary>
    public const double DefaultMinConfidence = 0.6;

    public static IReadOnlyList<string> Categories(JsonElement profile) =>
        FromSegments(profile, "overtureCategories");

    public static IReadOnlyList<string> Keywords(JsonElement profile) =>
        FromSegments(profile, "keywords");

    public static IReadOnlyList<string> ExcludedCategories(JsonElement profile) =>
        FromExclusions(profile, "overtureCategories");

    public static IReadOnlyList<string> ExcludedKeywords(JsonElement profile) =>
        FromExclusions(profile, "keywords");

    public static double? MinConfidence(JsonElement profile) =>
        profile.ValueKind == JsonValueKind.Object
        && profile.TryGetProperty("minConfidence", out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    /// <summary>
    /// The profile's geography as a <c>resolve_geography</c> request: its free-text <c>query</c>, or the
    /// counties of an already-resolved <c>scope</c>. Null when the profile names no geography.
    /// </summary>
    public static GeoResolveRequest? Geography(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object
            || !profile.TryGetProperty("geography", out var geography)
            || geography.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (geography.TryGetProperty("query", out var query)
            && query.ValueKind == JsonValueKind.String
            && query.GetString() is { Length: > 0 } text)
        {
            return new GeoResolveRequest(Query: text);
        }

        if (!geography.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var counties = Values(scope, "countyFips");
        if (counties.Count > 0)
        {
            return new GeoResolveRequest(Type: GeoScopeTypes.Counties, Values: counties);
        }

        var zips = Values(scope, "zips");
        if (zips.Count > 0)
        {
            return new GeoResolveRequest(Type: GeoScopeTypes.Zips, Values: zips);
        }

        var states = Values(scope, "states");
        return states.Count > 0 ? new GeoResolveRequest(Type: GeoScopeTypes.State, Values: states) : null;
    }

    private static IReadOnlyList<string> FromSegments(JsonElement profile, string property)
    {
        if (profile.ValueKind != JsonValueKind.Object
            || !profile.TryGetProperty("segments", out var segments)
            || segments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return Distinct(segments.EnumerateArray()
            .Where(segment => segment.ValueKind == JsonValueKind.Object)
            .SelectMany(segment => Values(segment, property)));
    }

    private static IReadOnlyList<string> FromExclusions(JsonElement profile, string property) =>
        profile.ValueKind == JsonValueKind.Object
        && profile.TryGetProperty("exclusions", out var exclusions)
        && exclusions.ValueKind == JsonValueKind.Object
            ? Values(exclusions, property)
            : [];

    private static IReadOnlyList<string> Values(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? Distinct(value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .OfType<string>())
            : [];

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values) =>
    [
        .. values
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];
}
