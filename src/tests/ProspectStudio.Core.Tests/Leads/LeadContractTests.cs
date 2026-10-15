using ProspectStudio.Core.Leads;
using Shouldly;

namespace ProspectStudio.Core.Tests.Leads;

/// <summary>
/// The small vocabularies the lead tools share, from technical-design §5.2 and mcp-tools.md §Leads.
/// They are asserted because a typo in one of them is a filter that silently matches nothing.
/// </summary>
public class LeadContractTests
{
    [Fact]
    public void ResearchStatusHasExactlyTheThreeValuesSection52Lists() =>
        ResearchStatuses.All.ShouldBe(
            ["none", "saved", "no_signal"],
            ignoreOrder: true,
            "technical-design §5.2: 'research_status (none/saved/no_signal)'. list_leads' researchStatus "
            + "filter draws from these, and the three-way split is the point: a no_signal lead has been "
            + "looked at and will never reach tier A, so it must not look like an unresearched one.");

    [Theory]
    [InlineData("researched", ResearchStatuses.Saved)]
    [InlineData("no_signal", ResearchStatuses.NoSignal)]
    public void AResearchDocumentsStatusMapsOntoTheLeadsResearchStatus(string documentStatus, string expected) =>
        ResearchStatuses.ForResearchStatus(documentStatus).ShouldBe(
            expected,
            "the two vocabularies differ on purpose: the document says 'researched', the lead column says "
            + "'saved'. Neither should be renamed to match the other.");

    [Fact]
    public void ScoreFeaturesAreTheSixOfSection76InTheOrderTheTableListsThem() =>
        ScoreFeatures.All.ShouldBe(
            ["segmentFit", "sizeFit", "facilityFit", "signals", "proximity", "confidence"],
            "these are also the keys of a profile's scoringWeights object, so a rename breaks a saved "
            + "profile.");

    [Fact]
    public void TierNamesAreTheThreeSection76Uses() =>
        LeadTiers.All.ShouldBe(["A", "B", "C"], ignoreOrder: true);

    [Fact]
    public void SignalTypesAreTheEightTheSchemaAllows() =>
        SignalTypes.All.ShouldBe(
            ["permit", "hiring", "expansion", "news", "funding", "contract", "registry", "other"],
            ignoreOrder: true,
            "poc/schemas/research.schema.json, signals[].type.");

    [Fact]
    public void BuyingSignalsAreTheSixSection76Credits()
    {
        SignalTypes.Buying.ShouldBe(
            ["permit", "hiring", "expansion", "news", "funding", "contract"],
            ignoreOrder: true,
            "§7.6: 'Buying signals (permit, hiring, expansion, news, funding, contract; not "
            + "registry/other)'.");

        SignalTypes.All.Except(SignalTypes.Buying).ShouldBe(
            ["registry", "other"],
            ignoreOrder: true,
            "every schema type is either a buying signal or deliberately excluded; a ninth type added to "
            + "the schema without a decision here would default to whichever side the code happened to "
            + "put it on.");
    }

    [Fact]
    public void FacilityFitLevelsAreTheFourTheSchemaAllows() =>
        FacilityFitLevels.All.ShouldBe(
            ["high", "medium", "low", "unknown"],
            ignoreOrder: true,
            "poc/schemas/research.schema.json, facilityFit.level - which §7.6 now reads in preference to "
            + "the website keyword count.");

    [Fact]
    public void ListLeadsDefaultsToTwentyFiveRowsAndCapsAWebExcerptAtFifteenHundred()
    {
        LeadResponseLimits.DefaultPageSize.ShouldBe(25, "mcp-tools.md §list_leads.");
        LeadResponseLimits.MaxWebExcerpt.ShouldBe(1500, "mcp-tools.md §get_lead.");
    }

    [Fact]
    public void ScoreDescIsADocumentedSortValueAndSomethingElseIsNot()
    {
        LeadSorts.IsKnown(LeadSorts.ScoreDesc).ShouldBeTrue("mcp-tools.md §list_leads shows 'score_desc'.");
        LeadSorts.IsKnown("scoredesc").ShouldBeFalse();
        LeadSorts.IsKnown(null).ShouldBeFalse();
    }
}
