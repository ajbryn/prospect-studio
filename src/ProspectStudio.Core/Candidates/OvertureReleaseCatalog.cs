using System.Text.Json;

namespace ProspectStudio.Core.Candidates;

/// <summary>
/// Reading the current Overture release out of <c>https://stac.overturemaps.org/catalog.json</c>
/// (technical-design §6.1). Pure parsing, so the setup pipeline's choice of release is testable
/// against a recorded response.
/// </summary>
public static class OvertureReleaseCatalog
{
    public const string CatalogUrl = "https://stac.overturemaps.org/catalog.json";

    /// <summary>
    /// The value of the catalog's <c>latest</c> field, or <c>null</c> when the document has none.
    /// <strong>Only that field is authoritative.</strong> The <c>links</c> array is neither sorted nor
    /// complete - it mixes <c>root</c>, <c>self</c> and <c>child</c> entries in no particular order -
    /// so picking a release out of it would be guesswork.
    /// </summary>
    public static string? ReadLatest(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("latest", out var latest)
                && latest.ValueKind == JsonValueKind.String
                && latest.GetString() is { Length: > 0 } release
                    ? release
                    : null;
        }
        catch (JsonException)
        {
            // A catalog we cannot read is the same as one with no 'latest': the caller falls back to
            // the configured PS_OVERTURE_RELEASE and says so.
            return null;
        }
    }
}
