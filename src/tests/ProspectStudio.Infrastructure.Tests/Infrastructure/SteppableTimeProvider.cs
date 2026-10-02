namespace ProspectStudio.Infrastructure.Tests.Infrastructure;

/// <summary>
/// A hand-driven clock whose <see cref="CreateTimer"/> also obeys <see cref="Advance"/>, so code that
/// waits through the clock — <c>Task.Delay(wait, clock, token)</c>, a <c>CancellationTokenSource</c>
/// built on a <see cref="TimeProvider"/> — finishes waiting the moment the test says time has passed.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ManualTimeProvider"/> on purpose: that one deliberately leaves timers on the
/// real clock, and the C2 job tests depend on it doing so. Making timers steppable there would change
/// what those tests exercise.
/// </para>
/// <para>
/// This is what makes the CBP client's 1 request/second spacing and its 20-second call budget assertable
/// at all. Waiting them out for real would put 15 to 20 seconds of sleeping into <c>dotnet test</c>, and
/// a timing test that sleeps is either slow or flaky — usually both.
/// </para>
/// </remarks>
internal sealed class SteppableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<Scheduled> _timers = [];
    private DateTimeOffset _now = start;

    public SteppableTimeProvider()
        : this(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero))
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

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new Scheduled(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the clock on and fires everything that was due, so an awaiting caller resumes.
    /// </summary>
    public DateTimeOffset Advance(TimeSpan by)
    {
        List<Scheduled> due;

        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(timer => timer.IsDueAt(_now))];
        }

        foreach (var timer in due)
        {
            timer.Fire(GetUtcNow());
        }

        return GetUtcNow();
    }

    private void Register(Scheduled timer)
    {
        lock (_gate)
        {
            if (!_timers.Contains(timer))
            {
                _timers.Add(timer);
            }
        }
    }

    private void Unregister(Scheduled timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class Scheduled(SteppableTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private readonly Lock _gate = new();
        private DateTimeOffset? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
            }

            if (_dueAt is null)
            {
                clock.Unregister(this);
            }
            else
            {
                clock.Register(this);
            }

            return true;
        }

        public bool IsDueAt(DateTimeOffset now)
        {
            lock (_gate)
            {
                return _dueAt is { } due && due <= now;
            }
        }

        public void Fire(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_dueAt is not { } due || due > now)
                {
                    return;
                }

                _dueAt = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : now + _period;
            }

            if (_dueAt is null)
            {
                clock.Unregister(this);
            }

            callback(state);
        }

        public void Dispose() => clock.Unregister(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
