using ProspectStudio.Core.Configuration;

namespace ProspectStudio.Infrastructure.Reference;

/// <summary>
/// The one <see cref="HttpClient"/> the configured first-party bulk-data endpoints share - the Census
/// downloads, the Census geocoder and the Overture release catalog - identified with the configured
/// <c>PS_USER_AGENT</c> (CLAUDE.md §Web fetching).
/// </summary>
/// <remarks>
/// <para>
/// The 10-second rule is a response deadline, not a transfer budget: the county boundary file is about
/// 12 MB, so callers bound the <em>headers</em> with <see cref="ResponseTimeout"/> through a linked token
/// and let the body stream inside the client's much longer overall limit.
/// </para>
/// <para>
/// It is deliberately host-neutral. There is no per-domain pacing here, so a caller that adds a
/// <em>crawled</em> host - a company website in C7's enrichment - must not reuse this client: that path
/// needs robots.txt, the concurrency cap and the one-request-per-second-per-domain throttle.
/// </para>
/// </remarks>
public sealed class ProspectStudioHttpClient : IDisposable
{
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan _transferTimeout = TimeSpan.FromMinutes(10);

    public ProspectStudioHttpClient(PsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Client = new HttpClient { Timeout = _transferTimeout };
        if (!Client.DefaultRequestHeaders.UserAgent.TryParseAdd(options.UserAgent))
        {
            Client.DefaultRequestHeaders.UserAgent.ParseAdd(PsOptionsFactory.DefaultUserAgent);
        }
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}
