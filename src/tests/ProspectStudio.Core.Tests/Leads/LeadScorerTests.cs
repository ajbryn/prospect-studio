using ProspectStudio.Core.Leads;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Leads;

/// <summary>
/// Technical-design §7.6's deterministic scorer, feature by feature and then as a whole:
/// <c>score = clamp(round(100 × Σ wᵢ·fᵢ) + clamp(llmAdjustment, −15, +15), 0, 100)</c>, tiers
/// A ≥ 80, B ≥ 65, C below.
/// </summary>
/// <remarks>
/// Every case holds five features at their floor and moves one, so a band test cannot pass because two
/// mistakes cancelled out. The totals are chosen to avoid an exact <c>.5</c>, because §7.6 says "round"
/// without naming a midpoint rule - see the C6 report.
/// </remarks>
public class LeadScorerTests
{
    private const double Tolerance = 1e-9;

    [Theory]
    [InlineData(SegmentMatch.Taxonomy, 1.0)]
    [InlineData(SegmentMatch.NameKeyword, 0.8)]
    [InlineData(SegmentMatch.WebsiteKeyword, 0.6)]
    [InlineData(SegmentMatch.None, 0.2)]
    public void SegmentFit_FollowsTheBandsOfSection76(SegmentMatch match, double expected)
    {
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Floor with { SegmentMatch = match });

