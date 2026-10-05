namespace ProspectStudio.Core.Candidates;

/// <summary>
/// Jaro-Winkler similarity, used by dedupe (technical-design §7.2 rule 3, ≥ 0.92), suppression (§7.3,
/// ≥ 0.92) and warranty matchback (§7.9, ≥ 0.90). Always fed <c>name_norm</c> values, never raw names.
/// </summary>
public static class JaroWinkler
{
    /// <summary>The dedupe and suppression threshold.</summary>
    public const double DuplicateThreshold = 0.92;

    /// <summary>The warranty matchback threshold (§7.9).</summary>
    public const double MatchbackThreshold = 0.90;

    /// <summary>Winkler's prefix scale, and the longest prefix it rewards.</summary>
    private const double PrefixScale = 0.1;
    private const int MaxPrefix = 4;

    /// <summary>Below this Jaro score Winkler leaves the result alone.</summary>
    private const double BoostThreshold = 0.7;

    /// <summary>1.0 for identical strings, 0.0 when nothing matches.</summary>
    public static double Similarity(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return 0.0;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (left.Length == 0 || right.Length == 0)
        {
            return 0.0;
        }

        var jaro = Jaro(left, right);
        if (jaro < BoostThreshold)
        {
            return jaro;
        }

        var prefix = 0;
        var shared = Math.Min(Math.Min(left.Length, right.Length), MaxPrefix);
        while (prefix < shared && left[prefix] == right[prefix])
        {
            prefix++;
        }

        return jaro + (prefix * PrefixScale * (1 - jaro));
    }

    private static double Jaro(string left, string right)
    {
        var window = Math.Max((Math.Max(left.Length, right.Length) / 2) - 1, 0);
        var leftMatched = new bool[left.Length];
        var rightMatched = new bool[right.Length];
        var matches = 0;

        for (var index = 0; index < left.Length; index++)
        {
            var from = Math.Max(0, index - window);
            var to = Math.Min(index + window + 1, right.Length);

            for (var other = from; other < to; other++)
            {
                if (rightMatched[other] || left[index] != right[other])
                {
                    continue;
                }

                leftMatched[index] = true;
                rightMatched[other] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
        {
            return 0.0;
        }

        var transpositions = 0;
        var position = 0;
        for (var index = 0; index < left.Length; index++)
        {
            if (!leftMatched[index])
            {
                continue;
            }

            while (!rightMatched[position])
            {
                position++;
            }

            if (left[index] != right[position])
            {
                transpositions++;
            }

            position++;
        }

        return (((double)matches / left.Length)
            + ((double)matches / right.Length)
            + ((matches - (transpositions / 2.0)) / matches)) / 3.0;
    }
}
