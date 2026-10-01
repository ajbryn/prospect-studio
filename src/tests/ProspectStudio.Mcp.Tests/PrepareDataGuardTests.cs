using ProspectStudio.Core.Jobs;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The two things <c>prepare_data</c> refuses before it queues anything: a second run while one is still
/// going, and a state code that is not a US state. Both decisions happen before any file work, so the
/// validation cases need no prepared data at all.
/// </summary>
[Collection(McpServerCollection.Name)]
public class PrepareDataGuardTests(McpServerFixture server)
{
    [Theory]
    [InlineData(JobStatuses.Running)]
    [InlineData(JobStatuses.Queued)]
    public async Task An_unfinished_prepare_data_job_blocks_a_second_one_and_the_hint_names_it(string status)
    {
        using var timeout = TestTimeout.Start(120);
        using var workspace = new TempWorkspace();

        // Its own server, because the seeded job stays unfinished for the rest of the process and would
        // otherwise make every later prepare_data call on a shared server fail too.
        await using var isolated = await IsolatedMcpServer.StartAsync(
            workspace.Home,
            workspace.Data,
            cancellationToken: timeout.Token);

        // Seeded after the server started: startup recovery turns unfinished rows into 'interrupted', so
        // a row written beforehand would no longer be unfinished by the time the tool looks.
        const string existing = "job_BUSY01";
        await ServerJobs.AddAsync(
            workspace.Data,
            ServerJobs.Snapshot(existing, JobKinds.PrepareData, status: status, progress: 0.3),
            timeout.Token);

        var error = await ToolCall.ErrorAsync(
            isolated.Client,
            "prepare_data",
            new Dictionary<string, object?> { ["states"] = new[] { "TX" } },
            isolated.StandardError);

        error.Code.ShouldBe(
            "JOB_RUNNING",
            $"a '{status}' job has not finished. Two runs would write counties.parquet and the CSVs at "
            + "the same time, and a half-written file still has content - so the next run would skip it "
            + "and the damage would stick.");

        error.Hint.ShouldNotBeNull().ShouldContain(
            existing,
            Case.Sensitive,
            "mcp-tools.md §Errors hints JOB_RUNNING with \"Wait for job_ab12cd\", so the hint has to name "
            + $"the job to wait for; got '{error.Hint}'");
    }

    [Fact]
    public async Task A_finished_prepare_data_job_does_not_block_the_next_one()
    {
        using var timeout = TestTimeout.Start(120);
        using var workspace = new TempWorkspace();

        // Reference data in place first, so the job this test queues skips every step instead of
        // downloading from census.gov - no test may reach the network (CLAUDE.md).
        Directory.CreateDirectory(workspace.Data);
        ReferenceDataFixture.Install(workspace.Data);

        await using var isolated = await IsolatedMcpServer.StartAsync(
            workspace.Home,
            workspace.Data,
            cancellationToken: timeout.Token);

        await ServerJobs.AddAsync(
            workspace.Data,
            ServerJobs.Snapshot(
                "job_OVER01",
                JobKinds.PrepareData,
                status: JobStatuses.Succeeded,
                progress: 1.0,
                resultJson: """{"referenceData":[]}"""),
            timeout.Token);

        // The guard is about work in flight, not history: an earlier run must not wedge the tool shut.
        var payload = await ToolCall.OkAsync(
            isolated.Client,
            "prepare_data",
            new Dictionary<string, object?>(),
            isolated.StandardError);

        payload.GetProperty("jobId").GetString().ShouldNotBeNull().ShouldMatch("^job_[0-9A-Za-z]{6}$");
        payload.GetProperty("status").GetString().ShouldBe(JobStatuses.Queued);
    }

    [Fact]
    public async Task An_unknown_state_is_VALIDATION_FAILED_pointing_at_the_entry_that_is_wrong()
    {
        // Validation runs before the job guard and before any file work, so this needs no prepared data
        // and queues nothing on the shared server.
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "prepare_data",
            new Dictionary<string, object?> { ["states"] = new[] { "TX", "ZZ" } },
            server.StandardError);

        error.Code.ShouldBe("VALIDATION_FAILED");

        var detail = error.Details.ShouldHaveSingleItem();
        detail.Pointer.ShouldBe(
            "/states/1",
            "the pointer is the position of the offending entry, not of the first one, so Claude can fix "
            + $"that single element instead of resending the list; got {detail}");
    }

    [Fact]
    public async Task Every_bad_state_gets_its_own_pointer()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "prepare_data",
            new Dictionary<string, object?> { ["states"] = new[] { "QQ", "TX", "Atlantis" } },
            server.StandardError);

        error.Code.ShouldBe("VALIDATION_FAILED");
        error.Details.Select(detail => detail.Pointer).ShouldBe(["/states/0", "/states/2"]);
    }
}
