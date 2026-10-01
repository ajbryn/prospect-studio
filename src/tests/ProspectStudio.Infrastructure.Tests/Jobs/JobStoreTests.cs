using Microsoft.EntityFrameworkCore;
using ProspectStudio.Core.Jobs;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Jobs;

/// <summary>
/// The <c>jobs</c> table (technical-design §5.2) through the <see cref="IJobStore"/> the production
/// registration provides, against real SQLite in a temp file. Provider-neutral throughout: EF's own
/// APIs, never <c>sqlite_master</c> or SQLite type names.
/// </summary>
public class JobStoreTests
{
    [Fact]
    public async Task The_C2_Jobs_migration_is_applied()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        await using var context = await database.MigrateAsync(timeout.Token);

        (await context.Database.GetAppliedMigrationsAsync(timeout.Token)).ShouldContain(
            id => id.EndsWith("_C2_Jobs", StringComparison.Ordinal),
            "implementation-plan C2 introduces the jobs table in a migration named C2_Jobs.");
    }

    [Fact]
    public async Task A_job_round_trips_through_the_store_with_every_field_intact()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);

        var job = new JobSnapshot(
            "job_AB12CD",
            JobKinds.PrefetchWebsites,
            CampaignId: "cmp_7Q3KXM",
            JobStatuses.Running,
            Progress: 0.42,
            Message: "Fetched 336/800 sites (12 blocked by robots.txt)",
            ParametersJson: """{"scope":"top","topN":800}""",
            ResultJson: null,
            CreatedAt: new DateTimeOffset(2026, 10, 2, 13, 5, 0, TimeSpan.Zero),
            StartedAt: new DateTimeOffset(2026, 10, 2, 13, 5, 2, TimeSpan.Zero),
            FinishedAt: null);

        await harness.Store.AddAsync(job, timeout.Token);

        (await harness.Store.FindAsync("job_AB12CD", timeout.Token)).ShouldBe(job);
    }

    [Fact]
    public async Task An_unknown_job_id_is_not_found()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);

        (await harness.Store.FindAsync("job_NOPE00", timeout.Token)).ShouldBeNull();
        (await harness.Store.UpdateProgressAsync("job_NOPE00", 0.5, "x", timeout.Token)).ShouldBeFalse();
        (await harness.Store.MarkRunningAsync("job_NOPE00", DateTimeOffset.UtcNow, timeout.Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task List_filters_by_campaign_and_by_status_and_returns_the_newest_first()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);

        await harness.Store.AddAsync(Seed("job_OLD001", JobKinds.PrepareData, null, JobStatuses.Succeeded, minute: 1), timeout.Token);
        await harness.Store.AddAsync(Seed("job_MINE01", JobKinds.PrefetchWebsites, "cmp_AAAAAA", JobStatuses.Running, minute: 2), timeout.Token);
        await harness.Store.AddAsync(Seed("job_MINE02", JobKinds.RenderCampaign, "cmp_AAAAAA", JobStatuses.Succeeded, minute: 3), timeout.Token);
        await harness.Store.AddAsync(Seed("job_OTHER1", JobKinds.RenderCampaign, "cmp_BBBBBB", JobStatuses.Succeeded, minute: 4), timeout.Token);

        var all = await harness.Store.ListAsync(null, null, timeout.Token);
        all.Select(job => job.JobId).ShouldBe(["job_OTHER1", "job_MINE02", "job_MINE01", "job_OLD001"]);

        var mine = await harness.Store.ListAsync("cmp_AAAAAA", null, timeout.Token);
        mine.Select(job => job.JobId).ShouldBe(["job_MINE02", "job_MINE01"]);

        var succeeded = await harness.Store.ListAsync(null, JobStatuses.Succeeded, timeout.Token);
        succeeded.Select(job => job.JobId).ShouldBe(["job_OTHER1", "job_MINE02", "job_OLD001"]);

        var both = await harness.Store.ListAsync("cmp_AAAAAA", JobStatuses.Succeeded, timeout.Token);
        both.Select(job => job.JobId).ShouldBe(["job_MINE02"]);
    }

    [Fact]
    public async Task Progress_and_completion_updates_replace_the_stored_values()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var startedAt = new DateTimeOffset(2026, 10, 2, 13, 0, 0, TimeSpan.Zero);

        await harness.Store.AddAsync(
            Seed("job_LIVE01", JobKinds.PrepareData, null, JobStatuses.Queued, minute: 0),
            timeout.Token);

        (await harness.Store.MarkRunningAsync("job_LIVE01", startedAt, timeout.Token)).ShouldBeTrue();
        (await harness.Store.UpdateProgressAsync("job_LIVE01", 0.5, "Half way", timeout.Token)).ShouldBeTrue();

        var running = await harness.RequireJobAsync("job_LIVE01", timeout.Token);
        running.Status.ShouldBe(JobStatuses.Running);
        running.Progress.ShouldBe(0.5);
        running.Message.ShouldBe("Half way");
        running.StartedAt.ShouldBe(startedAt);
        running.FinishedAt.ShouldBeNull();

        var finishedAt = startedAt.AddMinutes(2);
        (await harness.Store.FinishAsync(
            "job_LIVE01",
            JobStatuses.Succeeded,
            1.0,
            "Reference data ready",
            """{"counties":11}""",
            finishedAt,
            timeout.Token)).ShouldBeTrue();

        var done = await harness.RequireJobAsync("job_LIVE01", timeout.Token);
        done.Status.ShouldBe(JobStatuses.Succeeded);
        done.Progress.ShouldBe(1.0);
        done.ResultJson.ShouldBe("""{"counties":11}""");
        done.FinishedAt.ShouldBe(finishedAt);
    }

    [Theory]
    [InlineData(JobStatuses.Cancelled)]
    [InlineData(JobStatuses.Interrupted)]
    [InlineData(JobStatuses.Succeeded)]
    [InlineData(JobStatuses.Failed)]
    public async Task Finishing_a_job_that_has_already_finished_is_refused(string terminal)
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);

        await harness.Store.AddAsync(
            Seed("job_FIXED1", JobKinds.PrepareData, null, terminal, minute: 1),
            timeout.Token);

        (await harness.Store.FinishAsync(
            "job_FIXED1",
            JobStatuses.Succeeded,
            1.0,
            "finished anyway",
            """{"counties":11}""",
            new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.Zero),
            timeout.Token)).ShouldBeFalse(
            $"a '{terminal}' row is final. Work that ignores its cancellation token and returns anyway "
            + "must not overwrite the status cancel_job already wrote, or cancel_job becomes advisory.");

        var unchanged = await harness.RequireJobAsync("job_FIXED1", timeout.Token);
        unchanged.Status.ShouldBe(terminal);
        unchanged.Message.ShouldNotBe("finished anyway");
        unchanged.ResultJson.ShouldBeNull();
    }

    [Theory]
    [InlineData(JobStatuses.Queued)]
    [InlineData(JobStatuses.Running)]
    public async Task Finishing_a_job_that_is_still_unfinished_is_allowed(string status)
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var finishedAt = new DateTimeOffset(2026, 10, 2, 15, 30, 0, TimeSpan.Zero);

        await harness.Store.AddAsync(
            Seed("job_OPEN01", JobKinds.PrepareData, null, status, minute: 1),
            timeout.Token);

        (await harness.Store.FinishAsync(
            "job_OPEN01",
            JobStatuses.Succeeded,
            1.0,
            "Reference data ready",
            """{"counties":11}""",
            finishedAt,
            timeout.Token)).ShouldBeTrue($"a '{status}' job is exactly what finishing is for.");

        var finished = await harness.RequireJobAsync("job_OPEN01", timeout.Token);
        finished.Status.ShouldBe(JobStatuses.Succeeded);
        finished.FinishedAt.ShouldBe(finishedAt);
        finished.ResultJson.ShouldBe("""{"counties":11}""");
    }

    [Fact]
    public async Task Marking_unfinished_jobs_interrupted_covers_running_and_queued_and_nothing_else()
    {
        using var timeout = Deadline();
        await using var harness = await JobTestHarness.CreateAsync(timeout.Token);
        var finishedAt = new DateTimeOffset(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);

        await harness.Store.AddAsync(Seed("job_RUN001", JobKinds.PrepareData, null, JobStatuses.Running, minute: 1), timeout.Token);
        await harness.Store.AddAsync(Seed("job_RUN002", JobKinds.PrefetchWebsites, "cmp_AAAAAA", JobStatuses.Running, minute: 2), timeout.Token);
        await harness.Store.AddAsync(Seed("job_WAIT01", JobKinds.PrepareData, null, JobStatuses.Queued, minute: 3), timeout.Token);
        await harness.Store.AddAsync(Seed("job_OK0001", JobKinds.RenderCampaign, null, JobStatuses.Succeeded, minute: 4), timeout.Token);
        await harness.Store.AddAsync(Seed("job_BAD001", JobKinds.RenderCampaign, null, JobStatuses.Failed, minute: 5), timeout.Token);
        await harness.Store.AddAsync(Seed("job_GONE01", JobKinds.RenderCampaign, null, JobStatuses.Cancelled, minute: 6), timeout.Token);

        (await harness.Store.MarkUnfinishedJobsInterruptedAsync(finishedAt, timeout.Token)).ShouldBe(3);

        foreach (var jobId in new[] { "job_RUN001", "job_RUN002", "job_WAIT01" })
        {
            var recovered = await harness.RequireJobAsync(jobId, timeout.Token);
            recovered.Status.ShouldBe(JobStatuses.Interrupted);
            recovered.FinishedAt.ShouldBe(finishedAt);
        }

        (await harness.RequireJobAsync("job_OK0001", timeout.Token)).Status.ShouldBe(JobStatuses.Succeeded);
        (await harness.RequireJobAsync("job_BAD001", timeout.Token)).Status.ShouldBe(JobStatuses.Failed);
        (await harness.RequireJobAsync("job_GONE01", timeout.Token)).Status.ShouldBe(JobStatuses.Cancelled);

        // Running it again changes nothing, so a restart loop cannot keep rewriting history.
        (await harness.Store.MarkUnfinishedJobsInterruptedAsync(finishedAt, timeout.Token)).ShouldBe(0);
    }

    private static JobSnapshot Seed(string jobId, string kind, string? campaignId, string status, int minute) =>
        new(
            jobId,
            kind,
            campaignId,
            status,
            Progress: status == JobStatuses.Succeeded ? 1.0 : 0.0,
            Message: null,
            ParametersJson: null,
            ResultJson: null,
            CreatedAt: new DateTimeOffset(2026, 10, 2, 12, minute, 0, TimeSpan.Zero),
            StartedAt: null,
            FinishedAt: null);

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(30));
}
