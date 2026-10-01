namespace ProspectStudio.Core.Naics;

/// <summary>
/// One row of <c>Infrastructure/Reference/naics2022.csv</c>, which is generated from the Census NAICS
/// 2022 file (implementation-plan C2).
/// </summary>
/// <param name="Code">
/// 2 to 6 characters. Sector codes are ranges and therefore strings, not numbers: <c>31-33</c>,
/// <c>44-45</c>, <c>48-49</c>.
/// </param>
/// <param name="Level">Digits in the code: 2 sector, 3 subsector, 4 industry group, 5, 6 national industry.</param>
public sealed record NaicsEntry(string Code, string Title, int Level);

/// <summary>
/// Keyword ranking for <c>lookup_naics</c> by <strong>common-prefix scoring</strong>
/// (implementation-plan C2): a query token matches a title token when either is a prefix of the other
/// (minimum 4 characters) <em>or</em> they share a common prefix of at least 6 characters. Rank by
/// matched-token count first, then by coverage (matched title tokens over all of them), then by total
/// common-prefix length.
/// </summary>
/// <remarks>
/// Plain token or prefix matching is not enough: <c>warehouse</c> has to reach 4931 "Warehousing and
/// Storage", and "warehousing" does not start with "warehouse" (they diverge at
/// <c>warehous|e</c> vs <c>warehous|i</c>). The 6-character arm covers that without a stemmer.
/// </remarks>
public static class NaicsSearch
{
    /// <summary>A prefix match is only a match once the shorter word is this long.</summary>
    private const int MinimumPrefixMatch = 4;

    /// <summary>How much two words must share when neither is a prefix of the other.</summary>
    private const int MinimumSharedPrefix = 6;

    /// <summary>
    /// The best <paramref name="limit"/> matches for <paramref name="query"/>, best first. An entry
    /// that matches none of the query's tokens is not a result.
    /// </summary>
    public static IReadOnlyList<NaicsEntry> Rank(IReadOnlyList<NaicsEntry> entries, string query, int limit)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (limit <= 0)
        {
            return [];
        }

        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0)
        {
            return [];
        }

        var scored = new List<ScoredEntry>();
        foreach (var entry in entries)
        {
            if (Score(entry, queryTokens) is { } hit)
            {
                scored.Add(hit);
            }
        }

        return
        [
            .. scored
                // The real table repeats titles across levels - 493 and 4931 are both "Warehousing and
                // Storage" - and the longer code is the more specific one, so it is the one to keep.
                .GroupBy(hit => hit.Entry.Title.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(hit => hit.Entry.Code.Length)
                    .ThenBy(hit => hit.Entry.Code, StringComparer.Ordinal)
                    .First())
                .OrderByDescending(hit => hit.Matched)
                .ThenByDescending(hit => hit.Coverage)
                .ThenByDescending(hit => hit.SharedPrefix)
                .ThenBy(hit => hit.Entry.Code.Length)
                .ThenBy(hit => hit.Entry.Code, StringComparer.Ordinal)
                .Take(limit)
                .Select(hit => hit.Entry),
        ];
    }

    private static ScoredEntry? Score(NaicsEntry entry, List<string> queryTokens)
    {
        var titleTokens = Tokenize(entry.Title);
        if (titleTokens.Count == 0)
        {
            return null;
        }

        var matched = 0;
        var sharedPrefix = 0;

        foreach (var titleToken in titleTokens)
        {
            var best = -1;
            foreach (var queryToken in queryTokens)
            {
                var shared = SharedPrefixLength(queryToken, titleToken);
                if (IsMatch(queryToken, titleToken, shared) && shared > best)
                {
                    best = shared;
                }
            }

            if (best < 0)
            {
                continue;
            }

            matched++;
            sharedPrefix += best;
        }

        return matched == 0
            ? null
            : new ScoredEntry(entry, matched, (double)matched / titleTokens.Count, sharedPrefix);
    }

    /// <summary>
    /// One word is a prefix of the other when they share all of the shorter one; the alternative arm
    /// catches pairs that diverge late, which is how "warehouse" reaches "Warehousing".
    /// </summary>
    private static bool IsMatch(string queryToken, string titleToken, int shared) =>
        (shared == Math.Min(queryToken.Length, titleToken.Length) && shared >= MinimumPrefixMatch)
        || shared >= MinimumSharedPrefix;

    private static int SharedPrefixLength(string left, string right)
    {
        var shared = 0;
        var length = Math.Min(left.Length, right.Length);
        while (shared < length && left[shared] == right[shared])
        {
            shared++;
        }

        return shared;
    }

    private static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var token = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (token.Length > 0)
            {
                tokens.Add(token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
        }

        return tokens;
    }

    /// <param name="Matched">Title tokens that matched at least one query token.</param>
    /// <param name="Coverage">
    /// <paramref name="Matched"/> over every title token: what defeats the "Warehouse Clubs" trap, where
    /// a long omnibus title contains the query word and so beats a short specific one on prefix length.
    /// </param>
    private sealed record ScoredEntry(NaicsEntry Entry, int Matched, double Coverage, int SharedPrefix);
}
