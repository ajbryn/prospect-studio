namespace ProspectStudio.Core.Configuration;

public sealed record PsOptions
{
    public required string Home { get; init; }

    public required string Data { get; init; }

    public required string TrackingBaseUrl { get; init; }

    public string? OfferPrefix { get; init; }

    public required string UserAgent { get; init; }

    public required string OvertureRelease { get; init; }

    public int? CbpYear { get; init; }

    public required ApiKeys Keys { get; init; }

    public string LogsDirectory => Path.Combine(Data, "logs");

    public string CacheDirectory => Path.Combine(Data, "cache");

    public string ReferenceDataDirectory => Path.Combine(Data, "refdata");

    public string OvertureDirectory => Path.Combine(Data, "overture");

    public string DatabasePath => Path.Combine(Data, "prospect.db");
}

public sealed record ApiKeys
{
    public string? Census { get; init; }

    public string? OpenAi { get; init; }

    public string? Gemini { get; init; }

    public string? GoogleMaps { get; init; }

    public string? HubSpot { get; init; }

    public bool HasCensus => Present(Census);

    public bool HasOpenAi => Present(OpenAi);

    public bool HasGemini => Present(Gemini);

    public bool HasGoogleMaps => Present(GoogleMaps);

    public bool HasHubSpot => Present(HubSpot);

    // Records print their property values, and PsOptions prints this one; keys must never reach a log.
    public override string ToString() =>
        $"ApiKeys {{ census = {HasCensus}, openai = {HasOpenAi}, gemini = {HasGemini}, googleMaps = {HasGoogleMaps}, hubspot = {HasHubSpot} }}";

    private static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
}
