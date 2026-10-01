using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using ProspectStudio.Core.Json;

namespace ProspectStudio.Core.Jobs;

/// <summary>The work a job does. Report progress through <paramref name="context"/>; return the result document.</summary>
public delegate Task<object?> JobWork(JobContext context, CancellationToken cancellationToken);

/// <summary>
/// What a running job can say about itself. The runner writes progress to the <c>jobs</c> table so
/// <c>get_job</c> can read it (mcp-tools.md §get_job).
/// </summary>
public sealed class JobContext
{
    private readonly JobRunner _runner;

    internal JobContext(JobRunner runner, string jobId)
    {
        _runner = runner;
        JobId = jobId;
    }

    public string JobId { get; }

    /// <param name="progress">0.0 to 1.0.</param>
    /// <param name="message">What the job is doing, e.g. "Fetched 336/800 sites".</param>
    public Task ReportAsync(double progress, string? message, CancellationToken cancellationToken = default) =>
        _runner.ReportAsync(JobId, progress, message, cancellationToken);
}

/// <summary>
/// The in-process job runner from technical-design §8: a queue with at most
/// <see cref="MaxConcurrency"/> jobs running at once, state persisted in the <c>jobs</c> table, and
/// cancellation through <c>cancel_job</c>.
/// </summary>
/// <remarks>
/// Pure orchestration over <see cref="Channel"/>: every byte of persistence goes through
/// <see cref="IJobStore"/>, so this stays in Core (where the desktop app can reuse it) even though
/// technical-design §3 files it under Infrastructure.
/// </remarks>
public sealed class JobRunner : IAsyncDisposable
{
    /// <summary>Technical-design §8: "max 2 concurrent".</summary>
    public const int MaxConcurrency = 2;

    /// <summary>Technical-design §8: progress reaches the <c>jobs</c> table "roughly every 2 s".</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

    /// <summary>Matches the <c>message</c> column, so a long failure reason cannot overflow it.</summary>
    public const int MaxMessageLength = 1000;

    private static readonly TimeSpan _shutdownGrace = TimeSpan.FromSeconds(10);

    private readonly IJobStore _store;
    private readonly TimeProvider _time;
    private readonly JobFinishedObserver? _finished;
    private readonly Channel<JobEntry> _queue = Channel.CreateUnbounded<JobEntry>();
    private readonly ConcurrentDictionary<string, JobEntry> _live = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _workers = [];
    private int _started;
    private volatile bool _disposed;

    /// <param name="finished">
    /// Optional: told how every job ended, so the host can log a failure that would otherwise exist only
    /// in <c>jobs.message</c> (NFR-7).
    /// </param>
    public JobRunner(IJobStore store, TimeProvider timeProvider, JobFinishedObserver? finished = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _store = store;
        _time = timeProvider;
        _finished = finished;
    }

