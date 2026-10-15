using System.Text.Json;
using ProspectStudio.Core.Leads;
using ProspectStudio.Core.SearchProfiles;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Leads;

/// <summary>
/// <c>save_research</c>'s validation against <c>poc/schemas/research.schema.json</c>: the spec pack's
/// accepted document, every rejected one, and the two shapes mcp-tools.md §save_research calls out by
/// name.
/// </summary>
/// <remarks>
/// Only the JSON <strong>pointer</strong> is asserted, not the message. C1 settled that: the pointer is
/// what a skill branches on and what Claude shows the user, and pinning library wording would make the
/// suite hostage to a JsonSchema.Net release note.
/// </remarks>
public class ResearchValidationTests
{
    public static TheoryData<string> InvalidCases() =>
        [.. SampleResearch.InvalidCases.Select(entry => entry.Case)];

    private static ResearchValidator Validator => new();

    [Fact]
    public void TheSpecPacksValidDocumentIsAccepted()
    {
        var problems = Validator.Validate(SampleResearch.Valid());

        problems.ShouldBeEmpty(
            "poc/fixtures/sample-research-valid.json is the document mcp-tools.md §save_research points "
            + $"at. Reported: {Describe(problems)}");
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void EachRejectedDocumentIsReportedAtThePointerTheFixtureNames(string caseName)
    {
        var entry = SampleResearch.InvalidCase(caseName);

        var problems = Validator.Validate(entry.Research);

        problems.ShouldNotBeEmpty(
            $"'{entry.Case}' in sample-research-invalid.json claims '{entry.ExpectedError}', so it has "
            + "to be rejected.");

        problems.ShouldContain(
            problem => At(problem.Pointer, entry.Pointer),
            $"'{entry.Case}' claims the problem is at '{entry.Pointer}' ({entry.Reason}). Reported: "
            + Describe(problems));

        // Precision, not just rejection: a document rejected for a reason somewhere else entirely would
        // satisfy the assertion above while telling the user to look in the wrong place.
        problems.Where(problem => !At(problem.Pointer, entry.Pointer)).ShouldBeEmpty(
            $"'{entry.Case}' is wrong in exactly one place. Anything reported elsewhere is noise that "
            + $"sends the reader hunting. Reported: {Describe(problems)}");
    }

    [Fact]
    public void AnEmptySignalsArrayIsAcceptedWithStatusNoSignal()
    {
        // mcp-tools.md §save_research: 'status: "no_signal" allowed with an empty signals'.
        var problems = Validator.Validate(SampleResearch.NorthlineGlassNoSignal());

        problems.ShouldBeEmpty(Describe(problems));
    }

    [Fact]
    public void NoSignalWithNoSignalsMemberAtAllIsAccepted()
    {
        // The schema's allOf/if/then applies maxItems: 0 to a member that may be absent, and an absent
        // member satisfies maxItems. Worth pinning: a hand-rolled version of the rule that read
        // "signals must be present and empty" would reject the commonest shape Claude will send.
        var research = SampleResearch.With(SampleResearch.NorthlineGlassNoSignal(), ("signals", null));

        research.TryGetProperty("signals", out _).ShouldBeFalse("the test document must have no signals member.");

        Validator.Validate(research).ShouldBeEmpty();
    }

    [Fact]
    public void AResearchedDocumentMayCarrySignals()
    {
        // The other half of the coupling: the if/then must fire only for no_signal. If it applied
        // unconditionally, every real research document would be rejected.
        var research = SampleResearch.Valid();

        research.GetProperty("status").GetString().ShouldBe("researched");
        research.GetProperty("signals").GetArrayLength().ShouldBe(2);

        Validator.Validate(research).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(-15)]
    [InlineData(0)]
    [InlineData(15)]
    public void AnAdjustmentOnTheBoundaryIsAccepted(int adjustment) =>
        Validator.Validate(SampleResearch.With(SampleResearch.Valid(), ("llmAdjustment", adjustment)))
            .ShouldBeEmpty("mcp-tools.md §save_research: 'llmAdjustment in −15…15', inclusive.");

    [Theory]
    [InlineData(-16)]
    [InlineData(16)]
    public void AnAdjustmentOutsideFifteenEitherWayIsRejectedAtItsOwnPointer(int adjustment)
    {
        var problems = Validator.Validate(
            SampleResearch.With(SampleResearch.Valid(), ("llmAdjustment", adjustment)));

        problems.ShouldContain(
            problem => problem.Pointer == "/llmAdjustment",
            $"the fixture covers +20; this covers the edges either side of the limit. Reported: {Describe(problems)}");
    }

    [Fact]
    public void ADocumentThatIsNotAnObjectIsRejectedRatherThanThrowing()
    {
        using var document = JsonDocument.Parse("\"researched\"");

        Validator.Validate(document.RootElement).ShouldNotBeEmpty(
            "save_research must answer VALIDATION_FAILED for a string or an array, not INTERNAL.");
    }

    /// <summary>
    /// True when a reported pointer is the fixture's pointer or sits inside it. C1's convention
    /// synthesizes the pointer of a <em>missing</em> member - <c>/signals/0/url</c> where JSON Schema
    /// reports the object at <c>/signals/0</c> - and that is more useful, not less, so both are accepted.
    /// </summary>
    private static bool At(string reported, string expected) =>
        string.Equals(reported, expected, StringComparison.Ordinal)
        || reported.StartsWith(expected + "/", StringComparison.Ordinal);

    private static string Describe(IReadOnlyList<ValidationProblem> problems) =>
        problems.Count == 0
            ? "(nothing)"
            : string.Join("; ", problems.Select(problem => $"{problem.Pointer} :: {problem.Message}"));
}
