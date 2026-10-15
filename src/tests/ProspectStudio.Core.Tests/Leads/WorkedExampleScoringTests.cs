using ProspectStudio.Core.Leads;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Leads;

/// <summary>
/// Implementation-plan C6: "the three worked examples in docs/03 §8, with fixture research, land in
/// tiers A, A, B."
/// </summary>
/// <remarks>
/// <para>
/// <strong>Tiers are the contract, not the published scores.</strong> docs/03 §8's 92 / 88 / 71 are
/// marked illustrative, and only the 71 reproduces: examples 1 and 2 score 100 and 86 under §7.6's
/// arithmetic, whichever way <c>facilityFit</c> is read. Their scores are pinned here as regression
/// guards with the arithmetic written out, not as spec quotations.
/// </para>
/// <para>
/// These features are assembled in the test from the two fixtures (the places row and the research
/// document), which mirrors what the production <c>FeatureExtractor</c> must produce without replacing
/// it. The extractor and scorer together, over a real campaign, are covered by the <c>score_leads</c>
/// tests in ProspectStudio.Mcp.Tests, which assert the same three tiers.
/// </para>
/// </remarks>
public class WorkedExampleScoringTests
{
    public static TheoryData<string> Examples() => WorkedExamples.Numbers();

    [Theory]
    [MemberData(nameof(Examples))]
    public void TheWorkedExampleLandsInTheTierTheStrategyDocumentGivesIt(string placeId)
    {
        var example = WorkedExamples.ByPlaceId(placeId);
        var breakdown = ScoringFixtures.Scorer().Score(example.Features);

        breakdown.Tier.ShouldBe(
            example.ExpectedTier,
            $"docs/03 §8 puts {example} in tier {example.ExpectedTier}. "
            + $"src/tests/Fixtures/research/README.md has the arithmetic. {breakdown.Describe()}");
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void TheWorkedExampleScoresWhatSection76ArithmeticProduces(string placeId)
    {
        var example = WorkedExamples.ByPlaceId(placeId);
        var breakdown = ScoringFixtures.Scorer().Score(example.Features);

        var provenance = example.FromTheDocument
            ? "this is the figure docs/03 §8 itself publishes"
            : "docs/03 §8's figure for this example is illustrative and does not reproduce; this is what "
              + "§7.6's arithmetic gives";

        breakdown.Score.ShouldBe(
            example.ExpectedScore,
            $"{example}: {provenance}. {breakdown.Describe()}");
    }

    [Fact]
    public void WestparkReachesItsDocumentedSeventyOneOnlyBecauseResearchOutranksKeywords()
    {
        // The arithmetic that settled §7.6's facilityFit ruling, both ways round, so neither reading can
        // be swapped back in without a red test naming the number that moved.
        var example = WorkedExamples.WestparkMetalFab;
        var scorer = ScoringFixtures.Scorer();

        var withResearch = scorer.Score(example.Features);

        withResearch.Score.ShouldBe(
            71,
            "0.25 + 0.15 + 0.20 + 0 + 0.05 + 0.10×0.63 = 0.713 → 71.3 → 71, which is exactly the figure "
            + $"docs/03 §8 publishes for Westpark Metal Fab. {withResearch.Describe()}");
        withResearch.Tier.ShouldBe(LeadTiers.B, withResearch.Describe());

        var fromKeywordsOnly = scorer.Score(example.Features with { ResearchFacilityLevel = null });

        fromKeywordsOnly.Score.ShouldBe(
            WorkedExamples.WestparkScoreFromTheKeywordPath,
            "ignoring facilityFit.level and falling back to 'no website text = 0.4' gives "
            + "0.713 − 0.20 + 0.08 = 0.593 → 59, a tier C, which contradicts docs/03 §8. That is the "
            + $"conflict §7.6's ruling resolves. {fromKeywordsOnly.Describe()}");
        fromKeywordsOnly.Tier.ShouldBe(LeadTiers.C, fromKeywordsOnly.Describe());
    }

    [Fact]
    public void BayouIsClampedAtOneHundredRatherThanReportingOneHundredAndTwo()
    {
        var breakdown = ScoringFixtures.Scorer().Score(WorkedExamples.BayouFulfillment.Features);

        breakdown.BaseScore.ShouldBe(
            97,
            "0.9665 → 96.65 → 97, before the research document's llmAdjustment of +5. "
            + breakdown.Describe());
        breakdown.LlmAdjustment.ShouldBe(5, breakdown.Describe());
        breakdown.Score.ShouldBe(100, $"§7.6 clamps 102 to 100. {breakdown.Describe()}");
    }

    [Fact]
    public void WestparkHasNoQualifyingSignalYetIsNotCapped_BecauseItNeverReachesTheCap()
    {
        // §7.6's tier-A cap lowers a score; it is not a blanket applied to every lead without evidence.
        // Westpark is the case that tells the two apart: its only signal is a registry row, so it has zero
        // qualifying signals, but 71 is already inside tier B and nothing should be taken off it. An
        // implementation that set the score to 79 whenever signals were 0 would raise this example.
        var breakdown = ScoringFixtures.Scorer().Score(WorkedExamples.WestparkMetalFab.Features);

        breakdown.Of(ScoreFeatures.Signals).Value.ShouldBe(
            0d,
            1e-9,
            $"a registry entry is not a buying signal. {breakdown.Describe()}");
        breakdown.Score.ShouldBe(71, breakdown.Describe());
        breakdown.CappedFrom.ShouldBeNull(
            $"71 is below the cap of {LeadTiers.ScoreCapWithoutSignals}, so nothing was held back. "
            + breakdown.Describe());
    }

    [Fact]
    public void WestparkWouldJumpToTierAIfItsRegistrySignalWereCountedAsABuyingSignal()
    {
        // Not a requirement - a demonstration of why §7.6 excludes registry, kept next to the example it
        // would break, so the exclusion reads as load-bearing rather than arbitrary.
        var example = WorkedExamples.WestparkMetalFab;

        var asIs = ScoringFixtures.Scorer().Score(example.Features);
        var withTheRegistryRowTreatedAsBuying = ScoringFixtures.Scorer().Score(
            example.Features with { Signals = [new SignalFact(SignalTypes.Permit, "2026-03")] });

        asIs.Tier.ShouldBe(LeadTiers.B, asIs.Describe());
        withTheRegistryRowTreatedAsBuying.Tier.ShouldBe(
            LeadTiers.A,
            "0.713 + 0.25×0.6 = 0.863 → 86. So counting the OSHA establishment record as a buying signal "
            + $"moves the example a whole tier. {withTheRegistryRowTreatedAsBuying.Describe()}");
    }
}
