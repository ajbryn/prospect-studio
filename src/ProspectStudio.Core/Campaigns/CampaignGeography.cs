using System.Text.Json;
using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Json;

namespace ProspectStudio.Core.Campaigns;

/// <summary>
/// The campaign's stored <c>geo_json</c>: the scope <c>find_candidates</c> last searched
/// (technical-design §5.2). <c>get_campaign.geoLabel</c> is read from it, which is why it stays null
/// until C4 writes it for the first time (mcp-tools.md §get_campaign).
/// </summary>
public static class CampaignGeography
{
    public static string Serialize(ResolvedGeography geography)
    {
        ArgumentNullException.ThrowIfNull(geography);
        return JsonSerializer.Serialize(geography, ProspectStudioJson.Options);
    }

    /// <summary>The scope's label, or null when the campaign has no stored scope.</summary>
    public static string? Label(string? geoJson)
    {
        if (string.IsNullOrWhiteSpace(geoJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(geoJson);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("label", out var label)
                && label.ValueKind == JsonValueKind.String
                && label.GetString() is { Length: > 0 } text
                    ? text
                    : null;
        }
        catch (JsonException)
        {
            // An unreadable geo_json is a display-only field; reporting no label beats failing the read.
            return null;
        }
    }
}
