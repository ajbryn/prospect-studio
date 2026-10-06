namespace ProspectStudio.Tests.Shared;

/// <summary>
/// A clock stopped at a chosen instant, for anything whose answer depends on "now". Scoring is the
/// first such thing: technical-design §7.6 credits buying signals "dated within 12 months", so the
/// same research scores differently as the calendar moves.
/// </summary>
/// <remarks>
/// <para>
/// This is why every scoring test pins <see cref="ScoringReference"/> instead of letting the scorer
/// read <c>DateTimeOffset.UtcNow</c>. Worked example 1's permit is dated <c>2026-07</c>; against wall
/// time it stops counting in July 2027, the <c>signals</c> feature drops 0.25 → 0, and the example
/// silently stops being tier A - a suite that passes today and fails next summer with no code change.
/// </para>
/// <para>
/// Separate from <c>ManualTimeProvider</c> in ProspectStudio.Infrastructure.Tests, which the C2 job
/// tests step forward by hand. This one is shared by all three test projects.
/// </para>
/// </remarks>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    /// <summary>
    /// The instant every scoring test measures signal recency against: <c>2026-10-06T00:00:00Z</c>,
    /// the date chunk C6 was written, so the dates in the committed research fixtures keep the meaning
    /// their comments give them. Worked example 1's permit (<c>2026-07</c>) is three whole months old
    /// here and inside §7.6's window; its job post (<c>2026-09-12</c>) is one month old.
    /// </summary>
    public static DateTimeOffset ScoringReference { get; } = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A provider stopped at <see cref="ScoringReference"/>.</summary>
    public static FixedTimeProvider AtScoringReference() => new(ScoringReference);

    /// <summary>The same clock moved on, for proving the window is read from the clock.</summary>
    public static FixedTimeProvider AtScoringReferencePlus(int months) =>
        new(ScoringReference.AddMonths(months));

    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => now.UtcTicks;
}
