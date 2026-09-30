using System.Text.Json.Serialization;

namespace ProspectStudio.Core.Status;

public sealed record StatusReport(
    string Version,
    string Home,
    string Data,
    ReadinessReport Ready,
    ConfiguredKeys Keys,
    string TrackingBaseUrl,
    IReadOnlyList<string> Warnings);

public sealed record ReadinessReport(
    bool ReferenceData,
    OvertureReadiness Overture,
    bool BrandKit,
    int Dealers,
    int Territories,
    int Suppression);

public sealed record OvertureReadiness(string? Release, IReadOnlyList<string> States);

public sealed record ConfiguredKeys(
    [property: JsonPropertyName("census")] bool Census,
    [property: JsonPropertyName("openai")] bool OpenAi,
    [property: JsonPropertyName("gemini")] bool Gemini,
    [property: JsonPropertyName("googleMaps")] bool GoogleMaps,
    [property: JsonPropertyName("hubspot")] bool HubSpot);
