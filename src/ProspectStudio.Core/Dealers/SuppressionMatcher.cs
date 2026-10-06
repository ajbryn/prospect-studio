using ProspectStudio.Core.Candidates;

namespace ProspectStudio.Core.Dealers;

/// <summary>
/// Suppression matching (technical-design §7.3): a lead is suppressed when any suppression row matches
/// it by <strong>domain</strong>, by <strong>exact <c>name_norm</c></strong> (with the same ZIP when
/// the row has one), or by <strong>fuzzy name ≥ 0.92 with the same ZIP</strong>. The reason and the
/// matching row id are both recorded.
/// </summary>
public static class SuppressionMatcher
{
    /// <summary>
    /// §7.3's fuzzy threshold. Declared literally rather than as an alias of §7.2's dedupe threshold:
    /// the two are independently chosen figures that happen to agree, so tuning how readily two records
    /// merge must not quietly change who gets suppressed. §7.9's matchback threshold is a third (0.90).
    /// </summary>
    public const double FuzzyThreshold = 0.92;

    /// <summary>
    /// The first suppression row that matches <paramref name="subject"/>, or null when none does. The
    /// rules are tried in §7.3's order, so <see cref="SuppressionHit.Rule"/> says which one fired.
    /// </summary>
    public static SuppressionHit? Match(
        SuppressionSubject subject,
        IReadOnlyList<SuppressionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(rules);

        if (subject.Domain is { Length: > 0 } domain)
        {
            foreach (var rule in rules)
            {
                if (rule.Domain is { Length: > 0 } listed
                    && string.Equals(listed, domain, StringComparison.Ordinal))
                {
                    return Hit(rule, SuppressionRules.Domain, Similarity(subject, rule));
                }
            }
        }

        if (subject.NameNorm.Length == 0)
        {
            return null;
        }

        foreach (var rule in rules)
        {
            if (string.Equals(rule.NameNorm, subject.NameNorm, StringComparison.Ordinal)
                && (rule.Zip is not { Length: > 0 } || SameZip(subject, rule)))
            {
                return Hit(rule, SuppressionRules.Name, 1.0);
            }
        }

        foreach (var rule in rules)
        {
            // The ZIP test is unconditional here, unlike the exact-name rule above: a row with no ZIP
            // never fuzzy-matches, because a false suppression silently deletes a real prospect (§7.3).
            if (rule.Zip is not { Length: > 0 } || !SameZip(subject, rule))
            {
                continue;
            }

            var similarity = Similarity(subject, rule);
            if (similarity >= FuzzyThreshold)
            {
                return Hit(rule, SuppressionRules.Fuzzy, similarity);
            }
        }

        return null;
    }

    private static SuppressionHit Hit(SuppressionRule rule, string matched, double similarity) =>
        new(rule.Id, rule.Reason, matched, similarity);

    private static double Similarity(SuppressionSubject subject, SuppressionRule rule) =>
        JaroWinkler.Similarity(subject.NameNorm, rule.NameNorm);

    private static bool SameZip(SuppressionSubject subject, SuppressionRule rule) =>
        subject.Zip is { Length: > 0 } zip && string.Equals(rule.Zip, zip, StringComparison.Ordinal);
}
