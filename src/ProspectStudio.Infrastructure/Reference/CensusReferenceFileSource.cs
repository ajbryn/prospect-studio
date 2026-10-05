using System.Net;
using ProspectStudio.Core.Reference;

namespace ProspectStudio.Infrastructure.Reference;

/// <summary>
/// Downloads the raw Census files the setup pipeline parses (technical-design §6.1) and caches them, so
/// re-running a single step does not fetch 12 MB again. Verified URLs; the county vintage is probed
/// downward from the current year because the newest year is published mid-year.
/// </summary>
public sealed class CensusReferenceFileSource : IReferenceFileSource, IDisposable
{
    public const string CbsaUrl =
        "https://www2.census.gov/programs-surveys/metro-micro/geographies/reference-files/2023/delineation-files/list1_2023.xlsx";

    public const string ZctaCountyUrl =
        "https://www2.census.gov/geo/docs/maps-data/data/rel2020/zcta520/tab20_zcta520_county20_natl.txt";

    /// <summary>The oldest county vintage worth probing down to.</summary>
    private const int OldestCountyVintage = 2022;

    private const int MaxAttempts = 3;

    /// <summary>CLAUDE.md §Web fetching: at most one request a second to a domain.</summary>
    private static readonly TimeSpan _minimumGap = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public CensusReferenceFileSource(ProspectStudioHttpClient http, string cacheDirectory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _http = http.Client;
        _cacheDirectory = cacheDirectory;
        _time = timeProvider;
    }

    public void Dispose() => _gate.Dispose();

    public static string CountyUrl(int year) =>
        $"https://www2.census.gov/geo/tiger/GENZ{year}/shp/cb_{year}_us_county_500k.zip";

    public async Task<ReferenceFile> GetAsync(string step, CancellationToken cancellationToken)
    {
        var url = step switch
        {
            ReferenceSteps.Counties => await LatestCountyUrlAsync(cancellationToken).ConfigureAwait(false),
            ReferenceSteps.Cbsa => new Uri(CbsaUrl),
            ReferenceSteps.Zcta => new Uri(ZctaCountyUrl),
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Not a reference-data step."),
        };

        return await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Uri> LatestCountyUrlAsync(CancellationToken cancellationToken)
    {
        var newest = _time.GetUtcNow().Year;

        for (var year = newest; year >= OldestCountyVintage; year--)
        {
            var url = new Uri(CountyUrl(year));
            if (Cached(url) is not null || await ExistsAsync(url, cancellationToken).ConfigureAwait(false))
            {
                return url;
            }
        }

        throw new InvalidOperationException(
            $"No county boundary file between {OldestCountyVintage} and {newest} is published at "
            + "https://www2.census.gov/geo/tiger/.");
    }

    private async Task<bool> ExistsAsync(Uri url, CancellationToken cancellationToken)
    {
        await ThrottleAsync(cancellationToken).ConfigureAwait(false);

        using var deadline = Deadline(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Head, url);

        try
        {
            using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<ReferenceFile> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDirectory);

        if (Cached(url) is { } cached)
        {
            return new ReferenceFile(cached, url, File.GetLastWriteTimeUtc(cached));
        }

        var path = CachePath(url);
        var partial = path + ".part";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await FetchAsync(url, partial, cancellationToken).ConfigureAwait(false);
                File.Move(partial, path, overwrite: true);
                return new ReferenceFile(path, url, _time.GetUtcNow());
            }
            catch (Exception exception) when (IsTransient(exception)
                && attempt < MaxAttempts
                && !cancellationToken.IsCancellationRequested)
            {
                TryDelete(partial);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FetchAsync(Uri url, string partial, CancellationToken cancellationToken)
    {
        await ThrottleAsync(cancellationToken).ConfigureAwait(false);

        // The headers get the 10-second deadline; the body then streams under the client's own limit.
        using var deadline = Deadline(cancellationToken);
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(partial);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private string? Cached(Uri url)
    {
        var path = CachePath(url);
        return new FileInfo(path) is { Exists: true, Length: > 0 } ? path : null;
    }

    private string CachePath(Uri url) =>
        Path.Combine(_cacheDirectory, Path.GetFileName(url.LocalPath));

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _lastRequest + _minimumGap - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }

            _lastRequest = _time.GetUtcNow();
        }
        finally
        {
            _gate.Release();
        }
    }

    private CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProspectStudioHttpClient.ResponseTimeout);
        return deadline;
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout,
        HttpRequestException => true,
        IOException => true,
        OperationCanceledException => true,
        _ => false,
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover .part file is overwritten by the next attempt.
        }
    }
}
