using ProspectStudio.Core.Jobs;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// mcp-tools.md §get_job: "On server start, both <c>running</c> and <c>queued</c> rows become
/// <c>interrupted</c>." Proven over the real stdio server rather than at the store level, because that
/// is where a forgotten startup call would actually bite: the user restarts Claude Desktop and
/// <c>get_job</c> keeps claiming a job is running that nothing is working on.
/// </summary>
public class JobRestartTests
{
    [Theory]
    [InlineData(JobStatuses.Running, "job_CRASH2")]
    [InlineData(JobStatuses.Queued, "job_WAIT02")]
    public async Task An_unfinished_job_left_by_a_stopped_server_is_interrupted_after_a_restart(
        string leftAs,
        string jobId)
    {
        using var timeout = TestTimeout.Start(120);
        using var workspace = new TempWorkspace();

        // First run: just enough to create and migrate the database.
        await using (await IsolatedMcpServer.StartAsync(workspace.Home, workspace.Data, cancellationToken: timeout.Token))
        {
        }

        await ServerJobs.AddAsync(
            workspace.Data,
            ServerJobs.Snapshot(jobId, JobKinds.PrepareData, status: leftAs, progress: 0.4),
            timeout.Token);

        await using var restarted = await IsolatedMcpServer.StartAsync(
            workspace.Home,
            workspace.Data,
            cancellationToken: timeout.Token);

        var payload = await ToolCall.OkAsync(
            restarted.Client,
            "get_job",
            new Dictionary<string, object?> { ["jobId"] = jobId },
            restarted.StandardError);

        payload.GetProperty("status").GetString().ShouldBe(
            JobStatuses.Interrupted,
            $"nothing is running and the queue is empty when the process starts, so a row left "
            + $"'{leftAs}' is stale.");
    }

    [Fact]
    public async Task A_job_that_had_already_finished_is_untouched_by_a_restart()
    {
        using var timeout = TestTimeout.Start(120);
        using var workspace = new TempWorkspace();

        await using (await IsolatedMcpServer.StartAsync(workspace.Home, workspace.Data, cancellationToken: timeout.Token))
        {
        }

        await ServerJobs.AddAsync(
            workspace.Data,
            ServerJobs.Snapshot(
                "job_KEEP01",
                JobKinds.PrepareData,
                status: JobStatuses.Succeeded,
                progress: 1.0,
                message: "Reference data ready",
                resultJson: """{"counties":11}"""),
            timeout.Token);

        await using var restarted = await IsolatedMcpServer.StartAsync(
            workspace.Home,
            workspace.Data,
            cancellationToken: timeout.Token);

        var payload = await ToolCall.OkAsync(
            restarted.Client,
            "get_job",
            new Dictionary<string, object?> { ["jobId"] = "job_KEEP01" },
            restarted.StandardError);

        payload.GetProperty("status").GetString().ShouldBe(JobStatuses.Succeeded);
        payload.GetProperty("progress").GetDouble().ShouldBe(1.0);
    }
}
