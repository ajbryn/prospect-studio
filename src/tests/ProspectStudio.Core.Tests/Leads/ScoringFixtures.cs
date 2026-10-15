using ProspectStudio.Core.Leads;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Leads;

/// <summary>
/// Baselines and helpers for the §7.6 scorer tests. The baselines live here rather than as defaults on
/// <see cref="LeadFeatures"/> so the production type pre-decides nothing the spec does not.
/// </summary>
internal static class ScoringFixtures
{
    /// <summary>The profile minimum the sample search profile sets (<c>size.employeesMin</c>).</summary>
    public const int ProfileMinEmployees = 20;

    /// <summary>
    /// The minimum the sample profile's fourth segment, "Facilities &amp; property management", sets for
    /// itself. §7.6 prefers it over <see cref="ProfileMinEmployees"/> for a lead that matched it.
    /// </summary>
    public const int SegmentMinEmployees = 50;

    /// <summary>
    /// Everything at its "no evidence" value, so changing one member changes exactly one contribution.
    /// <c>FacilityKeywordCount</c> is null rather than 0: before chunk C7 there is no website text at
    /// all, which §7.6 scores 0.4, not the 0.2 that "zero keywords found" scores.
    /// </summary>
    public static LeadFeatures Floor { get; } = new()
    {
        SegmentMatch = SegmentMatch.None,
        EmployeeEstimate = null,
        ProfileMinEmployees = ProfileMinEmployees,
        SegmentMinEmployees = null,
        ResearchFacilityLevel = null,
        FacilityKeywordCount = null,
        Signals = [],
        BranchDistanceMiles = null,
        OvertureConfidence = 0d,
        WebsiteReachable = false,
        LlmAdjustment = 0,
    };

    /// <summary>
    /// Two buying signals inside §7.6's window as measured from
    /// <see cref="FixedTimeProvider.ScoringReference"/> - the dates worked example 1 carries.
    /// </summary>
    public static IReadOnlyList<SignalFact> TwoRecentBuyingSignals { get; } =
    [
        new(SignalTypes.Permit, "2026-07"),
        new(SignalTypes.Hiring, "2026-09-12"),
    ];

    /// <summary>Every feature at its maximum except <c>signals</c>: §7.6's 0.75 ceiling.</summary>
    public static LeadFeatures MaximumWithoutSignals { get; } = Floor with
    {
        SegmentMatch = SegmentMatch.Taxonomy,
        EmployeeEstimate = 500,
        ResearchFacilityLevel = FacilityFitLevels.High,
        BranchDistanceMiles = 1d,
        OvertureConfidence = 1d,
        WebsiteReachable = true,
    };

    /// <summary>Every feature at its maximum, including two buying signals: Σ wᵢ·fᵢ = 1.0.</summary>
    public static LeadFeatures Maximum { get; } = MaximumWithoutSignals with
    {
        Signals = TwoRecentBuyingSignals,
    };

    /// <summary>
    /// The same lead with exactly one qualifying signal: Σ = 0.90. It is the other side of §7.6's tier-A
    /// guard - one piece of cited evidence is all it takes to clear 80 honestly.
    /// </summary>
    public static LeadFeatures MaximumWithOneSignal { get; } = MaximumWithoutSignals with
    {
        Signals = [TwoRecentBuyingSignals[0]],
    };

    /// <summary>
    /// A deliberately mixed set, so the weighted sum is tested on something other than the corners:
    /// 0.8, 0.5, 0.75, 0.6, 0.6 and 0.86 → Σ = 0.691 → 69. It carries a qualifying signal, so §7.6's
    /// tier-A cap never applies to it and the adjustment arithmetic can be read on its own.
    /// </summary>
    public static LeadFeatures Mixed { get; } = new()
    {
        SegmentMatch = SegmentMatch.NameKeyword,
        EmployeeEstimate = 15,
        ProfileMinEmployees = ProfileMinEmployees,
        ResearchFacilityLevel = FacilityFitLevels.Medium,
        Signals = [new SignalFact(SignalTypes.Permit, "2026-07")],
        BranchDistanceMiles = 30d,
        OvertureConfidence = 0.8d,
        WebsiteReachable = true,
        LlmAdjustment = 0,
    };