    /// <summary>
    /// Marks any job the last run left <see cref="JobStatuses.Running"/> or
    /// <see cref="JobStatuses.Queued"/> as <see cref="JobStatuses.Interrupted"/>, then starts consuming
    /// the queue.
    /// </summary>
    /// <returns>How many stale rows the recovery touched.</returns>
    public async Task<int> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return 0;
        }

        var interrupted = await _store
            .MarkUnfinishedJobsInterruptedAsync(_time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        for (var worker = 0; worker < MaxConcurrency; worker++)
        {
            _workers.Add(Task.Run(ConsumeAsync, CancellationToken.None));
        }

        return interrupted;
    }

    /// <summary>Queues a job and returns its <c>job_XXXXXX</c> id straight away.</summary>
    public async Task<string> EnqueueAsync(
        string kind,
        JobWork work,
        string? campaignId = null,
        string? parametersJson = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entry = new JobEntry(JobIds.New(), kind, work);
        await _store.AddAsync(
            new JobSnapshot(
                entry.JobId,
                kind,
                campaignId,
                JobStatuses.Queued,
                Progress: 0,
                Message: null,
                parametersJson,
                ResultJson: null,
                CreatedAt: _time.GetUtcNow(),
                StartedAt: null,
                FinishedAt: null),
            cancellationToken).ConfigureAwait(false);

        // Registered before it is queued, so cancel_job can reach a job that has not started yet.
        _live[entry.JobId] = entry;

        if (!_queue.Writer.TryWrite(entry))
        {
            // The queue closed between the disposal check and here, so nothing will ever run the row.
            _live.TryRemove(entry.JobId, out _);
            entry.Dispose();
            await _store.FinishAsync(
                entry.JobId,
                JobStatuses.Interrupted,
                progress: 0,
                "The server stopped before the job started.",
                resultJson: null,
                _time.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }

        return entry.JobId;
    }

    /// <summary>
    /// Cancels a queued or running job: the work's <see cref="CancellationToken"/> is signalled and the
    /// row ends as <see cref="JobStatuses.Cancelled"/>.
    /// </summary>
    /// <returns>
    /// The status <c>cancel_job</c> should report: <see cref="JobStatuses.Cancelled"/> for a job that was
    /// queued or running, or - because cancelling is idempotent - the job's own terminal status when it
    /// had already finished, so a skill polling a job it just cancelled never meets an exception on a
    /// race (mcp-tools.md §cancel_job). Null when there is no such job, which the tool maps to
    /// <c>NOT_FOUND</c>.
    /// </returns>
    public async Task<string?> CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = await _store.FindAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return null;
        }

        if (JobStatuses.IsTerminal(job.Status))
        {
            return job.Status;
        }

        if (_live.TryGetValue(jobId, out var entry))
        {
            entry.RequestCancel();
        }

        if (job.Status == JobStatuses.Queued)
        {
            // Nothing has picked the job up, so no worker will ever write its terminal row.
            await _store.FinishAsync(
                jobId,
                JobStatuses.Cancelled,
                job.Progress,
                job.Message,
                resultJson: null,
                _time.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }

        return JobStatuses.Cancelled;
    }

    internal async Task ReportAsync(string jobId, double progress, string? message, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        var trimmed = Truncate(message);

        if (!_live.TryGetValue(jobId, out var entry))
        {
            await _store.UpdateProgressAsync(jobId, clamped, trimmed, cancellationToken).ConfigureAwait(false);
            return;
        }

        entry.Progress = clamped;
        entry.Message = trimmed;

        if (!entry.MayPersistAt(_time.GetUtcNow(), ProgressInterval))
        {
            return;
        }

        await _store.UpdateProgressAsync(jobId, clamped, trimmed, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_workers).WaitAsync(_shutdownGrace).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Work that ignores its cancellation token, or a store that is already closing, must not
            // hold the process open or throw from a dispose. Startup recovery turns whatever the
            // process leaves behind into 'interrupted'.
        }

        _shutdown.Dispose();
    }

    private async Task ConsumeAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (_shutdown.IsCancellationRequested)
            {
                // Drain without running: the row stays queued and startup recovery interrupts it.
                continue;
            }

            try
            {
                await RunAsync(entry).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Only a store that refused the final write reaches here. The worker has to survive it,
                // or one unwritable row would stop every later job from running.
            }
        }
    }

    private async Task RunAsync(JobEntry entry)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Token, _shutdown.Token);

        try
        {
            if (linked.IsCancellationRequested)
            {
                return;
            }

            var startedAt = _time.GetUtcNow();
            if (!await _store.MarkRunningAsync(entry.JobId, startedAt, CancellationToken.None).ConfigureAwait(false))
            {
                // The row is no longer queued: cancel_job got there first, or it is gone.
                return;
            }

            entry.MarkStarted(startedAt);
            var result = await entry.Work(new JobContext(this, entry.JobId), linked.Token).ConfigureAwait(false);
            await FinishAsync(entry, JobStatuses.Succeeded, 1.0, entry.Message, Serialize(result)).ConfigureAwait(false);
            Report(entry, JobStatuses.Succeeded, entry.Message, exception: null);
        }
        catch (OperationCanceledException)
        {
            var status = entry.CancelRequested ? JobStatuses.Cancelled : JobStatuses.Interrupted;
            await FinishAsync(entry, status, entry.Progress, entry.Message, resultJson: null).ConfigureAwait(false);
            Report(entry, status, entry.Message, exception: null);
        }
        catch (Exception exception)
        {
            var reason = Truncate(exception.Message);
            await FinishAsync(entry, JobStatuses.Failed, entry.Progress, reason, resultJson: null)
                .ConfigureAwait(false);
            Report(entry, JobStatuses.Failed, reason, exception);
        }
        finally
        {
            _live.TryRemove(entry.JobId, out _);
            entry.Dispose();
        }
    }

    private Task FinishAsync(JobEntry entry, string status, double progress, string? message, string? resultJson) =>
        _store.FinishAsync(entry.JobId, status, progress, message, resultJson, _time.GetUtcNow(), CancellationToken.None);

    private void Report(JobEntry entry, string status, string? message, Exception? exception)
    {
        if (_finished is null)
        {
            return;
        }

        try
        {
            _finished(new JobOutcome(entry.JobId, entry.Kind, status, message, exception));
        }
        catch (Exception)
        {
            // A logger that throws must not turn a finished job into a worker crash.
        }
    }

    private static string? Serialize(object? result) =>
        result is null ? null : JsonSerializer.Serialize(result, ProspectStudioJson.Options);

    private static string? Truncate(string? message) =>
        message is { Length: > MaxMessageLength } ? message[..MaxMessageLength] : message;

    /// <summary>One queued or running job: its work, its cancellation and its last reported progress.</summary>
    private sealed class JobEntry(string jobId, string kind, JobWork work) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Lock _gate = new();
        private DateTimeOffset _lastPersistedAt = DateTimeOffset.MinValue;
        private bool _disposed;

        public string JobId { get; } = jobId;

        public string Kind { get; } = kind;

        public JobWork Work { get; } = work;

        public CancellationToken Token => _cancellation.Token;

        public bool CancelRequested { get; private set; }

        public double Progress { get; set; }

        public string? Message { get; set; }

        public void MarkStarted(DateTimeOffset startedAt)
        {
            lock (_gate)
            {
                _lastPersistedAt = startedAt;
            }
        }

        /// <summary>True when enough time has passed to write progress again, and claims the window.</summary>
        public bool MayPersistAt(DateTimeOffset now, TimeSpan interval)
        {
            lock (_gate)
            {
                if (now - _lastPersistedAt < interval)
                {
                    return false;
                }

                _lastPersistedAt = now;
                return true;
            }
        }

        public void RequestCancel()
        {
            lock (_gate)
            {
                CancelRequested = true;
                if (_disposed)
                {
                    return;
                }
            }

            try
            {
                // Outside the lock: Cancel runs its callbacks, which can continue the job inline.
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job finished between the check and here, so there is nothing left to cancel.
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _cancellation.Dispose();
            }
        }
    }
}
