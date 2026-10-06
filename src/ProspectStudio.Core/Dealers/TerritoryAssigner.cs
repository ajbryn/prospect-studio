using ProspectStudio.Core.Candidates;

namespace ProspectStudio.Core.Dealers;

/// <summary>
/// Territory assignment (technical-design §7.4): a ZIP5 rule wins; otherwise the county FIPS rule;
/// ties go to the lowest <c>priority</c> and then to the nearest branch by haversine
/// (<see cref="Candidates.Geohash.DistanceMeters"/>). No match at all is
/// <see cref="Assignments.Gap"/> with no dealer.
/// </summary>
/// <remarks>
/// Manual overrides are <strong>not</strong> this type's business: §7.4 says they are never
/// overwritten by re-assignment, and the store decides which leads to re-route. Keeping that out of
/// here means the rule can be table-tested on its own.
/// </remarks>
public static class TerritoryAssigner
{
    /// <summary>A rule whose branch is unknown cannot win a distance tie-break, but still assigns.</summary>
    private const double UnknownBranchDistance = double.MaxValue;

    /// <summary>
    /// The dealer and branch for <paramref name="subject"/>, or a gap.
    /// </summary>
    /// <param name="branches">
    /// Branch locations, used only for the nearest-branch tie-break. A rule whose branch is missing
    /// from this list still assigns; it simply cannot win a distance tie-break.
    /// </param>
    public static TerritoryAssignment Assign(
        AssignmentSubject subject,
        IReadOnlyList<TerritoryRule> territories,
        IReadOnlyList<BranchPoint> branches)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(territories);
        ArgumentNullException.ThrowIfNull(branches);

        // The levels are ordered, not merged and sorted by priority: a ZIP rule is more specific than a
        // county rule whatever the priorities say.
        if (subject.Zip is { Length: > 0 } zip
            && Best(territories, TerritoryLevels.Zip, zip, subject, branches) is { } byZip)
        {
            return new TerritoryAssignment(byZip.DealerId, byZip.BranchId, Assignments.Auto, TerritoryLevels.Zip);
        }

        if (subject.CountyFips is { Length: > 0 } county
            && Best(territories, TerritoryLevels.County, county, subject, branches) is { } byCounty)
        {
            return new TerritoryAssignment(byCounty.DealerId, byCounty.BranchId, Assignments.Auto, TerritoryLevels.County);
        }

        return new TerritoryAssignment(null, null, Assignments.Gap, null);
    }

    private static TerritoryRule? Best(
        IReadOnlyList<TerritoryRule> territories,
        string level,
        string code,
        AssignmentSubject subject,
        IReadOnlyList<BranchPoint> branches)
    {
        TerritoryRule? best = null;
        var bestDistance = 0.0;

        foreach (var rule in territories)
        {
            if (!string.Equals(rule.Level, level, StringComparison.Ordinal)
                || !string.Equals(rule.Code, code, StringComparison.Ordinal))
            {
                continue;
            }

            var distance = Distance(rule, subject, branches);
            if (best is null || Beats(rule, distance, best, bestDistance))
            {
                best = rule;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// §7.4's tie-break: lowest priority, then the nearest branch. The dealer and branch ids break a
    /// remaining tie so the answer cannot depend on the order SQLite returned the rules in (NFR-3).
    /// </summary>
    private static bool Beats(
        TerritoryRule candidate,
        double candidateDistance,
        TerritoryRule best,
        double bestDistance)
    {
        if (candidate.Priority != best.Priority)
        {
            return candidate.Priority < best.Priority;
        }

        if (candidateDistance != bestDistance)
        {
            return candidateDistance < bestDistance;
        }

        var byDealer = string.CompareOrdinal(candidate.DealerId, best.DealerId);
        return byDealer != 0
            ? byDealer < 0
            : string.CompareOrdinal(candidate.BranchId ?? string.Empty, best.BranchId ?? string.Empty) < 0;
    }

    private static double Distance(
        TerritoryRule rule,
        AssignmentSubject subject,
        IReadOnlyList<BranchPoint> branches)
    {
        foreach (var branch in branches)
        {
            if (string.Equals(branch.BranchId, rule.BranchId, StringComparison.Ordinal)
                && string.Equals(branch.DealerId, rule.DealerId, StringComparison.Ordinal))
            {
                return Geohash.DistanceMeters(subject.Lat, subject.Lon, branch.Lat, branch.Lon);
            }
        }

        return UnknownBranchDistance;
    }
}
