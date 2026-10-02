using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Market;

namespace ProspectStudio.Infrastructure.Census;

/// <summary>
/// Reads County Business Patterns from <c>api.census.gov</c> (technical-design §4,
/// implementation-plan C3), caching every response on disk under
/// <see cref="PsOptions.CacheDirectory"/> for <see cref="CacheLifetime"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The handler must set <c>AllowAutoRedirect = false</c>.</strong> Every data query without a
/// usable key answers 302 to <c>missing_key.html</c> or <c>invalid_key.html</c>; followed, that lands on
/// a 200 HTML page and the key problem surfaces as an inexplicable JSON parse error.
/// </para>
/// <para>
/// One request per state: <c>for=county:201,001&amp;in=state:48,22</c> is HTTP 400 "wildcard mismatch in
/// geography hierarchy".
/// </para>
/// <para>
/// Requests are serial and spaced one second apart, so the whole call is bounded two ways —
/// <see cref="MaxRequestsPerCall"/> refuses a query that would need too many round trips, and
/// <see cref="CallBudget"/> caps the elapsed time — because <c>estimate_market</c> is a tool, not a
/// background job (CLAUDE.md: a tool that can take more than about 20 s returns a <c>jobId</c> instead).
/// The resolved vintage is cached on disk for the same reason: probing for it costs four requests, and
/// the published CBP year changes once a year.
/// </para>
/// </remarks>
/// <param name="options">Reads <c>CENSUS_API_KEY</c>, <c>PS_CBP_YEAR</c>, the user agent and the cache folder.</param>
/// <param name="clock">Ages cache entries and spaces requests. Injected so a test can step past the 30 days.</param>
/// <param name="handler">
/// The transport. Tests inject a handler that answers from the recorded bodies in
/// <c>src/tests/Fixtures/cbp</c>; production passes null and the client builds its own with redirects
/// disabled.
/// </param>
/// <param name="requestGap">
/// The minimum spacing between requests to the host (CLAUDE.md §Web fetching: 1 request/second per
/// domain), which is <see cref="DefaultRequestGap"/> for the real transport. Pass it explicitly to put a
/// recorded-handler test on the production spacing, or <see cref="TimeSpan.Zero"/> to turn it off; a test
/// that answers from fixtures reaches no host, so that is the default there.
/// </param>
public sealed class CensusCbpClient(
    PsOptions options,
    TimeProvider clock,
    HttpMessageHandler? handler = null,
    TimeSpan? requestGap = null)
    : ICbpDataSource, IDisposable
{
    /// <summary>
    /// How long a cached response stays good (implementation-plan C3: "disk cache for 30 days"). CBP is
    /// an annual release, so a month is conservative.
    /// </summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// The deadline for a response's <em>headers</em> (CLAUDE.md §Web fetching, as C2 read it): the body
    /// then streams inside <see cref="_transferTimeout"/>.
    /// </summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>One request per second per host (CLAUDE.md §Web fetching), as C2 applies it.</summary>
    public static readonly TimeSpan DefaultRequestGap = TimeSpan.FromSeconds(1);

    /// <summary>How long one call may spend on Census altogether, retries and spacing included.</summary>
    public static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How many Census requests one call may need: NAICS codes × states, since there is one request per
    /// pair, they run one after another and each is a second behind the last. Ten leaves real headroom
    /// inside <see cref="CallBudget"/> once the vintage is cached.
    /// </summary>
    public const int MaxRequestsPerCall = 10;

    public const string ApiBaseUrl = "https://api.census.gov/data";

    /// <summary>The columns every query reads: the count, the band, and the band's only published bounds.</summary>
    private const string Columns = "ESTAB,EMPSZES,EMPSZES_LABEL";

    /// <summary>How far down the year probe walks before it reports that nothing is published.</summary>
    private const int ProbedYears = 15;

    private const int MaxAttempts = 3;

    private static readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan _transferTimeout = TimeSpan.FromMinutes(1);

    private readonly PsOptions _options = options;
    private readonly TimeProvider _clock = clock;
    private readonly HttpClient _http = CreateHttpClient(options, handler);
    private readonly TimeSpan _requestGap = requestGap ?? (handler is null ? DefaultRequestGap : TimeSpan.Zero);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _vintageGate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private CbpVintage? _vintage;

    public Task<int> GetYearAsync(CancellationToken cancellationToken) =>
        WithinBudgetAsync(
            async budget => (await VintageAsync(budget).ConfigureAwait(false)).Year,
            cancellationToken);

    public Task<IReadOnlyList<CbpEstablishmentRow>> GetEstablishmentsAsync(
        IReadOnlyList<string> naicsCodes,
        IReadOnlyList<string> countyFips,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(naicsCodes);
        ArgumentNullException.ThrowIfNull(countyFips);

        // A keyless data query can only answer 302, so it is never sent: the user is told what to set.
        var key = _options.Keys.Census;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new MarketNotReadyException(
                $"{PsOptionsFactory.CensusKeyVariable} is not set, and the Census API answers every data "
                + "query without a key with a redirect instead of data.",
                "Get a free key at https://api.census.gov/data/key_signup.html, then set "
                + $"{PsOptionsFactory.CensusKeyVariable} for the server and restart it.");
        }

        var states = ByState(countyFips);
        var requests = naicsCodes.Count * states.Count;
        if (requests > MaxRequestsPerCall)
        {
            throw new MarketRequestException(
                $"This needs {requests} separate Census queries ({naicsCodes.Count} NAICS codes across "
                + $"{states.Count} states) and they run one after another, a second apart, which is past "
                + $"what one tool call can do ({MaxRequestsPerCall}).",
                "Size fewer NAICS codes at a time, or an area that falls inside fewer states.");
        }

        return WithinBudgetAsync(budget => RowsAsync(naicsCodes, states, key, budget), cancellationToken);
    }

    public void Dispose()
    {
        _http.Dispose();
        _requestGate.Dispose();
        _vintageGate.Dispose();
    }

    private static HttpClient CreateHttpClient(PsOptions options, HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(options);

        var client = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);

        client.Timeout = _transferTimeout;
        if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd(options.UserAgent))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(PsOptionsFactory.DefaultUserAgent);
        }

        return client;
    }

    private async Task<IReadOnlyList<CbpEstablishmentRow>> RowsAsync(
        IReadOnlyList<string> naicsCodes,
        List<(string State, List<string> Counties)> states,
        string key,
        CancellationToken cancellationToken)
    {
        var vintage = await VintageAsync(cancellationToken).ConfigureAwait(false);

        List<CbpEstablishmentRow> rows = [];
        foreach (var naics in naicsCodes)
        {
            foreach (var (state, counties) in states)
            {
                var body = await BodyAsync(vintage, naics, state, counties, key, cancellationToken)
                    .ConfigureAwait(false);

                if (body is not null)
                {
                    rows.AddRange(CbpTable.Parse(body));
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Runs <paramref name="work"/> under <see cref="CallBudget"/>. A budget that runs out is the Census
    /// API being too slow to answer, not a cancelled tool call, so it keeps its own message.
    /// </summary>
    private async Task<T> WithinBudgetAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        // The budget runs on the injected clock, like the request spacing, so both can be driven by a test.
        using var elapsed = new CancellationTokenSource(CallBudget, _clock);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, elapsed.Token);

        try
        {
            return await work(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MarketExternalException(
                $"The Census API did not finish answering within {CallBudget.TotalSeconds:0} seconds. Try "
                + "fewer NAICS codes or a smaller area, or try again later.");
        }
    }

    /// <summary>
    /// The counties grouped into one request per state: naming two states in one <c>in=state:</c> is an
    /// HTTP 400 "wildcard mismatch in geography hierarchy".
    /// </summary>
    private static List<(string State, List<string> Counties)> ByState(IReadOnlyList<string> countyFips)
    {
        List<(string State, List<string> Counties)> states = [];

        foreach (var fips in countyFips.Select(value => value?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal))
        {
            if (fips.Length != 5 || !fips.All(char.IsAsciiDigit))
            {
                throw new MarketRequestException(
                    $"'{fips}' is not a county GEOID: CBP is queried with a five-digit state + county code.");
            }

            var state = fips[..2];
            var index = states.FindIndex(entry => entry.State == state);
            if (index < 0)
            {
                states.Add((state, [fips[2..]]));
            }
            else
            {
                states[index].Counties.Add(fips[2..]);
            }
        }

        foreach (var (_, counties) in states)
        {
            counties.Sort(StringComparer.Ordinal);
        }

        return states;
    }

    private async Task<CbpVintage> VintageAsync(CancellationToken cancellationToken)
    {
        if (_vintage is { } known)
        {
            return known;
        }

        await _vintageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _vintage ??= await CachedVintageAsync(cancellationToken).ConfigureAwait(false);
            if (_vintage is { } cached)
            {
                return cached;
            }

            var discovered = await DiscoverVintageAsync(cancellationToken).ConfigureAwait(false);
            await StoreVintageAsync(discovered, cancellationToken).ConfigureAwait(false);

            _vintage = discovered;
            return discovered;
        }
        finally
        {
            _vintageGate.Release();
        }
    }

    /// <summary>
    /// The vintage to read, probed over <c>…/cbp/variables.json</c>. Metadata answers unkeyed, which is
    /// why the probe uses it: with a key a missing year is a 404, but without one every year is a 302, so
    /// a data query cannot tell the two apart.
    /// </summary>
    private async Task<CbpVintage> DiscoverVintageAsync(CancellationToken cancellationToken)
    {
        if (_options.CbpYear is int configured)
        {
            return await MetadataAsync(configured, cancellationToken).ConfigureAwait(false)
                ?? throw new MarketNotReadyException(
                    $"CBP {configured} is not published: its variables endpoint answered 404.",
                    $"Clear {PsOptionsFactory.CbpYearVariable} to use the newest published year.");
        }

        var newest = _clock.GetUtcNow().Year;
        for (var year = newest; year > newest - ProbedYears; year--)
        {
            if (await MetadataAsync(year, cancellationToken).ConfigureAwait(false) is { } vintage)
            {
                return vintage;
            }
        }

        throw new MarketNotReadyException(
            $"No published CBP year was found: every year from {newest} down to {newest - ProbedYears + 1} "
            + "answered 404 on its variables endpoint.",
            $"Set {PsOptionsFactory.CbpYearVariable} to a published vintage, or try again later - this is a "
            + "Census publication gap, not a problem with this server.");
    }

    /// <summary>The vintage for <paramref name="year"/>, or null when that year is not published.</summary>
    private async Task<CbpVintage?> MetadataAsync(int year, CancellationToken cancellationToken)
    {
        var uri = new Uri($"{ApiBaseUrl}/{year.ToString(CultureInfo.InvariantCulture)}/cbp/variables.json");
        using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await RefuseAsync(response, $"the CBP {year} variables endpoint", null, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return NaicsVariable(body) is { } variable ? new CbpVintage(year, variable) : null;
    }

    /// <summary>
    /// The vintage's NAICS variable, which is <c>NAICS2017</c> in 2023 and something else later. Taken
    /// from the metadata rather than hard-coded, because filtering on the wrong name is an HTTP 400.
    /// </summary>
    private static string? NaicsVariable(string metadata)
    {
        try
        {
            using var document = JsonDocument.Parse(metadata);

            if (!document.RootElement.TryGetProperty("variables", out var variables)
                || variables.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return variables.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => name.Length > 5
                    && name.StartsWith("NAICS", StringComparison.Ordinal)
                    && name[5..].All(char.IsAsciiDigit))
                .OrderByDescending(name => name, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One state's rows for one NAICS code, from the disk cache when it is still fresh. Null when CBP
    /// published nothing for that query, which is <em>unknown</em> and never zero.
    /// </summary>
    private async Task<string?> BodyAsync(
        CbpVintage vintage,
        string naics,
        string state,
        IReadOnlyList<string> counties,
        string key,
        CancellationToken cancellationToken)
    {
        var path = CachePath(vintage, naics, state, counties);
        var (cacheState, cached) = await ReadCacheAsync(path, cancellationToken).ConfigureAwait(false);
        if (cacheState is CacheState.Present or CacheState.Empty)
        {
            return cached;
        }

        var query =
            $"get={Columns},{vintage.NaicsVariable}"
            + $"&for=county:{string.Join(',', counties)}"
            + $"&in=state:{state}"
            + $"&{vintage.NaicsVariable}={Uri.EscapeDataString(naics)}"
            + $"&key={Uri.EscapeDataString(key)}";

        var uri = new Uri($"{ApiBaseUrl}/{vintage.Year.ToString(CultureInfo.InvariantCulture)}/cbp?{query}");
        var body = await DataAsync(uri, vintage, key, cancellationToken).ConfigureAwait(false);

        // An empty market is cached too: it is a stable answer, and re-asking costs the one thing this
        // call has least of.
        await WriteCacheAsync(path, body, cancellationToken).ConfigureAwait(false);
        return body;
    }

    /// <summary>
    /// One data response's body, or null when the query is valid but CBP publishes no rows for it — a 204,
    /// or a 200 with nothing in it. Parsing that as JSON would report a well-formed but unpublished NAICS
    /// code as a Census malfunction.
    /// </summary>
    private async Task<string?> DataAsync(Uri uri, CbpVintage vintage, string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var location = response.Headers.Location?.ToString() ?? string.Empty;
            var rejected = location.Contains("invalid_key", StringComparison.OrdinalIgnoreCase);

            throw new MarketNotReadyException(
                rejected
                    ? $"The Census API rejected {PsOptionsFactory.CensusKeyVariable}."
                    : $"The Census API did not accept the request as keyed, so {PsOptionsFactory.CensusKeyVariable} "
                      + "is missing or empty.",
                rejected
                    ? $"Check {PsOptionsFactory.CensusKeyVariable} against the key Census emailed you, or sign up "
                      + "again at https://api.census.gov/data/key_signup.html."
                    : $"Set {PsOptionsFactory.CensusKeyVariable} for the server and restart it.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new MarketNotReadyException(
                $"CBP {vintage.Year} answered 404 for this query, so that year is no longer published.",
                $"Clear {PsOptionsFactory.CbpYearVariable} to use the newest published year.");
        }

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        await RefuseAsync(response, $"the CBP {vintage.Year} data endpoint", key, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    /// <summary>
    /// Turns an unsuccessful response into the right failure: 429 is <c>RATE_LIMITED</c>, because waiting
    /// fixes it, and everything else is <c>EXTERNAL_API</c>.
    /// </summary>
    private static async Task RefuseAsync(
        HttpResponseMessage response,
        string what,
        string? key,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var body = Collapse(Redact(raw, key));
        var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : $": {body}";
        var message = $"The Census API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) "
            + $"from {what}{detail}";

        throw response.StatusCode == HttpStatusCode.TooManyRequests
            ? new MarketRateLimitedException(message)
            : new MarketExternalException(message);
    }

    /// <summary>
    /// One request, spaced behind the last and retried while the API is failing transiently. The headers
    /// get <see cref="ResponseTimeout"/>; the body then reads under the client's own limit.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        Exception? failure = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                // The real clock, not the injected one: the backoff is short enough to wait out, and a
                // manual clock that nobody advances would hang the retry instead of pacing it.
                await Task.Delay(_retryDelay * (attempt - 1), cancellationToken).ConfigureAwait(false);
            }

            response?.Dispose();
            response = null;
            failure = null;

            try
            {
                await ThrottleAsync(cancellationToken).ConfigureAwait(false);

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(ResponseTimeout);

                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);

                // The headers arrived inside the deadline, so stop it before the body is read.
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);

                if (!Transient(response.StatusCode))
                {
                    return response;
                }
            }
            catch (HttpRequestException exception)
            {
                failure = exception;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The header deadline or the client's own timeout, not the caller and not the call budget.
                failure = exception;
            }
        }

        if (response is not null)
        {
            return response;
        }

        throw new MarketExternalException(
            $"The Census API could not be reached after {MaxAttempts} attempts: {failure?.Message}");
    }

    /// <summary>
    /// Holds each request <see cref="_requestGap"/> behind the last, off the injected clock so a test can
    /// assert the spacing without waiting it out.
    /// </summary>
    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        if (_requestGap <= TimeSpan.Zero)
        {
            return;
        }

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _lastRequest + _requestGap - _clock.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _clock, cancellationToken).ConfigureAwait(false);
            }

            _lastRequest = _clock.GetUtcNow();
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static bool Transient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>An error body is data, never instructions: one short line of it, for the log and the user.</summary>
    private static string Collapse(string body)
    {
        var text = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 200 ? text : text[..200] + "...";
    }

    private static string Redact(string text, string? key) =>
        string.IsNullOrEmpty(key) ? text : text.Replace(key, "<key>", StringComparison.Ordinal);

    /// <summary>
    /// The cache entry for one query, named by a hash of what the query means. A hash rather than the
    /// URL, so the key never reaches a file name (CLAUDE.md: no secrets on disk).
    /// </summary>
    private string CachePath(CbpVintage vintage, string naics, string state, IReadOnlyList<string> counties)
    {
        var identity = string.Join(
            '|',
            vintage.Year.ToString(CultureInfo.InvariantCulture),
            vintage.NaicsVariable,
            naics,
            state,
            string.Join(',', counties));

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_options.CacheDirectory, "census", $"cbp-{hash[..32]}.json");
    }

    /// <summary>
    /// Where the resolved vintage is kept. Named after what was asked for, so pinning
    /// <c>PS_CBP_YEAR</c> cannot be answered from the entry that was discovered without it.
    /// </summary>
    private string VintageCachePath() =>
        Path.Combine(
            _options.CacheDirectory,
            "census",
            _options.CbpYear is int year
                ? $"cbp-vintage-{year.ToString(CultureInfo.InvariantCulture)}.json"
                : "cbp-vintage-latest.json");

    private async Task<CbpVintage?> CachedVintageAsync(CancellationToken cancellationToken)
    {
        var (state, body) = await ReadCacheAsync(VintageCachePath(), cancellationToken).ConfigureAwait(false);
        if (state != CacheState.Present || body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return root.TryGetProperty("year", out var year)
                && year.TryGetInt32(out var value)
                && root.TryGetProperty("naicsVariable", out var variable)
                && variable.GetString() is { Length: > 0 } name
                ? new CbpVintage(value, name)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task StoreVintageAsync(CbpVintage vintage, CancellationToken cancellationToken) =>
        WriteCacheAsync(
            VintageCachePath(),
            JsonSerializer.Serialize(new { year = vintage.Year, naicsVariable = vintage.NaicsVariable }),
            cancellationToken);

    /// <summary>
    /// The cached body when it is still inside <see cref="CacheLifetime"/>, measured against the injected
    /// clock rather than the file's timestamp so the lifetime is testable.
    /// </summary>
    private async Task<(CacheState State, string? Body)> ReadCacheAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (CacheState.Missing, null);
            }

            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;

            if (!root.TryGetProperty("fetchedAt", out var fetchedAt)
                || !root.TryGetProperty("body", out var body)
                || !fetchedAt.TryGetDateTimeOffset(out var when)
                || _clock.GetUtcNow() - when >= CacheLifetime)
            {
                return (CacheState.Missing, null);
            }

            return body.ValueKind == JsonValueKind.Null
                ? (CacheState.Empty, null)
                : (CacheState.Present, body.GetRawText());
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return (CacheState.Missing, null);
        }
    }

    /// <param name="body">The response body, or null for "CBP publishes nothing for this query".</param>
    private async Task WriteCacheAsync(string path, string? body, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("fetchedAt", _clock.GetUtcNow().UtcDateTime);
                writer.WritePropertyName("body");

                if (body is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteRawValue(body);
                }

                writer.WriteEndObject();
            }

            await File.WriteAllBytesAsync(path, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written is slower, not wrong.
        }
    }

    private enum CacheState
    {
        Missing,
        Empty,
        Present,
    }

    /// <param name="NaicsVariable">
    /// The NAICS column of this vintage, read from its metadata: <c>NAICS2017</c> in 2023.
    /// </param>
    private sealed record CbpVintage(int Year, string NaicsVariable);
}
