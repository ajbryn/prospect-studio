using ProspectStudio.Core.Jobs;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Jobs;

/// <summary>
/// The job runner from technical-design §8, driven against real SQLite and a hand-wound clock:
/// progress is observable while a job runs, <c>cancel_job</c> stops it, a job the last process left
/// <c>running</c> becomes <c>interrupted</c> at startup, and at most two jobs run at once. Every test
/// sequences the work with gates instead of sleeping, so none of them can be flaky.
/// </summary>
public class JobRunnerTests
{
    /// <summary>
    /// Longer than the runner's "roughly every 2 s" progress window (technical-design §8), so an
    /// update cannot be hidden by throttling.
    /// </summary>
    private static readonly TimeSpan PastTheProgressWindow = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Enqueue_returns_a_job_id_and_stores_the_job_before_it_finishes()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var runner = await harness.StartRunnerAsync(timeout.Token);
        var finish = new TaskCompletionSource();

        var jobId = await runner.EnqueueAsync(
            JobKinds.PrepareData,
            async (_, token) =>
            {
                await finish.Task.WaitAsync(token);
                return null;
            },
            campaignId: "cmp_AAAAAA",
            parametersJson: """{"states":["TX"]}""",
            cancellationToken: timeout.Token);

        jobId.ShouldMatch(
            "^job_[0-9A-Za-z]{6}$",
            "mcp-tools.md §get_job shows job ids as 'job_' plus 6 characters, e.g. job_ab12cd.");

        var job = await harness.RequireJobAsync(jobId, timeout.Token);
        job.Kind.ShouldBe(JobKinds.PrepareData);
        job.CampaignId.ShouldBe("cmp_AAAAAA");
        job.ParametersJson.ShouldBe("""{"states":["TX"]}""");
        job.Status.ShouldBeOneOf(JobStatuses.Queued, JobStatuses.Running);
        job.FinishedAt.ShouldBeNull();

