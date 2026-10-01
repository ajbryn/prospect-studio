using System.Text.Json;
using ProspectStudio.Core.Jobs;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// What <c>get_job</c> and <c>list_jobs</c> return, against job rows seeded into the server's own
/// database. Shapes come from mcp-tools.md §get_job / §list_jobs.
/// </summary>
[Collection(McpServerCollection.Name)]
public class JobToolContractTests(McpServerFixture server)
{
    [Fact]
    public async Task Get_job_returns_the_contract_shape()
    {
        using var timeout = TestTimeout.Start();
        var job = ServerJobs.Snapshot("job_SHAPE1", JobKinds.PrefetchWebsites, campaignId: "cmp_SHAPE1");
        await ServerJobs.AddAsync(server.Data, job, timeout.Token);

        var payload = await ToolCall.OkAsync(
            server.Client,
            "get_job",
            new Dictionary<string, object?> { ["jobId"] = "job_SHAPE1" },
            server.StandardError);

        payload.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["jobId", "kind", "status", "progress", "message", "result"],
            ignoreOrder: true,
            $"mcp-tools.md §get_job documents exactly these fields; got {payload}");

        payload.GetProperty("jobId").GetString().ShouldBe("job_SHAPE1");
        payload.GetProperty("kind").GetString().ShouldBe(JobKinds.PrefetchWebsites);
        payload.GetProperty("status").GetString().ShouldBe(JobStatuses.Running);
        payload.GetProperty("progress").GetDouble().ShouldBe(0.42);
        payload.GetProperty("message").GetString().ShouldBe("Fetched 336/800 sites (12 blocked by robots.txt)");
        payload.GetProperty("result").ValueKind.ShouldBe(
            JsonValueKind.Null,
            "a job that has not finished has no result yet (mcp-tools.md §get_job shows \"result\": null).");
    }

    [Fact]
    public async Task Get_job_returns_the_stored_result_document_once_the_job_has_finished()
    {
        using var timeout = TestTimeout.Start();
        await ServerJobs.AddAsync(
            server.Data,
            ServerJobs.Snapshot(
                "job_DONE02",
                JobKinds.PrepareData,
                status: JobStatuses.Succeeded,
                progress: 1.0,
                message: "Reference data ready",
                resultJson: """{"counties":11,"cbsa":142}"""),
            timeout.Token);

        var payload = await ToolCall.OkAsync(
            server.Client,
            "get_job",
            new Dictionary<string, object?> { ["jobId"] = "job_DONE02" },
            server.StandardError);

        var result = payload.GetProperty("result");
        result.ValueKind.ShouldBe(
            JsonValueKind.Object,
            $"result_json is a JSON document, not a string holding JSON; got {result}");
        result.GetProperty("counties").GetInt32().ShouldBe(11);
    }

    [Fact]
    public async Task Cancel_job_on_a_job_that_already_finished_returns_its_real_terminal_status()
    {
        using var timeout = TestTimeout.Start();
        await ServerJobs.AddAsync(
            server.Data,
            ServerJobs.Snapshot(
                "job_RACE01",
                JobKinds.PrepareData,
                status: JobStatuses.Succeeded,
                progress: 1.0,
                message: "Reference data ready",
                resultJson: """{"counties":11}"""),
            timeout.Token);

        var payload = await ToolCall.OkAsync(
            server.Client,
            "cancel_job",
            new Dictionary<string, object?> { ["jobId"] = "job_RACE01" },
            server.StandardError);

        payload.GetProperty("status").GetString().ShouldBe(
            JobStatuses.Succeeded,
            "mcp-tools.md §cancel_job is idempotent on a finished job and reports that job's actual "
            + "terminal status, so a skill polling a job it just cancelled never meets an exception.");

        var after = await ToolCall.OkAsync(
            server.Client,
            "get_job",
            new Dictionary<string, object?> { ["jobId"] = "job_RACE01" },
            server.StandardError);

        after.GetProperty("status").GetString().ShouldBe(JobStatuses.Succeeded, "and the row is untouched.");
        after.GetProperty("result").GetProperty("counties").GetInt32().ShouldBe(11);
    }

    [Fact]
    public async Task List_jobs_filters_by_campaign_and_by_status()
    {
        using var timeout = TestTimeout.Start();
        const string campaignId = "cmp_LISTME";

        await ServerJobs.AddAsync(
            server.Data,
            ServerJobs.Snapshot("job_LIST01", JobKinds.PrefetchWebsites, campaignId, minute: 10),
            timeout.Token);
        await ServerJobs.AddAsync(
            server.Data,
            ServerJobs.Snapshot("job_LIST02", JobKinds.RenderCampaign, campaignId, status: JobStatuses.Succeeded, progress: 1.0, minute: 20),
            timeout.Token);
        await ServerJobs.AddAsync(
            server.Data,
            ServerJobs.Snapshot("job_LIST03", JobKinds.RenderCampaign, "cmp_OTHERR", minute: 30),
            timeout.Token);

        var mine = await ListJobIdsAsync(new Dictionary<string, object?> { ["campaignId"] = campaignId });
        mine.ShouldBe(
            ["job_LIST02", "job_LIST01"],
            "mcp-tools.md §list_jobs returns them newest first.");

        var running = await ListJobIdsAsync(new Dictionary<string, object?>
        {
            ["campaignId"] = campaignId,
            ["status"] = JobStatuses.Running,
        });
        running.ShouldBe(["job_LIST01"]);

        var everything = await ListJobIdsAsync([]);
        everything.ShouldContain("job_LIST03");
    }

    [Fact]
    public async Task List_jobs_rows_carry_the_same_fields_as_get_job()
    {
        using var timeout = TestTimeout.Start();
        await ServerJobs.AddAsync(
            server.Data,
            ServerJobs.Snapshot("job_ROWS01", JobKinds.PrepareData, campaignId: "cmp_ROWS01"),
            timeout.Token);

        var payload = await ToolCall.OkAsync(
            server.Client,
            "list_jobs",
            new Dictionary<string, object?> { ["campaignId"] = "cmp_ROWS01" },
            server.StandardError);

        var row = payload.GetProperty("jobs").EnumerateArray().ShouldHaveSingleItem();
        var names = row.EnumerateObject().Select(property => property.Name).ToList();
        foreach (var field in new[] { "jobId", "kind", "status", "progress" })
        {
            names.ShouldContain(field, $"a list_jobs row needs '{field}' to be useful; got {row}");
        }
    }

    private async Task<List<string>> ListJobIdsAsync(Dictionary<string, object?> arguments)
    {
        var payload = await ToolCall.OkAsync(server.Client, "list_jobs", arguments, server.StandardError);

        return [.. payload.GetProperty("jobs").EnumerateArray()
            .Select(job => job.GetProperty("jobId").GetString() ?? string.Empty)];
    }
}
