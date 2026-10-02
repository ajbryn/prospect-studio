namespace ProspectStudio.Core.Market;

/// <summary>
/// A prerequisite for market sizing is missing, which <c>estimate_market</c> reports as
/// <c>NOT_READY</c>: no <c>CENSUS_API_KEY</c>, a key the API rejects, or no published CBP year.
/// </summary>
/// <remarks>
/// A 302 from the data API always means a key problem — <c>missing_key.html</c> or
/// <c>invalid_key.html</c> — and must never surface as a JSON parse failure, which is what happens when
/// the redirect is followed to its 200 HTML page.
/// </remarks>
public sealed class MarketNotReadyException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>The Census API failed after retries, which the tool reports as <c>EXTERNAL_API</c>.</summary>
public sealed class MarketExternalException(string message) : Exception(message);

/// <summary>
/// The Census API answered 429 even after retries, which the tool reports as <c>RATE_LIMITED</c> rather
/// than <c>EXTERNAL_API</c>: waiting fixes it, so the code a skill branches on should say so.
/// </summary>
public sealed class MarketRateLimitedException(string message) : Exception(message);

/// <summary>The request made no sense, which the tool reports as <c>VALIDATION_FAILED</c>.</summary>
/// <param name="hint">
/// What to do instead, when the general advice would misdirect: a request that asks for too many Census
/// queries at once has nothing wrong with its codes or its geography, only with their product.
/// </param>
public sealed class MarketRequestException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}
