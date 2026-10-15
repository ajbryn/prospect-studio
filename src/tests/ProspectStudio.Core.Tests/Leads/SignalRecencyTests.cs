using ProspectStudio.Core.Leads;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Leads;

/// <summary>
/// §7.6's "dated within 12 months", which the spec leaves without a reference date or a precision.
/// Both are settled in the C6 decisions log: the instant comes from the injected
/// <see cref="TimeProvider"/>, and the comparison is at whole-month granularity.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the clock is injected.</strong> Worked example 1's permit is dated <c>2026-07</c>. Read
/// against wall time it is three months old today and inside the window; in July 2027 it falls out, the
/// <c>signals</c> feature drops 0.25 → 0, and the example silently stops being tier A - a suite that
/// passes today and fails next summer with no code change. <c>DateTimeOffset.UtcNow</c> must not appear
/// in the scorer, and <see cref="TheWindowIsMeasuredFromTheInjectedClock"/> is the guard that says so.
/// </para>
/// <para>
/// <strong>Why whole months.</strong> <c>signals[].date</c> admits both <c>YYYY-MM</c> and
/// <c>YYYY-MM-DD</c>. A day-precise window forces an arbitrary choice for a month-only date - read
/// "2025-10" as the 1st and it is outside a 365-day window on 2026-10-06, read it as the 31st and it is
/// inside - for an event that genuinely happened somewhere in that month. A signal therefore counts when
/// <c>(now.Year×12 + now.Month) − (date.Year×12 + date.Month)</c> is 0 through 11 inclusive: the twelve
/// calendar months ending with the current one.
/// </para>
/// </remarks>
public class SignalRecencyTests
{
    private const double Tolerance = 1e-9;

    /// <summary>The reference month is 2026-10, so 2025-11 is eleven whole months back.</summary>
    [Theory]
    [InlineData("2026-10", 0.6, "the current month")]
    [InlineData("2026-10-06", 0.6, "today")]
    [InlineData("2026-09-12", 0.6, "one month back - worked example 1's job post")]
    [InlineData("2026-07", 0.6, "three months back - worked example 1's permit")]
    [InlineData("2025-11", 0.6, "eleven whole months back: the last month inside the window")]
    [InlineData("2025-11-01", 0.6, "the first day of that month")]
    [InlineData("2025-11-30", 0.6, "the last day of that month")]
    [InlineData("2025-10", 0.0, "twelve whole months back: the first month outside the window")]
    [InlineData("2025-10-31", 0.0, "the last day of that month, still twelve whole months back")]
    [InlineData("2024-06", 0.0, "long outside")]
    [InlineData("2026-10-31", 0.6, "later in the current month, which the month truncation still counts")]
    [InlineData("2026-11", 0.0, "next month: future-dated, so it does not count")]
    [InlineData("2027-06", 0.0, "well in the future")]
    public void ASignalCountsForTheTwelveCalendarMonthsEndingWithTheCurrentOne(
        string date,
        double expected,
        string why)
    {
        var breakdown = ScoringFixtures.Scorer().Score(ScoringFixtures.Floor with
        {
            Signals = [new SignalFact(SignalTypes.Permit, date)],
        });

        breakdown.Of(ScoreFeatures.Signals).Value.ShouldBe(
            expected,
            Tolerance,
            $"'{date}' is {why}, measured from {FixedTimeProvider.ScoringReference:yyyy-MM}. "
            + $"{breakdown.Describe()}");
    }

    [Theory]
    [InlineData("2025-11", "2025-11-01")]
    [InlineData("2025-11", "2025-11-30")]
    [InlineData("2025-10", "2025-10-31")]
    public void AMonthOnlyDateScoresTheSameAsAnyDayInThatMonth(string monthOnly, string withDay)
    {
        var scorer = ScoringFixtures.Scorer();

        var coarse = scorer.Score(ScoringFixtures.Floor with
        {
            Signals = [new SignalFact(SignalTypes.Permit, monthOnly)],
        });
        var precise = scorer.Score(ScoringFixtures.Floor with
        {
            Signals = [new SignalFact(SignalTypes.Permit, withDay)],
        });

        precise.Of(ScoreFeatures.Signals).Value.ShouldBe(
            coarse.Of(ScoreFeatures.Signals).Value,
            Tolerance,
            $"'{monthOnly}' and '{withDay}' are the same event at two precisions, so they must score the "
            + "same. A day-precise window would split them: on a 365-day window measured from "
            + $"{FixedTimeProvider.ScoringReference:yyyy-MM-dd}, '2025-10-31' is 340 days old and inside "
            + $"while '2025-10-01' is 370 days old and outside. {precise.Describe()}");
    }

    [Fact]
    public void AFutureDatedSignalIsIgnoredRatherThanCredited()
    {
        // §7.6: "a date later than today is far more often a typo or a mis-transcribed citation than a
        // genuine filed-for-later project, and crediting it would let a bad date inflate a score." The
        // tempting alternative - treating a negative difference as "very recent" - rewards the typo.
        var scorer = ScoringFixtures.Scorer();

        var future = scorer.Score(ScoringFixtures.Floor with
        {
            Signals = [new SignalFact(SignalTypes.Permit, "2026-12"), new SignalFact(SignalTypes.Hiring, "2027-03")],
        });
        var inWindow = scorer.Score(ScoringFixtures.Floor with { Signals = ScoringFixtures.TwoRecentBuyingSignals });

        future.Of(ScoreFeatures.Signals).Value.ShouldBe(
            0d,
            Tolerance,
            "two future-dated signals are two signals the scorer must not count, so this is 0 and not 1.0. "
            + future.Describe());
        inWindow.Of(ScoreFeatures.Signals).Value.ShouldBe(
            1.0,
            Tolerance,
            $"the same two types with real dates do count, so the difference is the dates. {inWindow.Describe()}");
    }

    [Fact]
    public void TheWindowIsMeasuredFromTheInjectedClock()
    {
        // The guard against DateTimeOffset.UtcNow creeping back into the scorer. The same features are
        // scored twice, with nothing changed but the clock.
        var features = ScoringFixtures.Maximum;

        var atReference = ScoringFixtures.Scorer(clock: FixedTimeProvider.AtScoringReference()).Score(features);
        var thirteenMonthsLater = ScoringFixtures
            .Scorer(clock: FixedTimeProvider.AtScoringReferencePlus(13))
            .Score(features);

        atReference.Of(ScoreFeatures.Signals).Value.ShouldBe(
            1.0,
            Tolerance,
            $"both of worked example 1's signals are inside the window at the pinned instant. {atReference.Describe()}");
        atReference.Tier.ShouldBe(LeadTiers.A, atReference.Describe());

        thirteenMonthsLater.Of(ScoreFeatures.Signals).Value.ShouldBe(
            0d,
            Tolerance,
            "thirteen months on, both signals have left the twelve-month window. If this is still 1.0 the "
            + "scorer is reading wall time, or the window is not being applied at all. "
            + thirteenMonthsLater.Describe());

        thirteenMonthsLater.Score.ShouldBe(
            atReference.BaseScore - 25 + 0,
            "losing the signals feature costs exactly its weight × 100 = 25 points off the base, and the "
            + $"adjustment is unchanged. {thirteenMonthsLater.Describe()}");
        thirteenMonthsLater.AsOf.ShouldBe(
            FixedTimeProvider.ScoringReference.AddMonths(13),
            "the breakdown's asOf comes from the clock that scored it.");
    }
}