        finish.SetResult();
        (await harness.WaitForTerminalAsync(jobId, timeout.Token)).Status.ShouldBe(JobStatuses.Succeeded);
    }

    [Fact]
    public async Task Progress_a_running_job_reports_is_visible_through_the_store()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var runner = await harness.StartRunnerAsync(timeout.Token);

        var reportFirst = new TaskCompletionSource();
        var reportSecond = new TaskCompletionSource();
        var finish = new TaskCompletionSource();

        var jobId = await runner.EnqueueAsync(
            JobKinds.PrepareData,
            async (context, token) =>
            {
                await reportFirst.Task.WaitAsync(token);
                await context.ReportAsync(0.25, "Downloading county boundaries", token);

                await reportSecond.Task.WaitAsync(token);
                await context.ReportAsync(0.75, "Building the ZCTA index", token);

                await finish.Task.WaitAsync(token);
                return new { steps = 3 };
            },
            cancellationToken: timeout.Token);

        await harness.WaitForStatusAsync(jobId, JobStatuses.Running, timeout.Token);

        harness.Clock.Advance(PastTheProgressWindow);
        reportFirst.SetResult();
        var quarter = await WaitForProgressAsync(harness, jobId, 0.25, timeout.Token);
        quarter.Message.ShouldBe(
            "Downloading county boundaries",
            "get_job's 'message' is what the job last said about itself (mcp-tools.md §get_job).");

        harness.Clock.Advance(PastTheProgressWindow);
        reportSecond.SetResult();
        var threeQuarters = await WaitForProgressAsync(harness, jobId, 0.75, timeout.Token);
        threeQuarters.Message.ShouldBe("Building the ZCTA index");

        finish.SetResult();
        var finished = await harness.WaitForTerminalAsync(jobId, timeout.Token);
        finished.Status.ShouldBe(JobStatuses.Succeeded);
        finished.Progress.ShouldBe(1.0, "a job that succeeded is complete, so its progress is 1.0.");
        finished.ResultJson.ShouldNotBeNull("the job's return value is stored as result_json.");
        finished.ResultJson.ShouldContain("3");
        finished.StartedAt.ShouldNotBeNull();
        finished.FinishedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancelling_a_running_job_stops_the_work_and_records_cancelled()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var runner = await harness.StartRunnerAsync(timeout.Token);

        var started = new TaskCompletionSource();
        var observedCancellation = new TaskCompletionSource<bool>();

        var jobId = await runner.EnqueueAsync(
            JobKinds.PrefetchWebsites,
            async (_, token) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    observedCancellation.SetResult(true);
                    throw;
                }

                return null;
            },
            cancellationToken: timeout.Token);

        await started.Task.WaitAsync(timeout.Token);

        (await runner.CancelAsync(jobId, timeout.Token)).ShouldBe(JobStatuses.Cancelled);

        (await observedCancellation.Task.WaitAsync(timeout.Token)).ShouldBeTrue(
            "cancel_job must signal the CancellationToken the job's work is running under.");

        var cancelled = await harness.WaitForTerminalAsync(jobId, timeout.Token);
        cancelled.Status.ShouldBe(JobStatuses.Cancelled);
        cancelled.FinishedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancelling_a_queued_job_stops_it_from_ever_running()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var runner = await harness.StartRunnerAsync(timeout.Token);

        var release = new TaskCompletionSource();
        var blockers = new List<string>();
        for (var slot = 0; slot < JobRunner.MaxConcurrency; slot++)
        {
            blockers.Add(await runner.EnqueueAsync(
                JobKinds.RenderCampaign,
                async (_, token) =>
                {
                    await release.Task.WaitAsync(token);
                    return null;
                },
                cancellationToken: timeout.Token));
        }

        foreach (var blocker in blockers)
        {
            await harness.WaitForStatusAsync(blocker, JobStatuses.Running, timeout.Token);
        }

        var ran = false;
        var queued = await runner.EnqueueAsync(
            JobKinds.RenderCampaign,
            (_, _) =>
            {
                ran = true;
                return Task.FromResult<object?>(null);
            },
            cancellationToken: timeout.Token);

        (await runner.CancelAsync(queued, timeout.Token)).ShouldBe(JobStatuses.Cancelled);
        (await harness.WaitForTerminalAsync(queued, timeout.Token)).Status.ShouldBe(JobStatuses.Cancelled);

        release.SetResult();
        foreach (var blocker in blockers)
        {
            await harness.WaitForTerminalAsync(blocker, timeout.Token);
        }

        ran.ShouldBeFalse("a cancelled job must not run after the queue frees up.");
    }

    [Fact]
    public async Task Cancelling_an_unknown_job_reports_that_there_is_no_such_job()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var runner = await harness.StartRunnerAsync(timeout.Token);

        (await runner.CancelAsync("job_ZZZZZZ", timeout.Token)).ShouldBeNull(
            "the tool turns a missing job into NOT_FOUND.");
    }

    [Fact]
    public async Task Cancelling_a_job_that_already_finished_is_idempotent_and_reports_its_real_status()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var runner = await harness.StartRunnerAsync(timeout.Token);

        var jobId = await runner.EnqueueAsync(
            JobKinds.PrepareData,
            (_, _) => Task.FromResult<object?>(new { counties = 11 }),
            cancellationToken: timeout.Token);

        await harness.WaitForTerminalAsync(jobId, timeout.Token);

        (await runner.CancelAsync(jobId, timeout.Token)).ShouldBe(
            JobStatuses.Succeeded,
            "mcp-tools.md \u00A7cancel_job: on an already-finished job it is idempotent and returns that "
            + "job's own terminal status, so a skill polling a job it just cancelled does not meet the "
            + "race as an exception.");

        (await harness.RequireJobAsync(jobId, timeout.Token)).Status.ShouldBe(
            JobStatuses.Succeeded,
            "and the row is left exactly as it was.");
    }

    [Fact]
    public async Task At_most_two_jobs_run_at_once()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var runner = await harness.StartRunnerAsync(timeout.Token);

        var release = new TaskCompletionSource();
        var running = 0;
        var peak = 0;
        var gate = new Lock();

        var jobIds = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            jobIds.Add(await runner.EnqueueAsync(
                JobKinds.PrefetchWebsites,
                async (_, token) =>
                {
                    lock (gate)
                    {
                        running++;
                        peak = Math.Max(peak, running);
                    }

                    try
                    {
                        await release.Task.WaitAsync(token);
                    }
                    finally
                    {
                        lock (gate)
                        {
                            running--;
                        }
                    }

                    return null;
                },
                cancellationToken: timeout.Token));
        }

        await Eventually.UntilAsync(
            () =>
            {
                lock (gate)
                {
                    return Task.FromResult(running >= 2);
                }
            },
            "two jobs to be running at the same time",
            cancellationToken: timeout.Token);

        // Give a third job every chance to slip through before asserting it did not.
        var statuses = await harness.Store.ListAsync(null, null, timeout.Token);
        statuses.Count(job => job.Status == JobStatuses.Running).ShouldBeLessThanOrEqualTo(
            JobRunner.MaxConcurrency,
            "technical-design §8 caps the runner at 2 concurrent jobs.");

        release.SetResult();
        foreach (var jobId in jobIds)
        {
            await harness.WaitForTerminalAsync(jobId, timeout.Token);
        }

        peak.ShouldBe(JobRunner.MaxConcurrency, "all four jobs ran, but never more than two at a time.");
    }

    [Fact]
    public async Task A_job_whose_work_throws_is_recorded_as_failed()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var runner = await harness.StartRunnerAsync(timeout.Token);

        var jobId = await runner.EnqueueAsync(
            JobKinds.PrepareData,
            (_, _) => throw new InvalidOperationException("the county file is not a zip"),
            cancellationToken: timeout.Token);

        var failed = await harness.WaitForTerminalAsync(jobId, timeout.Token);
        failed.Status.ShouldBe(JobStatuses.Failed);
        failed.FinishedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_job_the_last_run_left_running_or_queued_becomes_interrupted_when_the_runner_starts()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);

        await harness.Store.AddAsync(
            Snapshot("job_CRASH1", JobStatuses.Running, progress: 0.4, startedAt: true),
            timeout.Token);
        await harness.Store.AddAsync(Snapshot("job_WAIT01", JobStatuses.Queued, progress: 0.0), timeout.Token);
        await harness.Store.AddAsync(
            Snapshot("job_DONE01", JobStatuses.Succeeded, progress: 1.0, startedAt: true, finishedAt: true),
            timeout.Token);

        await harness.StartRunnerAsync(timeout.Token);

        var recovered = await harness.RequireJobAsync("job_CRASH1", timeout.Token);
        recovered.Status.ShouldBe(
            JobStatuses.Interrupted,
            "technical-design \u00A78: on startup, a job still 'running' becomes 'interrupted'.");
        recovered.FinishedAt.ShouldNotBeNull();

        (await harness.RequireJobAsync("job_WAIT01", timeout.Token)).Status.ShouldBe(
            JobStatuses.Interrupted,
            "the queue lives in memory, so a row left 'queued' has nothing left to run it and would "
            + "otherwise claim 'queued' forever (mcp-tools.md \u00A7get_job).");

        (await harness.RequireJobAsync("job_DONE01", timeout.Token)).Status.ShouldBe(
            JobStatuses.Succeeded,
            "startup recovery must only touch rows that had not finished.");
    }

    [Fact]
    public async Task Jobs_survive_closing_and_reopening_the_database()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var runner = await harness.StartRunnerAsync(timeout.Token);

        var jobId = await runner.EnqueueAsync(
            JobKinds.PrepareData,
            (_, _) => Task.FromResult<object?>(new { counties = 11 }),
            parametersJson: """{"states":["TX"],"force":false}""",
            cancellationToken: timeout.Token);

        var finished = await harness.WaitForTerminalAsync(jobId, timeout.Token);
        finished.Status.ShouldBe(JobStatuses.Succeeded);

        await harness.ReopenAsync(timeout.Token);

        var reopened = await harness.RequireJobAsync(jobId, timeout.Token);
        reopened.Status.ShouldBe(JobStatuses.Succeeded);
        reopened.Kind.ShouldBe(JobKinds.PrepareData);
        reopened.ParametersJson.ShouldBe("""{"states":["TX"],"force":false}""");
        reopened.ResultJson.ShouldNotBeNull();
    }

    private static async Task<JobSnapshot> WaitForProgressAsync(
        JobTestHarness harness,
        string jobId,
        double progress,
        CancellationToken cancellationToken)
    {
        JobSnapshot? job = null;
        await Eventually.UntilAsync(
            async () =>
            {
                job = await harness.Store.FindAsync(jobId, cancellationToken);
                return job is not null && Math.Abs(job.Progress - progress) < 0.0001;
            },
            $"job '{jobId}' to report progress {progress}",
            cancellationToken: cancellationToken);

        return job!;
    }

    private static JobSnapshot Snapshot(
        string jobId,
        string status,
        double progress,
        bool startedAt = false,
        bool finishedAt = false)
    {
        var created = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
        return new JobSnapshot(
            jobId,
            JobKinds.PrepareData,
            CampaignId: null,
            status,
            progress,
            Message: "Downloading county boundaries",
            ParametersJson: null,
            ResultJson: null,
            CreatedAt: created,
            StartedAt: startedAt ? created.AddSeconds(1) : null,
            FinishedAt: finishedAt ? created.AddSeconds(30) : null);
    }

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(30));
}
