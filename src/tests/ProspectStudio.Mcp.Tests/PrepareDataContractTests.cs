using System.Text.Json;
using ProspectStudio.Core.Jobs;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// POC-1, POC-2 and mcp-tools.md §prepare_data, against a server whose reference data <em>and</em>
/// Overture extracts are already in place. The run must therefore skip every step: a second run that
/// downloaded anything would both break idempotence and put the network inside a unit test - and from
/// C4 onwards that download is the 205 MB Texas Overture extract, not just a census.gov file.
/// </summary>
/// <remarks>
/// These moved off <see cref="ReferenceDataServerFixture"/> in C4, which deliberately has no Overture
/// extract so that <c>find_candidates</c> can be proved to answer <c>NOT_READY</c>.
/// </remarks>
[Collection(CandidateServerCollection.Name)]
public class PrepareDataContractTests(CandidateServerFixture server)
{
    [Fact]
    public async Task Get_status_reports_reference_data_as_ready()
    {
        var ready = (await ToolCall.OkAsync(server.Client, "get_status", diagnostics: server.Diagnostics))
            .GetProperty("ready");

        ready.GetProperty("referenceData").GetBoolean().ShouldBeTrue(
            $"'{ReferenceDataFixture.Directory(server.Data)}' holds counties, CBSA and ZCTA data plus a manifest, "
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
    public async Task Prepare_data_skips_an_Overture_extract_that_is_already_there()
    {
        // Chunk C4 adds the Overture step (technical-design §6.1). It is the one step whose source is a
        // 205 MB download, so "skip what is already done" stops being a nicety and becomes the only
        // reason this test can run offline at all.
        var before = PlacesDataFixture.Fingerprint(server.Data);
        before.ShouldNotBeEmpty($"the fixture extract must be installed at '{PlacesDataFixture.ReleaseDirectory(server.Data)}'.");

        var queued = await ToolCall.OkAsync(
            server.Client,
            "prepare_data",
            new Dictionary<string, object?> { ["states"] = new[] { "TX" } },
            server.Diagnostics);

        var job = await WaitForSuccessAsync(queued.GetProperty("jobId").GetString()!);

        PlacesDataFixture.Fingerprint(server.Data).ShouldBe(
            before,
            "a changed extract means the Overture step re-ran, which means prepare_data downloaded from "
            + "S3 inside a unit test (CLAUDE.md: unit tests must not hit the network).");

        // The step has to be reported, not merely not-run: C2's result deliberately carried a note
        // saying the states bought nothing yet, and leaving that in place would now be a lie.
        var result = job.GetProperty("result");
        result.TryGetProperty("overture", out var overture).ShouldBeTrue(
            "the job result must say what the Overture step did, one entry per state, the same way "
            + $"'referenceData' does for the Census steps. Got: {result}");

        var steps = overture.EnumerateArray().ToList();
        steps.Count.ShouldBe(1, $"one requested state, one entry. Got: {overture}");
        steps[0].GetProperty("skipped").GetBoolean().ShouldBeTrue(
            $"the extract was already there, so the step had nothing to do. Got: {overture}");
    }

    [Fact]
    public async Task Get_status_reports_the_Overture_release_and_the_states_it_holds()
    {
        var overture = (await ToolCall.OkAsync(server.Client, "get_status", diagnostics: server.Diagnostics))
            .GetProperty("ready")
            .GetProperty("overture");

        overture.GetProperty("release").GetString().ShouldBe(
            PlacesDataFixture.Release,
            "mcp-tools.md §get_status shows the release the extracts were taken from.");

        overture.GetProperty("states").EnumerateArray()
            .Select(state => state.GetString())
            .Order(StringComparer.Ordinal)
            .ToList()
            .ShouldBe(["OK", "TX"], "the states with an extract on disk, which is what a skill checks "
                + "before calling find_candidates.");
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
