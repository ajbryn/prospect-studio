using System.Text;

namespace ProspectStudio.Core.Candidates;

/// <summary>
/// Company-name normalization (technical-design §7.1): lowercase, <c>&amp;</c> to <c>and</c>,
/// <strong>remove</strong> punctuation (not replace it with a space, so <c>L.L.C.</c> becomes
/// <c>llc</c> and <c>O'Brien</c> becomes <c>obrien</c>), strip legal words by position, collapse
/// whitespace. The result is what dedupe, suppression and warranty matchback all compare on, and what
/// the <c>name_norm</c> columns store - so a change here changes who merges with whom.
/// </summary>
/// <remarks>
/// <strong>Position matters.</strong> §7.1 originally said to remove these tokens anywhere, which
/// turns <c>CO Industries</c> into <c>industries</c> and throws away the distinguishing word. The rule
/// is now positional, and the mandated cases still work:
/// <c>The Bayou Fulfillment Co., LLC</c> loses a leading <c>the</c>, then a trailing <c>llc</c>, then a
/// trailing <c>co</c>, giving <c>bayou fulfillment</c>.
/// </remarks>
public static class NameNormalizer
{
    /// <summary>Stripped only when it is the <strong>first</strong> token.</summary>
    public static IReadOnlyList<string> LeadingWords { get; } = ["the"];

    /// <summary>
    /// Stripped only from the <strong>end</strong>, and repeatedly, so <c>Co Inc LLC</c> all go.
    /// </summary>
    public static IReadOnlyList<string> TrailingWords { get; } =
    [
        "inc", "incorporated", "llc", "ltd", "co", "corp", "corporation", "company",
        "lp", "llp", "pllc",
    ];

    private static readonly HashSet<string> _leading = new(LeadingWords, StringComparer.Ordinal);
    private static readonly HashSet<string> _trailing = new(TrailingWords, StringComparer.Ordinal);

    /// <summary>
    /// The normalized form of <paramref name="name"/>, or the empty string when nothing is left.
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var tokens = Tokenize(name);

        var start = 0;
        while (start < tokens.Count && _leading.Contains(tokens[start]))
        {
            start++;
        }

        var end = tokens.Count;
        while (end > start && _trailing.Contains(tokens[end - 1]))
        {
            end--;
        }

        return string.Join(' ', tokens.GetRange(start, end - start));
    }

    private static List<string> Tokenize(string name)
    {
        var builder = new StringBuilder(name.Length + 8);

        foreach (var character in name.ToLowerInvariant())
        {
            if (character == '&')
            {
                builder.Append(" and ");
            }
            else if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (char.IsWhiteSpace(character))
            {
                builder.Append(' ');
            }

            // Everything else is punctuation and is removed rather than replaced with a space, which is
            // what turns 'L.L.C.' into 'llc' and 'O'Brien' into 'obrien' (§7.1).
        }

        return [.. builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }
}
