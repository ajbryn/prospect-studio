namespace ProspectStudio.Core.Naics;

/// <summary>
/// The NAICS 2022 table. Core defines it; Infrastructure reads the committed
/// <c>Infrastructure/Reference/naics2022.csv</c>, which ships inside the assembly so the lookup works
/// on a first run without <c>prepare_data</c> (implementation-plan C2).
/// </summary>
public interface INaicsCatalog
{
    Task<IReadOnlyList<NaicsEntry>> GetEntriesAsync(CancellationToken cancellationToken);
}

/// <summary>What <c>lookup_naics</c> returns (mcp-tools.md §lookup_naics).</summary>
public sealed record NaicsLookupResult(IReadOnlyList<NaicsEntry> Results);

/// <summary>
/// The rules behind <c>lookup_naics</c>: load the table, rank it with <see cref="NaicsSearch"/> and cap
/// the result so the response stays inside the compact-output budget (CLAUDE.md §Hard rules).
/// </summary>
public sealed class NaicsLookupService(INaicsCatalog catalog)
{
    /// <summary>mcp-tools.md §lookup_naics shows <c>limit: 10</c>.</summary>
    public const int DefaultLimit = 10;

    public const int MaxLimit = 50;

    public async Task<NaicsLookupResult> LookupAsync(string? query, int? limit, CancellationToken cancellationToken)
    {
        var entries = await catalog.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        var capped = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        return new NaicsLookupResult(NaicsSearch.Rank(entries, query ?? string.Empty, capped));
    }
}
