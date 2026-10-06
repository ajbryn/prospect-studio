using System.Text.Json;
using ProspectStudio.Core.Leads;
using ProspectStudio.Core.SearchProfiles;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Leads;

/// <summary>
/// The <c>scoringWeights</c> sum rule, which chunk C1 added to <c>save_search_profile</c> and chunk C6
/// has to apply again when <c>score_leads</c> is handed weights directly: a profile is not the only way
/// in, and a set summing to 0.6 would quietly score every lead a third low.
/// </summary>
public class ScoringWeightsTests
{
    private const string Pointer = "/weights";

    [Fact]
    public void TheDefaultsAreTheWeightsSection76Tabulates()
    {
        var weights = ScoringWeights.Default;

        weights.SegmentFit.ShouldBe(0.25);
        weights.SizeFit.ShouldBe(0.15);
        weights.FacilityFit.ShouldBe(0.20);
        weights.Signals.ShouldBe(0.25);
        weights.Proximity.ShouldBe(0.05);
        weights.Confidence.ShouldBe(0.10);

        weights.Sum.ShouldBe(
            1.0,
            ScoringWeights.Tolerance,
            "§7.6: 'Weights can be overridden in the profile (scoringWeights) and must sum to 1.0.'");
    }

    [Fact]
    public void TheSampleProfileUsesTheDefaults()
    {
        // Not decoration: every expected score in the C6 tests is computed from §7.6's defaults, and the
        // tool tests score a campaign whose saved profile is this file. If the fixture ever carried a
        // different set, the two halves of the suite would be measuring different things.
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFixtures.SampleSearchProfile));

        var weights = ScoringWeights.FromProfile(document.RootElement);

        weights.ShouldBe(
            ScoringWeights.Default,
            "poc/fixtures/sample-search-profile.json carries §7.6's default weights.");
    }

    [Fact]
    public void AProfileWithNoWeightsFallsBackToTheDefaults()
    {
        using var document = JsonDocument.Parse("""{"version":1,"name":"No weights"}""");

        ScoringWeights.FromProfile(document.RootElement).ShouldBe(ScoringWeights.Default);
    }

    [Fact]
    public void NoWeightsArgumentOnScoreLeadsMeansTheProfileOrTheDefaults() =>
        ScoringWeights.FromJson(null).ShouldBe(
            ScoringWeights.Default,
            "mcp-tools.md §score_leads: 'weights: null'.");

    [Theory]
    [InlineData("""{"segmentFit":0.25,"sizeFit":0.15,"facilityFit":0.20,"signals":0.25,"proximity":0.05,"confidence":0.10}""", true)]
    [InlineData("""{"segmentFit":0.2505,"sizeFit":0.15,"facilityFit":0.20,"signals":0.25,"proximity":0.05,"confidence":0.10}""", true)]
    [InlineData("""{"segmentFit":0.30,"sizeFit":0.15,"facilityFit":0.20,"signals":0.25,"proximity":0.05,"confidence":0.10}""", false)]
    [InlineData("""{"segmentFit":0.20,"sizeFit":0.15,"facilityFit":0.20,"signals":0.20,"proximity":0.05,"confidence":0.10}""", false)]
    [InlineData("""{"signals":1.0}""", true)]
    [InlineData("""{"signals":0.9}""", false)]
    public void TheWeightsMustSumToOneWithinATenthOfAPercent(string json, bool valid)
    {
        using var document = JsonDocument.Parse(json);

        var problems = ScoringWeights.Validate(document.RootElement, Pointer);

        if (valid)
        {
            problems.ShouldBeEmpty(
                $"these sum to within ±{ScoringWeights.Tolerance} of 1.0. Reported: {Describe(problems)}");
            return;
        }

        var problem = problems.ShouldHaveSingleItem(
            $"one rule, one problem. Reported: {Describe(problems)}");

        problem.Pointer.ShouldBe(
            Pointer,
            "the problem is reported at the object the caller passed, so score_leads says '/weights' and "
            + "save_search_profile says '/scoringWeights'. A hard-coded '/scoringWeights' would point a "
            + "score_leads caller at a field they never sent.");
    }

    [Theory]
    [InlineData("""{"signals":0.5,"signalz":0.5}""", "signalz")]
    [InlineData("""{"segmentFit":0.25,"sizeFit":0.15,"facilityFit":0.20,"signals":0.25,"proximity":0.05,"confidenc":0.10}""", "confidenc")]
    [InlineData("""{"segmentFit":0.5,"segment_fit":0.5}""", "segment_fit")]
    public void AnUnknownWeightKeyIsRejectedAndNamed(string json, string offender)
    {
        // The sum rule cannot catch a typo. Every one of these sums to exactly 1.0, so without a
        // known-keys check the misspelling resolves to nothing, the feature it was meant for keeps a
        // weight of 0, and every lead in the campaign is scored on a scale the caller never asked for -
        // silently. A profile is protected by search-profile.schema.json; score_leads.weights has no
        // schema at all, so this is the only thing standing in front of it.
        using var document = JsonDocument.Parse(json);

        SumOf(document.RootElement).ShouldBe(
            1.0,
            ScoringWeights.Tolerance,
            "the point of this case is that the sum rule is satisfied, so only a known-keys check can "
            + $"reject it: {json}");

        var problems = ScoringWeights.Validate(document.RootElement, Pointer);

        problems.ShouldNotBeEmpty($"'{offender}' is not one of §7.6's six features. Reported: {Describe(problems)}");

        problems.ShouldContain(
            problem => problem.Message.Contains(offender, StringComparison.Ordinal)
                || problem.Pointer.Contains(offender, StringComparison.Ordinal),
            $"the caller mistyped one key and needs to be told which. 'The weights are not valid' leaves "
            + $"them comparing six names by eye. Reported: {Describe(problems)}");

        problems.ShouldAllBe(
            problem => problem.Pointer == Pointer || problem.Pointer == $"{Pointer}/{offender}",
            $"the problem belongs to the weights object the caller sent, or to the offending key inside "
            + $"it. Reported: {Describe(problems)}");
    }

    [Theory]
    [InlineData("""{"segmentFit":2.0,"sizeFit":0.15,"facilityFit":0.20,"signals":-1.0,"proximity":0.05,"confidence":-0.4}""", "segmentFit")]
    [InlineData("""{"signals":1.5,"proximity":-0.5}""", "signals")]
    [InlineData("""{"segmentFit":-0.5,"signals":1.5}""", "segmentFit")]
    public void AWeightOutsideZeroToOneIsRejectedAndNamed(string json, string offender)
    {
        // A weight is a share of the score, so 2.0 and −1.0 are not weights. The sum rule cannot catch
        // either: any set containing a weight above 1 has to contain a negative one to reach 1.0, which is
        // exactly how these are built. §7.6's features are then scored on a scale where one of them counts
        // double and another counts against the lead, and nothing in the response says so.
        using var document = JsonDocument.Parse(json);

        SumOf(document.RootElement).ShouldBe(
            1.0,
            ScoringWeights.Tolerance,
            $"the point of this case is that the sum rule is satisfied: {json}");

        var problems = ScoringWeights.Validate(document.RootElement, Pointer);

        problems.ShouldNotBeEmpty(
            $"a weight outside 0–1 is not a share of anything. Reported: {Describe(problems)}");
        problems.ShouldContain(
            problem => problem.Message.Contains(offender, StringComparison.Ordinal)
                || problem.Pointer.Contains(offender, StringComparison.Ordinal),
            $"'{offender}' is the one out of range, and the caller has six numbers to check by hand "
            + $"otherwise. Reported: {Describe(problems)}");
    }

    [Theory]
    [InlineData("""{"segmentFit":"0.25","sizeFit":0.15,"facilityFit":0.20,"signals":0.25,"proximity":0.05,"confidence":0.10}""", "segmentFit")]
    [InlineData("""{"segmentFit":null,"sizeFit":0.15,"facilityFit":0.20,"signals":0.25,"proximity":0.05,"confidence":0.10}""", "segmentFit")]
    [InlineData("""{"segmentFit":true,"sizeFit":0.15,"facilityFit":0.20,"signals":0.25,"proximity":0.05,"confidence":0.10}""", "segmentFit")]
    public void ANonNumericWeightIsRejectedAndNamed(string json, string offender)
    {
        // The branch with no test at all: a string, a null or a boolean where a number belongs. Reading it
        // as 0 and letting the sum rule complain would blame the wrong thing - the caller would be told
        // their weights sum to 0.75 when what they actually did was quote a number.
        using var document = JsonDocument.Parse(json);

        var problems = ScoringWeights.Validate(document.RootElement, Pointer);

        problems.ShouldNotBeEmpty($"Reported: {Describe(problems)}");
        problems.ShouldContain(
            problem => problem.Message.Contains(offender, StringComparison.Ordinal)
                || problem.Pointer.Contains(offender, StringComparison.Ordinal),
            $"the message has to name '{offender}' rather than reporting a sum: the value is not a number, "
            + $"which is a different mistake with a different fix. Reported: {Describe(problems)}");
    }

    [Theory]
    [InlineData("""{"segmentFit":0.25,"sizeFit":0.15,"facilityFit":0.20,"signals":0.25,"proximity":0.05,"confidence":0.10}""")]
    [InlineData("""{"signals":1.0}""")]
    [InlineData("""{"segmentFit":0.5,"confidence":0.5}""")]
    [InlineData("""{"signals":1,"proximity":0}""")]
    public void AWeightsObjectWhoseKeysAreAllFeatureNamesIsAccepted(string json)
    {
        // The other side of the known-keys rule: a partial object is legitimate. §7.6's features are the
        // vocabulary, not a required set - FromJson treats an omitted feature as a weight of 0, which is
        // the only thing {"signals": 1.0} can mean.
        using var document = JsonDocument.Parse(json);

        ScoringWeights.Validate(document.RootElement, Pointer).ShouldBeEmpty(
            $"every key here is one of {string.Join(", ", ScoreFeatures.All)}: {json}");
    }

    [Fact]
    public void TheMessageSaysWhatTheWeightsActuallySumTo()
    {
        using var document = JsonDocument.Parse("""{"segmentFit":0.3,"signals":0.3}""");

        var problem = ScoringWeights.Validate(document.RootElement, Pointer).ShouldHaveSingleItem();

        problem.Message.ShouldContain(
            "0.6",
            Case.Insensitive,
            $"'must sum to 1.0' on its own leaves the caller to add six numbers up. Got: {problem.Message}");
    }

    [Theory]
    [InlineData(ScoreFeatures.SegmentFit, 0.25)]
    [InlineData(ScoreFeatures.SizeFit, 0.15)]
    [InlineData(ScoreFeatures.FacilityFit, 0.20)]
    [InlineData(ScoreFeatures.Signals, 0.25)]
    [InlineData(ScoreFeatures.Proximity, 0.05)]
    [InlineData(ScoreFeatures.Confidence, 0.10)]
    public void AFeatureNameResolvesToItsWeight(string feature, double expected) =>
        ScoringWeights.Default.For(feature).ShouldBe(
            expected,
            1e-9,
            "the breakdown is built by feature name, so the name has to resolve to the weight.");

    private static double SumOf(JsonElement weights) =>
        weights.EnumerateObject().Sum(weight => weight.Value.GetDouble());

    private static string Describe(IReadOnlyList<ValidationProblem> problems) =>
        problems.Count == 0
            ? "(nothing)"
            : string.Join("; ", problems.Select(problem => $"{problem.Pointer} :: {problem.Message}"));
}
