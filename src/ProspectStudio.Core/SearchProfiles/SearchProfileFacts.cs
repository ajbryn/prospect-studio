using System.Text.Json;

namespace ProspectStudio.Core.SearchProfiles;

/// <summary>
/// The few facts other parts of the POC need from a saved search profile. The profile itself stays an
/// opaque document: it is read here, never queried in the database.
/// </summary>
public static class SearchProfileFacts
{
    public static string Name(JsonElement profile) =>
        profile.ValueKind == JsonValueKind.Object
        && profile.TryGetProperty("name", out var name)
        && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? string.Empty
            : string.Empty;

    public static int SegmentCount(JsonElement profile) =>
        profile.ValueKind == JsonValueKind.Object
        && profile.TryGetProperty("segments", out var segments)
        && segments.ValueKind == JsonValueKind.Array
            ? segments.GetArrayLength()
            : 0;

    /// <summary>
    /// The raw geography text the user asked for. Turning it into a label needs
    /// <c>resolve_geography</c> (C2), so <c>get_campaign</c> exposes this until then.
    /// </summary>
    public static string? GeographyQuery(JsonElement profile) =>
        profile.ValueKind == JsonValueKind.Object
        && profile.TryGetProperty("geography", out var geography)
        && geography.ValueKind == JsonValueKind.Object
        && geography.TryGetProperty("query", out var query)
        && query.ValueKind == JsonValueKind.String
            ? query.GetString()
            : null;

    /// <summary>
    /// Advice worth passing back with a save: a segment with keywords but no Overture categories will
    /// only ever match on text, which finds fewer candidates (mcp-tools.md §save_search_profile).
    /// </summary>
    public static IReadOnlyList<string> Warnings(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object
            || !profile.TryGetProperty("segments", out var segments)
            || segments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var warnings = new List<string>();
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!HasItems(segment, "overtureCategories") && HasItems(segment, "keywords"))
            {
                var name = segment.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
                warnings.Add($"Segment '{name ?? "(unnamed)"}' has no overtureCategories; keywords only");
            }
        }

        return warnings;
    }

    private static bool HasItems(JsonElement segment, string propertyName) =>
        segment.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Array
        && value.GetArrayLength() > 0;
}
