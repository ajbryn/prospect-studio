using System.Text.Json;
using ProspectStudio.Core.Jobs;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// POC-1, POC-2 and mcp-tools.md §prepare_data, against a server whose reference data is already in
/// place. The run must therefore skip every step: a second run that downloads anything would both
/// break idempotence and put the network inside a unit test. The Overture step belongs to C4, so
/// nothing may appear under <c>overture\</c> yet.
/// </summary>
[Collection(ReferenceDataServerCollection.Name)]
public class PrepareDataContractTests(ReferenceDataServerFixture server)
{
    [Fact]
    public async Task Get_status_reports_reference_data_as_ready()
    {
        var ready = (await ToolCall.OkAsync(server.Client, "get_status", diagnostics: server.Diagnostics))
            .GetProperty("ready");

        ready.GetProperty("referenceData").GetBoolean().ShouldBeTrue(
            $"'{server.ReferenceDataDirectory}' holds counties, CBSA and ZCTA data plus a manifest, "
            + "so POC-1's readiness flag is true.");
    }

    [Fact]
    public async Task Prepare_data_returns_a_job_id_and_queues_the_job()
    {
        var payload = await ToolCall.OkAsync(
            server.Client,
            "prepare_data",
            new Dictionary<string, object?> { ["states"] = new[] { "TX" } },
            server.Diagnostics);

        payload.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["jobId", "status"],
            ignoreOrder: true,
            $"mcp-tools.md §prepare_data returns {{jobId, status}}; got {payload}");

        payload.GetProperty("jobId").GetString().ShouldNotBeNull().ShouldMatch("^job_[0-9A-Za-z]{6}$");
        payload.GetProperty("status").GetString().ShouldBe(
            JobStatuses.Queued,
            "a long-running tool returns the job it just queued (mcp-tools.md §Conventions).");

        await WaitForSuccessAsync(payload.GetProperty("jobId").GetString()!);
    }

    [Fact]
    public async Task Prepare_data_skips_reference_data_that_is_already_there()
    {
        var before = ReferenceDataFixture.Fingerprint(server.Data);
        before.ShouldAllBe(file => file.Exists);

        var queued = await ToolCall.OkAsync(
            server.Client,
            "prepare_data",
            new Dictionary<string, object?> { ["states"] = new[] { "TX" }, ["force"] = false },
            server.Diagnostics);

        var job = await WaitForSuccessAsync(queued.GetProperty("jobId").GetString()!);

        job.GetProperty("progress").GetDouble().ShouldBe(1.0);

        ReferenceDataFixture.Fingerprint(server.Data).ShouldBe(
            before,
            "every reference step must skip work it has already done (mcp-tools.md §prepare_data). "
            + "A changed file means the step downloaded its source again - which is also a unit test "
            + "reaching the network.");
    }

    [Fact]
    public async Task A_valid_state_code_comes_back_in_the_USPS_form_however_it_was_typed()
    {
        var queued = await ToolCall.OkAsync(
            server.Client,
            "prepare_data",
            new Dictionary<string, object?> { ["states"] = new[] { "tx", "Oklahoma" } },
            server.Diagnostics);

        var job = await WaitForSuccessAsync(queued.GetProperty("jobId").GetString()!);

        job.GetProperty("result").GetProperty("states").EnumerateArray()
            .Select(state => state.GetString())
            .ShouldBe(
                ["OK", "TX"],
                "a state is accepted by name or code and echoed in the two-letter USPS form, so the "
                + "result cannot read back as a different input than the one that was prepared.");
    }

    [Fact]
    public async Task Prepare_data_does_not_extract_Overture_yet()
    {
        var queued = await ToolCall.OkAsync(
            server.Client,
            "prepare_data",
            new Dictionary<string, object?> { ["states"] = new[] { "TX" } },
            server.Diagnostics);

        await WaitForSuccessAsync(queued.GetProperty("jobId").GetString()!);

        var overture = Path.Combine(server.Data, "overture");
        if (Directory.Exists(overture))
        {
            Directory.EnumerateFileSystemEntries(overture).ShouldBeEmpty(
                "the Overture extract is chunk C4; C2's prepare_data covers reference data only.");
        }
    }

    [Fact]
    public async Task The_queued_job_shows_up_in_list_jobs()
    {
        var queued = await ToolCall.OkAsync(
            server.Client,
            "prepare_data",
            new Dictionary<string, object?> { ["states"] = new[] { "TX" } },
            server.Diagnostics);

        var jobId = queued.GetProperty("jobId").GetString()!;
        await WaitForSuccessAsync(jobId);

        var listed = await ToolCall.OkAsync(
            server.Client,
            "list_jobs",
            new Dictionary<string, object?> { ["status"] = JobStatuses.Succeeded },
            server.Diagnostics);

        listed.GetProperty("jobs").EnumerateArray()
            .Select(job => job.GetProperty("jobId").GetString())
            .ShouldContain(jobId);

        listed.GetProperty("jobs").EnumerateArray()
            .Select(job => job.GetProperty("kind").GetString())
            .ShouldContain(JobKinds.PrepareData);
    }

    private async Task<JsonElement> WaitForSuccessAsync(string jobId)
    {
        JsonElement job = default;

        await Eventually.UntilAsync(
            async () =>
            {
                job = await ToolCall.OkAsync(
                    server.Client,
                    "get_job",
                    new Dictionary<string, object?> { ["jobId"] = jobId },
                    server.Diagnostics);

                return JobStatuses.IsTerminal(job.GetProperty("status").GetString() ?? string.Empty);
            },
            $"prepare_data job '{jobId}' to finish",
            TimeSpan.FromSeconds(60));

        job.GetProperty("status").GetString().ShouldBe(
            JobStatuses.Succeeded,
            $"the job finished as '{job.GetProperty("status").GetString()}': {job}{Environment.NewLine}{server.Diagnostics}");

        return job;
    }
}