    /// <summary>
    /// The one feature set whose weighted sum is an <strong>exact</strong> IEEE-754 midpoint under §7.6's
    /// default weights: 0.25×1.0 + 0.15×0.1 + 0.20×1.0 + 0.25×0.6 + 0.05×0 + 0.10×0.3, which multiplied
    /// by 100 is exactly <c>64.5</c>. Away-from-zero rounding gives 65 (tier B); banker's rounding gives
    /// 64 (tier C), so the two modes disagree on the tier.
    /// </summary>
    /// <remarks>
    /// The exactness depends on summing the contributions in the order §7.6's table lists them. Summing
    /// them differently can land a bit either side of the midpoint, which is a good reason to sum in that
    /// order and a poor reason to weaken this test.
    /// </remarks>
    public static LeadFeatures ExactMidpoint { get; } = new()
    {
        SegmentMatch = SegmentMatch.Taxonomy,
        EmployeeEstimate = 9,
        ProfileMinEmployees = ProfileMinEmployees,
        ResearchFacilityLevel = FacilityFitLevels.High,
        Signals = [new SignalFact(SignalTypes.Permit, "2026-07")],
        BranchDistanceMiles = null,
        OvertureConfidence = 0d,
        WebsiteReachable = true,
        LlmAdjustment = 0,
    };

    /// <summary>
    /// A lead that matched the sample profile's fourth segment, which sets its own
    /// <see cref="SegmentMinEmployees"/> of 50 against the profile's 20.
    /// </summary>
    public static LeadFeatures InSegmentWithItsOwnMinimum { get; } = Floor with
    {
        SegmentMatch = SegmentMatch.Taxonomy,
        SegmentName = "Facilities & property management",
        SegmentMinEmployees = SegmentMinEmployees,
    };

    /// <summary>A scorer on §7.6's default weights and the pinned reference instant.</summary>
    public static LeadScorer Scorer(ScoringWeights? weights = null, TimeProvider? clock = null) =>
        new(weights ?? ScoringWeights.Default, clock ?? FixedTimeProvider.AtScoringReference());

    /// <summary>One feature's row of the breakdown, with a message naming what was there instead.</summary>
    public static ScoreFeatureContribution Of(this ScoreBreakdown breakdown, string feature)
    {
        ArgumentNullException.ThrowIfNull(breakdown);

        var matches = breakdown.Features.Where(row => row.Feature == feature).ToList();

        matches.Count.ShouldBe(
            1,
            $"score_breakdown_json carries one row per feature (technical-design §7.6), so Claude can "
            + $"explain a score. Looking for '{feature}'; the breakdown holds: "
            + string.Join(", ", breakdown.Features.Select(row => row.Feature)));

        return matches[0];
    }

    /// <summary>The breakdown as text, for a failure message that shows the whole calculation.</summary>
    public static string Describe(this ScoreBreakdown breakdown)
    {
        ArgumentNullException.ThrowIfNull(breakdown);

        var rows = breakdown.Features.Select(row =>
            $"{row.Feature}={row.Value:0.####}×{row.Weight:0.####}={row.Contribution:0.#####}");

        var cap = breakdown.CappedFrom is null ? string.Empty : $" [capped from {breakdown.CappedFrom}]";

        return $"base {breakdown.BaseScore} + adjustment {breakdown.LlmAdjustment} = {breakdown.Score} "
            + $"({breakdown.Tier}){cap} as of {breakdown.AsOf:O}, size minimum "
            + $"{breakdown.SizeMinimum.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} "
            + $"from {breakdown.SizeMinimum.Source} | {string.Join(", ", rows)}";
    }
}
