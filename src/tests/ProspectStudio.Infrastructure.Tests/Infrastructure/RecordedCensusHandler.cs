using System.Net;
using System.Text.RegularExpressions;
using ProspectStudio.Tests.Shared;

namespace ProspectStudio.Infrastructure.Tests.Infrastructure;

/// <summary>
/// Answers Census requests from the recorded bodies in <c>src/tests/Fixtures/cbp</c> and records every
/// request URI, so a test can assert what the client <em>asked for</em> as well as what it made of the
/// answer. The seam the CBP client must go through: nothing in C3's unit tests reaches the network.
/// </summary>
/// <remarks>
/// A request whose <c>in=state:</c> names more than one state is answered with the recorded HTTP 400
/// body, exactly as the live API answers it, so building a cross-state URL fails loudly instead of
/// quietly working against a fake.
/// </remarks>
internal sealed partial class RecordedCensusHandler : HttpMessageHandler
{
    private readonly List<Uri> _requests = [];
    private readonly List<(Uri Uri, DateTimeOffset At)> _stamped = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// The clock request times are read from, so a test can assert the spacing between requests. Set it to
    /// the same provider the client was given.
    /// </summary>
    public TimeProvider? Clock { get; set; }

    /// <summary>Years whose <c>variables.json</c> answers 200. Everything else is the recorded 404.</summary>
    public HashSet<int> PublishedYears { get; } = [CbpFixtures.LatestCbpYear];

    /// <summary>What a data query answers with, keyed by the NAICS code in the query string.</summary>
    public Dictionary<string, string> Bodies { get; } = new(StringComparer.Ordinal);

    /// <summary>Set to answer every data query with this status instead of a body.</summary>
    public HttpStatusCode? DataStatus { get; set; }

    /// <summary>Set to answer every data query with a 302 to this page, as a key problem does.</summary>
    public string? RedirectTo { get; set; }

    /// <summary>
    /// The body for a NAICS code <see cref="Bodies"/> does not name. Lets the request-cap test ask for a
    /// dozen codes without pretending to have recorded a dozen responses.
    /// </summary>
    public string? Fallback { get; set; }

    /// <summary>
    /// A status to answer with from this data request onwards (1-based), after the earlier ones answered
    /// normally. Used to fail a multi-state call part way through, which is where a partial result would
    /// leak out if one could.
    /// </summary>
    public (int FromRequest, HttpStatusCode Status)? FailFrom { get; set; }

    /// <summary>
    /// When set, a data request never answers and waits for its own cancellation instead — a Census that
    /// has stopped responding, which is what the call budget exists for.
    /// </summary>
    public bool HangForever { get; set; }

    public IReadOnlyList<Uri> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public int RequestCount => Requests.Count;

    /// <summary>When each data request was made, by <see cref="Clock"/>, in order.</summary>
    public IReadOnlyList<DateTimeOffset> DataRequestTimes
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    .. _stamped
                        .Where(entry => !entry.Uri.AbsolutePath.EndsWith("variables.json", StringComparison.Ordinal))
                        .Select(entry => entry.At),
                ];
            }
        }
    }

    /// <summary>Only the data queries, which is what the cache and the per-state split are about.</summary>
    public IReadOnlyList<Uri> DataRequests =>
        [.. Requests.Where(uri => !uri.AbsolutePath.EndsWith("variables.json", StringComparison.Ordinal))];

    public IReadOnlyList<Uri> ProbeRequests =>
        [.. Requests.Where(uri => uri.AbsolutePath.EndsWith("variables.json", StringComparison.Ordinal))];

    public static RecordedCensusHandler Houston() => new()
    {
        Bodies =
        {
            ["4931"] = CbpFixtures.Houston4931,
            ["238210"] = CbpFixtures.Houston238210,
        },
    };

    public static RecordedCensusHandler Harris() => new()
    {
        Bodies =
        {
            ["4931"] = CbpFixtures.Harris4931,
            ["49311"] = CbpFixtures.Harris49311,
            ["238210"] = CbpFixtures.Harris238210,
        },
    };

    /// <summary>The <c>in=state:</c> values of a request, which must never hold more than one.</summary>
    public static IReadOnlyList<string> StatesIn(Uri uri)
    {
        var match = InClause().Match(Uri.UnescapeDataString(uri.Query));
        return match.Success
            ? match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    /// <summary>The year in <c>/data/&lt;year&gt;/cbp…</c>.</summary>
    public static int YearIn(Uri uri)
    {
        var match = YearSegment().Match(uri.AbsolutePath);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("The request has no URI.");

        lock (_gate)
        {
            _requests.Add(uri);
            _stamped.Add((uri, Clock?.GetUtcNow() ?? DateTimeOffset.MinValue));
        }

        if (HangForever && !uri.AbsolutePath.EndsWith("variables.json", StringComparison.Ordinal))
        {
            // Never answers. The caller's budget has to be what ends this.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        return Answer(uri);
    }

    private HttpResponseMessage Answer(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith("variables.json", StringComparison.Ordinal))
        {
            return PublishedYears.Contains(YearIn(uri))
                ? Json(CbpFixtures.Variables2023)
                : NotFound();
        }

        if (RedirectTo is { } page)
        {
            // What the live API does without a usable key: 302, plus the header that names the reason.
            // Following it lands on a 200 HTML page, which is why the client disables auto-redirect.
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri($"https://api.census.gov/data/{page}");
            redirect.Headers.TryAddWithoutValidation("X-DataWebAPI-KeyError", "1");
            redirect.Content = new StringContent(string.Empty);
            return redirect;
        }

        if (StatesIn(uri).Count > 1)
        {
            // Verbatim from the live API: for=county:201,001&in=state:48,22 is a 400. Checked before
            // DataStatus so a cross-state URL fails the same way whatever else the test set up.
            return BadRequest();
        }

        if (DataStatus is { } status)
        {
            return status == HttpStatusCode.BadRequest
                ? BadRequest()
                : new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
        }

        if (FailFrom is { } failure && DataRequests.Count >= failure.FromRequest)
        {
            return new HttpResponseMessage(failure.Status) { Content = new StringContent(string.Empty) };
        }

        var naics = NaicsFilter().Match(Uri.UnescapeDataString(uri.Query));
        if (naics.Success && Bodies.TryGetValue(naics.Groups[1].Value, out var body))
        {
            return Json(body);
        }

        if (Fallback is { } spare)
        {
            return Json(spare);
        }

        throw new InvalidOperationException(
            $"No recorded body for this query's NAICS filter. Registered: {string.Join(", ", Bodies.Keys)}.");
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound)
        {
            Content = new StringContent(CbpFixtures.NotFoundHtml, System.Text.Encoding.UTF8, "text/html"),
        };

    /// <summary>The recorded cross-state 400, body and all.</summary>
    private static HttpResponseMessage BadRequest() =>
        new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(CbpFixtures.CrossStateError, System.Text.Encoding.UTF8, "text/plain"),
        };

    [GeneratedRegex(@"[?&]in=state:([^&]*)")]
    private static partial Regex InClause();

    [GeneratedRegex(@"[?&]NAICS\d{4}=([^&]*)")]
    private static partial Regex NaicsFilter();

    [GeneratedRegex(@"/data/(\d{4})/")]
    private static partial Regex YearSegment();
}
