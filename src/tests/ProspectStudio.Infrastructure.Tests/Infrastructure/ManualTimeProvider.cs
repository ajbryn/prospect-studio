namespace ProspectStudio.Infrastructure.Tests.Infrastructure;

/// <summary>
/// A clock the test moves by hand, so the job tests can step past the runner's progress-reporting
/// window without sleeping. A flaky timing test is worse than no timing test.
/// </summary>
/// <remarks>
/// <see cref="CreateTimer"/> is deliberately left as the base implementation, which uses the real
/// clock: nothing in the C2 job behaviour is supposed to depend on a timer firing.
/// </remarks>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public DateTimeOffset Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
            return _now;
        }
    }
}