        breakdown.Of(ScoreFeatures.SegmentFit).Value.ShouldBe(
            expected,
            Tolerance,
            "§7.6 segmentFit: 'Taxonomy match to a segment's categories = 1.0; name keyword = 0.8; "
            + $"website keyword = 0.6; otherwise 0.2'. {breakdown.Describe()}");
    }

    [Theory]
    [InlineData(500, 1.0)]
    [InlineData(20, 1.0)]
    [InlineData(19, 0.5)]
    [InlineData(10, 0.5)]
    [InlineData(9, 0.1)]
    [InlineData(1, 0.1)]
    [InlineData(null, 0.5)]
    public void SizeFit_ComparesTheResearchEstimateWithTheProfileMinimum(int? employees, double expected)
    {
        // The minimum is the sample profile's size.employeesMin of 20, so 50 % of it is 10.
        var breakdown = ScoringFixtures.Scorer().Score(
            ScoringFixtures.Floor with { EmployeeEstimate = employees });

        breakdown.Of(ScoreFeatures.SizeFit).Value.ShouldBe(
            expected,
            Tolerance,
            "§7.6 sizeFit: '≥ min = 1.0; ≥ 50 % of min = 0.5; below = 0.1; unknown = 0.5'. An unknown "
            + "size scores the same as half the minimum on purpose: most leads have no employee count "
            + $"until somebody researches them, and 0.1 would bury all of them. {breakdown.Describe()}");

        breakdown.SizeMinimum.ShouldBe(
            new SizeMinimum(ScoringFixtures.ProfileMinEmployees, SizeMinimumSources.Profile),
            "the matched segment sets no minimum of its own, so the profile's applies - and the breakdown "
            + $"says which, because 0.5 means different things against 20 and against 50. {breakdown.Describe()}");
    }

    [Theory]
    [InlineData(50, 1.0)]
    [InlineData(60, 1.0)]
    [InlineData(49, 0.5)]
    [InlineData(25, 0.5)]
    [InlineData(24, 0.1)]
    public void SizeFit_PrefersTheMatchedSegmentsOwnMinimum(int employees, double expected)
    {
        // §7.6: the applicable minimum is "the matched segment's minEmployees when that segment sets one,
        // otherwise the profile's size.employeesMin". The sample profile models exactly this conflict -
        // "Facilities & property management" wants 50 where the profile wants 20 - so every one of these
        // employee counts would score 1.0 if the profile-wide number were used instead.
        var breakdown = ScoringFixtures.Scorer().Score(
            ScoringFixtures.InSegmentWithItsOwnMinimum with { EmployeeEstimate = employees });

        breakdown.Of(ScoreFeatures.SizeFit).Value.ShouldBe(
            expected,
            Tolerance,
            $"{employees} employees against the segment's minimum of {ScoringFixtures.SegmentMinEmployees}, "
            + $"not the profile's {ScoringFixtures.ProfileMinEmployees}. {breakdown.Describe()}");

        breakdown.SizeMinimum.ShouldBe(
            new SizeMinimum(ScoringFixtures.SegmentMinEmployees, SizeMinimumSources.Segment),
            $"and the breakdown records which number was in play. {breakdown.Describe()}");
    }

    [Fact]
    public void SizeFit_IsUnknownWhenNeitherTheSegmentNorTheProfileSetsAMinimum()
    {
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Floor with
        {
            EmployeeEstimate = 3,
            ProfileMinEmployees = null,
            SegmentMinEmployees = null,
        });

        breakdown.Of(ScoreFeatures.SizeFit).Value.ShouldBe(
            0.5,
            Tolerance,
            "with no minimum to compare against there is no 'below', so §7.6's 'unknown = 0.5' is the only "
            + $"honest answer: a three-person company is not evidence of a bad fit. {breakdown.Describe()}");
        breakdown.SizeMinimum.ShouldBe(
            new SizeMinimum(null, SizeMinimumSources.None),
            breakdown.Describe());
    }

    [Theory]
    [InlineData(FacilityFitLevels.High, 1.0)]
    [InlineData(FacilityFitLevels.Medium, 0.75)]
    [InlineData(FacilityFitLevels.Low, 0.5)]
    [InlineData(FacilityFitLevels.Unknown, 0.4)]
    public void FacilityFit_UsesTheResearchLevelWhenThereIsOne(string level, double expected)
    {
        var breakdown = ScoringFixtures.Scorer().Score(
            ScoringFixtures.Floor with { ResearchFacilityLevel = level });

        breakdown.Of(ScoreFeatures.FacilityFit).Value.ShouldBe(
            expected,
            Tolerance,
            "§7.6 facilityFit: 'Research wins when present: facilityFit.level high = 1.0, medium = 0.75, "
            + $"low = 0.5, unknown = 0.4'. {breakdown.Describe()}");
    }

    [Theory]
    [InlineData(0, 0.2)]
    [InlineData(1, 0.5)]
    [InlineData(2, 0.75)]
    [InlineData(3, 1.0)]
    [InlineData(7, 1.0)]
    [InlineData(null, 0.4)]
    public void FacilityFit_FallsBackToTheWebsiteKeywordCount(int? keywords, double expected)
    {
        // The fallback path, which stays unreachable in practice until chunk C7 fetches a website: in C6
        // the count is always null, which is the "no website text = 0.4" band.
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Floor with
        {
            ResearchFacilityLevel = null,
            FacilityKeywordCount = keywords,
        });

        breakdown.Of(ScoreFeatures.FacilityFit).Value.ShouldBe(
            expected,
            Tolerance,
            "§7.6 facilityFit: 'Otherwise from distinct facility keywords in the website text: 0 = 0.2, "
            + $"1 = 0.5, 2 = 0.75, ≥ 3 = 1.0; no website text = 0.4'. {breakdown.Describe()}");
    }

    [Fact]
    public void FacilityFit_PrefersALowResearchLevelOverAnyNumberOfWebsiteKeywords()
    {
        // The ruling §7.6 now states, in the direction that proves it: research that says the fit is
        // poor must not be overruled by five keywords scraped off a marketing page. The other direction
        // is pinned by worked example 3 - without the research level it scores 59 (tier C) instead of 71.
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Floor with
        {
            ResearchFacilityLevel = FacilityFitLevels.Low,
            FacilityKeywordCount = 5,
        });

        breakdown.Of(ScoreFeatures.FacilityFit).Value.ShouldBe(
            0.5,
            Tolerance,
            "§7.6: 'Research wins when present'. Five keywords would score 1.0 on the fallback path, so "
            + $"a 1.0 here means the precedence is the wrong way round. {breakdown.Describe()}");
    }

    [Fact]
    public void Signals_CountOnlyTheBuyingTypesSection76Lists()
    {
        var scorer = ScoringFixtures.Scorer();

        foreach (var type in SignalTypes.Buying)
        {
            var breakdown = scorer.Score(ScoringFixtures.Floor with
            {
                Signals = [new SignalFact(type, "2026-09")],
            });

            breakdown.Of(ScoreFeatures.Signals).Value.ShouldBe(
                0.6,
                Tolerance,
                $"§7.6 names '{type}' a buying signal, so one of them scores 0.6. Note the sample "
                + "profile's own 'signals' list omits 'funding'; §7.6's list is the one that scores "
                + $"(see the C6 report). {breakdown.Describe()}");
        }

        foreach (var type in new[] { SignalTypes.Registry, SignalTypes.Other })
        {
            var breakdown = scorer.Score(ScoringFixtures.Floor with
            {
                Signals = [new SignalFact(type, "2026-09"), new SignalFact(type, "2026-08")],
            });

            breakdown.Of(ScoreFeatures.Signals).Value.ShouldBe(
                0d,
                Tolerance,
                $"§7.6 excludes '{type}': a registry entry says a company exists, not that it is about "
                + "to buy. Worked example 3 turns on this - its only signal is a registry row, and "
                + $"counting it would move it from tier B to tier A. {breakdown.Describe()}");
        }
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(1, 0.6)]
    [InlineData(2, 1.0)]
    [InlineData(5, 1.0)]
    public void Signals_ScoreZeroThenSixTenthsThenOne(int count, double expected)
    {
        var signals = Enumerable.Range(0, count)
            .Select(index => new SignalFact(SignalTypes.Permit, $"2026-0{(index % 9) + 1}"))
            .ToList();

        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Floor with { Signals = signals });

        breakdown.Of(ScoreFeatures.Signals).Value.ShouldBe(
            expected,
            Tolerance,
            $"§7.6 signals: '0 = 0, 1 = 0.6, ≥ 2 = 1.0'. {breakdown.Describe()}");
    }

    [Theory]
    [InlineData(null, 0.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(24.9, 1.0)]
    [InlineData(25.0, 1.0)]
    [InlineData(25.1, 0.6)]
    [InlineData(50.0, 0.6)]
    [InlineData(50.1, 0.3)]
    [InlineData(500.0, 0.3)]
    public void Proximity_BandsOnTheDistanceToTheAssignedBranch(double? miles, double expected)
    {
        var breakdown = ScoringFixtures.Scorer().Score(
            ScoringFixtures.Floor with { BranchDistanceMiles = miles });

        breakdown.Of(ScoreFeatures.Proximity).Value.ShouldBe(
            expected,
            Tolerance,
            "§7.6 proximity: '≤ 25 mi = 1.0, ≤ 50 mi = 0.6, farther = 0.3, no dealer = 0'. A null "
            + "distance is a lead with assignment=gap, which no dealer would ever be handed. "
            + breakdown.Describe());
    }

    [Theory]
    [InlineData(0.95, false, 0.665)]
    [InlineData(0.95, true, 0.965)]
    [InlineData(0.9, false, 0.63)]
    [InlineData(1.0, true, 1.0)]
    [InlineData(0.0, false, 0.0)]
    public void Confidence_IsSevenTenthsOvertureAndThreeTenthsAReachableWebsite(
        double overture,
        bool reachable,
        double expected)
    {
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Floor with
        {
            OvertureConfidence = overture,
            WebsiteReachable = reachable,
        });

        breakdown.Of(ScoreFeatures.Confidence).Value.ShouldBe(
            expected,
            Tolerance,
            "§7.6 confidence: '0.7 × Overture confidence + 0.3 × (website reachable)'. Before chunk C7 "
            + $"nothing has been fetched, so the second term is always 0. {breakdown.Describe()}");
    }

    [Fact]
    public void TheWeightedSum_IsRoundedToAWholeScore()
    {
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Mixed);

        breakdown.BaseScore.ShouldBe(
            69,
            "0.25×0.8 + 0.15×0.5 + 0.20×0.75 + 0.25×0.6 + 0.05×0.6 + 0.10×0.86 = 0.691 → 69.1 → 69. "
            + breakdown.Describe());
        breakdown.Score.ShouldBe(69, breakdown.Describe());
        breakdown.Tier.ShouldBe(LeadTiers.B, breakdown.Describe());
    }

    [Fact]
    public void WithNoBuyingSignalTheBaseCannotPassSeventyFive_SoTierARequiresCitedEvidence()
    {
        // The design's load-bearing property. It is derived from the weights rather than read from
        // LeadTiers.BaseCeilingWithoutSignals, so a future weight tweak that lifted the ceiling to 80 -
        // and quietly made tier A reachable with no evidence at all - fails here rather than passing.
        var weights = ScoringWeights.Default;
        var ceiling = (int)Math.Round(100 * (weights.Sum - weights.Signals), MidpointRounding.AwayFromZero);

        ceiling.ShouldBe(
            75,
            "every feature except signals at 1.0 sums to 0.75 under §7.6's default weights: "
            + $"0.25 + 0.15 + 0.20 + 0.05 + 0.10. Got {ceiling} from {weights}.");
        ceiling.ShouldBeLessThan(
            LeadTiers.MinimumA,
            "§7.6: 'Without research signals the base maxes out at 75, so tier A requires cited "
            + "evidence, which is intentional.' If this ever passes, that sentence is no longer true "
            + "and nobody would notice from the outside.");
        LeadTiers.BaseCeilingWithoutSignals.ShouldBe(ceiling, "the documented constant must match the weights.");

        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.MaximumWithoutSignals);

        breakdown.BaseScore.ShouldBe(75, breakdown.Describe());
        breakdown.Score.ShouldBe(75, breakdown.Describe());
        breakdown.CappedFrom.ShouldBeNull(
            $"the cap did not need to bind: 75 is already inside tier B. {breakdown.Describe()}");
        breakdown.Tier.ShouldBe(
            LeadTiers.B,
            $"75 is below §7.6's tier A threshold of {LeadTiers.MinimumA}. {breakdown.Describe()}");
    }

    [Theory]
    [InlineData(20, 15, 90)]
    [InlineData(15, 15, 90)]
    [InlineData(5, 5, 80)]
    public void APositiveAdjustmentCannotCarryALeadWithNoQualifyingSignalPastTierB(
        int submitted,
        int applied,
        int uncapped)
    {
        // The guard §7.6 now enforces rather than merely asserting. The base ceiling of 75 is only
        // arithmetic; llmAdjustment is applied afterwards, so the one input a person can nudge would
        // defeat it - +5 reaches 80 and +15 reaches 90, tier A on no cited evidence whatsoever, through
        // precisely the subjective mechanism the guard exists to contain.
        var breakdown = ScoringFixtures.Scorer().Score(
            ScoringFixtures.MaximumWithoutSignals with { LlmAdjustment = submitted });

        breakdown.BaseScore.ShouldBe(75, breakdown.Describe());
        breakdown.LlmAdjustment.ShouldBe(
            applied,
            $"the ±15 clamp still applies before the cap. {breakdown.Describe()}");
        breakdown.Score.ShouldBe(
            LeadTiers.ScoreCapWithoutSignals,
            $"§7.6: 'a lead with zero qualifying buying signals is capped at 79 (the top of tier B) "
            + $"however large its adjustment'. {breakdown.Describe()}");
        breakdown.Tier.ShouldBe(LeadTiers.B, breakdown.Describe());
        breakdown.CappedFrom.ShouldBe(
            uncapped,
            "§7.6: the breakdown 'records when the cap bound, so a capped score is explainable rather "
            + $"than merely lower than expected'. {breakdown.Describe()}");
    }

    [Fact]
    public void OneQualifyingSignalIsEnoughToClearTierAHonestly()
    {
        // The other side of the boundary, which is what makes the cap a guard rather than a ceiling: the
        // way to tier A is cited evidence, and one piece of it is enough.
        var scorer = ScoringFixtures.Scorer();

        var capped = scorer.Score(ScoringFixtures.MaximumWithoutSignals with { LlmAdjustment = 15 });
        var earned = scorer.Score(ScoringFixtures.MaximumWithOneSignal);

        capped.Score.ShouldBe(LeadTiers.ScoreCapWithoutSignals, capped.Describe());
        capped.Tier.ShouldBe(LeadTiers.B, capped.Describe());

        earned.BaseScore.ShouldBe(
            90,
            $"0.75 + 0.25×0.6 = 0.90. {earned.Describe()}");
        earned.Score.ShouldBe(90, earned.Describe());
        earned.CappedFrom.ShouldBeNull(
            $"one signal inside the window is a qualifying signal. {earned.Describe()}");
        earned.Tier.ShouldBe(
            LeadTiers.A,
            $"and 90 ≥ {LeadTiers.MinimumA}, with no adjustment needed at all. {earned.Describe()}");
    }

    [Fact]
    public void ANegativeAdjustmentAppliesInFullEvenWithNoQualifyingSignal()
    {
        // §7.6: "negative adjustments always apply in full". The cap only ever lowers a score; reading it
        // as "clamp to 79" would raise a lead the researcher deliberately marked down.
        var breakdown = ScoringFixtures.Scorer().Score(
            ScoringFixtures.MaximumWithoutSignals with { LlmAdjustment = -15 });

        breakdown.Score.ShouldBe(60, $"75 − 15 = 60. {breakdown.Describe()}");
        breakdown.CappedFrom.ShouldBeNull($"nothing was capped. {breakdown.Describe()}");
        breakdown.Tier.ShouldBe(LeadTiers.C, breakdown.Describe());
    }

    [Theory]
    [InlineData(SignalTypes.Registry, "2026-09", "a registry entry is not a buying signal")]
    [InlineData(SignalTypes.Permit, "2024-01", "the permit is outside the twelve-month window")]
    [InlineData(SignalTypes.Permit, "2026-11", "the permit is dated in the future")]
    public void ASignalThatDoesNotQualifyDoesNotLiftTheCapEither(string type, string date, string why)
    {
        // "Zero qualifying buying signals" is about what §7.6 credits, not about whether the research
        // carries rows. A lead with a registry entry, a stale permit or a mistyped future date has cited
        // nothing the scorer accepts, so the guard still applies.
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.MaximumWithoutSignals with
        {
            Signals = [new SignalFact(type, date)],
            LlmAdjustment = 15,
        });

        breakdown.Of(ScoreFeatures.Signals).Value.ShouldBe(0d, Tolerance, $"{why}. {breakdown.Describe()}");
        breakdown.Score.ShouldBe(
            LeadTiers.ScoreCapWithoutSignals,
            $"{why}, so the cap binds. {breakdown.Describe()}");
        breakdown.CappedFrom.ShouldBe(90, breakdown.Describe());
    }

    [Fact]
    public void AMidpointRoundsAwayFromZero_NotToTheEvenNumber()
    {
        // §7.6 pins the midpoint rule because .NET's Math.Round default is banker's rounding. This feature
        // set is the one whose weighted sum is an exact IEEE-754 midpoint under the default weights:
        // 100 × Σ is exactly 64.5, where away-from-zero gives 65 (tier B) and banker's gives 64 (tier C).
        // The two modes disagree on the tier, which is the whole reason the rule had to be stated.
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.ExactMidpoint);

        var sum = breakdown.Features.Sum(row => row.Contribution);
        (100 * sum).ShouldBe(
            64.5,
            1e-9,
            "0.25×1.0 + 0.15×0.1 + 0.20×1.0 + 0.25×0.6 + 0.05×0 + 0.10×0.3 = 0.645. "
            + breakdown.Describe());

        Math.Round(64.5, MidpointRounding.ToEven).ShouldBe(
            64,
            "banker's rounding - .NET's default - would send this score to the tier below.");

        breakdown.BaseScore.ShouldBe(
            65,
            $"§7.6: rounding is away from zero at a midpoint. {breakdown.Describe()}");
        breakdown.Tier.ShouldBe(
            LeadTiers.B,
            $"65 is exactly §7.6's tier B threshold of {LeadTiers.MinimumB}. {breakdown.Describe()}");

        LeadScorer.Rounding.ShouldBe(
            MidpointRounding.AwayFromZero,
            "the documented constant must match the rule the scorer applies.");
    }

    [Fact]
    public void EveryFeatureAtItsMaximumIncludingSignals_ScoresOneHundred()
    {
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Maximum);

        breakdown.BaseScore.ShouldBe(100, breakdown.Describe());
        breakdown.Tier.ShouldBe(LeadTiers.A, breakdown.Describe());
    }

    [Theory]
    [InlineData(20, 15, 84)]
    [InlineData(15, 15, 84)]
    [InlineData(5, 5, 74)]
    [InlineData(0, 0, 69)]
    [InlineData(-15, -15, 54)]
    [InlineData(-20, -15, 54)]
    public void TheLlmAdjustment_IsClampedToFifteenEitherWay(int submitted, int applied, int expected)
    {
        // §7.6: clamp(llmAdjustment, −15, +15). schemas/research.schema.json caps it at ±15 too, so the
        // clamp is the second line of defence - for a weights override, a stored value, or a research
        // document that reached the scorer by another route.
        //
        // Scored on the mixed set (base 69) rather than on the 75 ceiling, because that one carries no
        // qualifying signal and §7.6's tier-A cap would hide the clamp behind it. The mixed set has a
        // signal, so the arithmetic here is only the clamp.
        var breakdown = ScoringFixtures.Scorer().Score(
            ScoringFixtures.Mixed with { LlmAdjustment = submitted });

        breakdown.LlmAdjustment.ShouldBe(
            applied,
            $"the breakdown reports the adjustment that was applied, not the one submitted. {breakdown.Describe()}");
        breakdown.Score.ShouldBe(expected, $"base 69 {applied:+0;-0;+0}. {breakdown.Describe()}");
        breakdown.CappedFrom.ShouldBeNull(
            $"the mixed set cites a permit inside the window, so the cap never applies. {breakdown.Describe()}");
    }

    [Fact]
    public void TheFinalScore_IsClampedToZeroAndOneHundred()
    {
        var scorer = ScoringFixtures.Scorer();

        var lowest = scorer.Score(new LeadFeatures
        {
            SegmentMatch = SegmentMatch.None,
            EmployeeEstimate = 1,
            ProfileMinEmployees = ScoringFixtures.ProfileMinEmployees,
            FacilityKeywordCount = 0,
            OvertureConfidence = 0.4d,
            LlmAdjustment = -15,
        });

        lowest.BaseScore.ShouldBe(
            13,
            "0.25×0.2 + 0.15×0.1 + 0.20×0.2 + 0 + 0 + 0.10×0.28 = 0.133 → 13.3 → 13. " + lowest.Describe());
        lowest.Score.ShouldBe(
            0,
            $"13 − 15 = −2, and §7.6 clamps the result to 0–100. {lowest.Describe()}");
        lowest.Tier.ShouldBe(LeadTiers.C, lowest.Describe());

        var highest = scorer.Score(ScoringFixtures.Maximum with { LlmAdjustment = 15 });

        highest.Score.ShouldBe(
            100,
            $"100 + 15 = 115, clamped to 100. {highest.Describe()}");
    }

    [Theory]
    [InlineData(0, LeadTiers.C)]
    [InlineData(64, LeadTiers.C)]
    [InlineData(65, LeadTiers.B)]
    [InlineData(79, LeadTiers.B)]
    [InlineData(80, LeadTiers.A)]
    [InlineData(100, LeadTiers.A)]
    public void TheTierThresholdsAreInclusiveAtEightyAndSixtyFive(int score, string tier) =>
        LeadTiers.For(score).ShouldBe(tier, "§7.6: 'Tiers: A ≥ 80, B ≥ 65, C < 65.'");

    [Fact]
    public void TheBreakdownExplainsTheScore_FeatureByFeatureWithItsWeightAndContribution()
    {
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Mixed);

        breakdown.Features.Select(row => row.Feature).ShouldBe(
            ScoreFeatures.All,
            ignoreOrder: true,
            "§7.6: 'score_breakdown_json stores each feature, weight and contribution so Claude can "
            + $"explain any score.' {breakdown.Describe()}");

        foreach (var row in breakdown.Features)
        {
            // Within the 4-decimal granularity §7.6 stores these at, not to the last bit: both halves of
            // the product are rounded, so demanding exact equality would be demanding that no rounding
            // happened. A contribution that is actually wrong is out by far more than 1e-4.
            row.Contribution.ShouldBe(
                row.Value * row.Weight,
                1e-4,
                $"'{row.Feature}' contribution has to be value × weight, or the breakdown does not add "
                + $"up to the score it explains. {breakdown.Describe()}");
        }

        var total = breakdown.Features.Sum(row => row.Contribution);
        ((int)Math.Round(100 * total, MidpointRounding.AwayFromZero)).ShouldBe(
            breakdown.BaseScore,
            $"the contributions must sum to the base score. {breakdown.Describe()}");

        var weights = ScoringWeights.Default;
        breakdown.Of(ScoreFeatures.SegmentFit).Weight.ShouldBe(weights.SegmentFit, Tolerance);
        breakdown.Of(ScoreFeatures.SizeFit).Weight.ShouldBe(weights.SizeFit, Tolerance);
        breakdown.Of(ScoreFeatures.FacilityFit).Weight.ShouldBe(weights.FacilityFit, Tolerance);
        breakdown.Of(ScoreFeatures.Signals).Weight.ShouldBe(weights.Signals, Tolerance);
        breakdown.Of(ScoreFeatures.Proximity).Weight.ShouldBe(weights.Proximity, Tolerance);
        breakdown.Of(ScoreFeatures.Confidence).Weight.ShouldBe(weights.Confidence, Tolerance);
    }

    [Fact]
    public void TheBreakdownRoundsItsNumbersToFourDecimalsWithoutMovingTheScore()
    {
        // §7.6: "Stored feature values and contributions are rounded to 4 decimal places ... The score
        // itself is never computed from the rounded values." These two features are the pair the manual
        // check actually tripped over: facilityFit 'unknown' gives 0.20 × 0.4, which a double renders as
        // 0.08000000000000002, and an Overture confidence of 0.77 gives 0.10 × (0.7 × 0.77), which renders
        // as 0.053899999999999997. Neither explains anything to a reader.
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Floor with
        {
            ResearchFacilityLevel = FacilityFitLevels.Unknown,
            OvertureConfidence = 0.77d,
        });

        foreach (var row in breakdown.Features)
        {
            row.Value.ShouldBe(
                Math.Round(row.Value, 4, MidpointRounding.AwayFromZero),
                $"'{row.Feature}' value is {row.Value:R}. {breakdown.Describe()}");
            row.Contribution.ShouldBe(
                Math.Round(row.Contribution, 4, MidpointRounding.AwayFromZero),
                $"'{row.Feature}' contribution is {row.Contribution:R}. {breakdown.Describe()}");
        }

        breakdown.Of(ScoreFeatures.FacilityFit).Contribution.ShouldBe(
            0.08,
            Tolerance,
            $"not 0.08000000000000002. {breakdown.Describe()}");
        breakdown.Of(ScoreFeatures.Confidence).Value.ShouldBe(
            0.539,
            Tolerance,
            $"0.7 × 0.77, rounded. {breakdown.Describe()}");
        breakdown.Of(ScoreFeatures.Confidence).Contribution.ShouldBe(
            0.0539,
            Tolerance,
            $"not 0.053899999999999997. {breakdown.Describe()}");

        // And the score is unchanged: 0.05 + 0.075 + 0.08 + 0 + 0 + 0.0539 = 0.2589 → 25.89 → 26.
        // Rounding is for the reader; it must not reach the arithmetic.
        breakdown.BaseScore.ShouldBe(26, breakdown.Describe());
        breakdown.Score.ShouldBe(26, breakdown.Describe());
    }

    [Fact]
    public void TheBreakdownRecordsTheInstantItWasScoredAt()
    {
        // Without it a stored score cannot be explained: the signals row depends on when the scorer ran,
        // so "signals = 0" is unexplainable six months later unless the breakdown says as of when.
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Maximum);

        breakdown.AsOf.ShouldBe(
            FixedTimeProvider.ScoringReference,
            "the breakdown's asOf is the reference instant for §7.6's twelve-month signal window, read "
            + $"from the injected TimeProvider. {breakdown.Describe()}");
    }

    [Fact]
    public void WeightsPassedToTheScorerOverrideTheDefaults_AndAreReportedInTheBreakdown()
    {
        var weights = new ScoringWeights(0.1, 0.1, 0.1, 0.5, 0.1, 0.1);
        weights.Sum.ShouldBe(1.0, ScoringWeights.Tolerance, "the override itself has to be a valid set.");

        var breakdown = ScoringFixtures.Scorer(weights).Score(ScoringFixtures.Mixed);

        breakdown.Of(ScoreFeatures.Signals).Weight.ShouldBe(
            0.5,
            Tolerance,
            $"§7.6: 'Weights can be overridden in the profile (scoringWeights).' {breakdown.Describe()}");
        breakdown.BaseScore.ShouldBe(
            65,
            "0.1×0.8 + 0.1×0.5 + 0.1×0.75 + 0.5×0.6 + 0.1×0.6 + 0.1×0.86 = 0.651 → 65.1 → 65, where "
            + $"the default weights gave 69. {breakdown.Describe()}");
        breakdown.Tier.ShouldBe(LeadTiers.B, breakdown.Describe());
    }

    [Fact]
    public void TheSameFeaturesScoreTheSameEveryTime()
    {
        var scorer = ScoringFixtures.Scorer();

        var first = scorer.Score(ScoringFixtures.Mixed);
        var second = scorer.Score(ScoringFixtures.Mixed);

        // Described rather than compared as records: the breakdown holds a list, and record equality
        // over a list member is reference equality, so two correct calls would never compare equal.
        second.Describe().ShouldBe(
            first.Describe(),
            "§7.6 is deterministic, and NFR-3 makes re-running score_leads safe.");
    }
}
