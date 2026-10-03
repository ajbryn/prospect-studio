using ProspectStudio.Core.Candidates;
using Shouldly;

namespace ProspectStudio.Core.Tests.Candidates;

/// <summary>
/// Jaro-Winkler similarity. Three different thresholds depend on it - dedupe and suppression at 0.92
/// (technical-design §7.2, §7.3) and warranty matchback at 0.90 (§7.9) - so the published reference
/// values are pinned rather than "roughly similar" assertions.
/// </summary>
public class JaroWinklerTests
{
    [Theory]
    // Winkler's own published examples, to four decimal places.
    [InlineData("martha", "marhta", 0.9611)]
    [InlineData("dwayne", "duane", 0.8400)]
    [InlineData("dixon", "dicksonx", 0.8133)]
    // The fixture pairs the dedupe rules turn on.
    [InlineData("summit signs", "summit sign", 0.9833)]
    [InlineData("coastal crane and rigging", "coastal crane rigging", 0.9442)]
    [InlineData("bayou fulfillment", "brazos way fulfillment", 0.8537)]
    [InlineData("frontier 3pl services", "buffalo industries", 0.6210)]
    public void Similarity_MatchesTheReferenceValues(string left, string right, double expected) =>
        JaroWinkler.Similarity(left, right).ShouldBe(expected, 0.0001);

    [Theory]
    [InlineData("westpark metal fab", "westpark metal fab")]
    [InlineData("sabine auto repair", "sabine auto repair")]
    [InlineData("", "")]
    public void Similarity_IdenticalStrings_IsOne(string left, string right) =>
        JaroWinkler.Similarity(left, right).ShouldBe(1.0, 0.0001);

    [Theory]
    [InlineData("", "westpark metal fab")]
    [InlineData("westpark metal fab", "")]
    [InlineData(null, "westpark metal fab")]
    [InlineData("westpark metal fab", null)]
    [InlineData("abc", "xyz")]
    public void Similarity_NothingInCommon_IsZero(string? left, string? right) =>
        JaroWinkler.Similarity(left, right).ShouldBe(0.0, 0.0001);

    [Fact]
    public void Similarity_IsSymmetric() =>
        JaroWinkler.Similarity("summit signs", "summit sign")
            .ShouldBe(JaroWinkler.Similarity("summit sign", "summit signs"), 0.0001);

    [Fact]
    public void The_thresholds_are_the_ones_the_design_states()
    {
        JaroWinkler.DuplicateThreshold.ShouldBe(0.92, "technical-design §7.2 rule 3 and §7.3.");
        JaroWinkler.MatchbackThreshold.ShouldBe(0.90, "technical-design §7.9.");
    }

    [Fact]
    public void The_fixture_controls_sit_on_the_right_side_of_the_dedupe_threshold()
    {
        // 'summit signs' / 'summit sign' clears 0.92 easily - the ONLY thing keeping fx_0065 and
        // fx_0066 apart is the 200 m window, so if that window is dropped they merge and two real
        // sign shops 60 km apart become one lead.
        JaroWinkler.Similarity("summit signs", "summit sign")
            .ShouldBeGreaterThan(JaroWinkler.DuplicateThreshold);

        // These two must stay below it however close they are on the map (fx_0001 / fx_0013 are the
        // domain pair; fx_0032 / fx_0045 are 115 m apart with unrelated names).
        JaroWinkler.Similarity("bayou fulfillment", "brazos way fulfillment")
            .ShouldBeLessThan(JaroWinkler.DuplicateThreshold);
        JaroWinkler.Similarity("frontier 3pl services", "buffalo industries")
            .ShouldBeLessThan(JaroWinkler.DuplicateThreshold);
    }
}
