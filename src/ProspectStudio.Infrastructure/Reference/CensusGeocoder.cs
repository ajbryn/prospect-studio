using System.Text.Json;
using ProspectStudio.Core.Geography;

namespace ProspectStudio.Infrastructure.Reference;

/// <summary>
/// The Census one-line-address geocoder, which is what turns <c>center.address</c> into a point
/// (technical-design §6.2). <c>benchmark</c> is required, and in <c>coordinates</c> <c>x</c> is the
/// longitude and <c>y</c> the latitude - easy to invert.
/// </summary>
/// <remarks>
/// The response is treated strictly as data: two numbers and a label that is trimmed and length-capped
/// before it reaches a <c>GeoScope</c>, never anything that could read as an instruction.
/// </remarks>
public sealed class CensusGeocoder(ProspectStudioHttpClient http) : IAddressGeocoder
{
    public const string Endpoint = "https://geocoding.geo.census.gov/geocoder/locations/onelineaddress";

    public const string Benchmark = "Public_AR_Current";

    /// <summary>Long enough for a full US address, short enough that a label stays a label.</summary>
    private const int MaxLabelLength = 120;

    public async Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var url = $"{Endpoint}?address={Uri.EscapeDataString(address)}&benchmark={Benchmark}&format=json";

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProspectStudioHttpClient.ResponseTimeout);

        string body;
        try
        {
            using var response = await http.Client.GetAsync(url, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new GeographyExternalException(
                    $"The Census geocoder answered {(int)response.StatusCode}.");
            }

            body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new GeographyExternalException("The Census geocoder could not be reached.");
        }

        return Read(body, address);
    }

    private static GeocodedAddress? Read(string body, string address)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("addressMatches", out var matches)
                || matches.ValueKind != JsonValueKind.Array
                || matches.GetArrayLength() == 0)
            {
                // No match is an answer, not a failure: the tool turns it into NOT_FOUND.
                return null;
            }

            var first = matches[0];
            if (!first.TryGetProperty("coordinates", out var coordinates)
                || !coordinates.TryGetProperty("x", out var longitude)
                || !coordinates.TryGetProperty("y", out var latitude))
            {
                return null;
            }

            var label = first.TryGetProperty("matchedAddress", out var matched) ? matched.GetString() : null;

            return new GeocodedAddress(Label(label, address), latitude.GetDouble(), longitude.GetDouble());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new GeographyExternalException("The Census geocoder returned a response we could not read.");
        }
    }

    private static string Label(string? matched, string address)
    {
        var text = string.IsNullOrWhiteSpace(matched) ? address : matched;
        var cleaned = new string([.. text.Where(character => !char.IsControl(character))]).Trim();

        return cleaned.Length <= MaxLabelLength ? cleaned : cleaned[..MaxLabelLength];
    }
}
