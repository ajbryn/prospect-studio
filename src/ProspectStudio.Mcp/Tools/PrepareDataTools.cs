using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Jobs;
using ProspectStudio.Core.Reference;
using ProspectStudio.Infrastructure.Overture;
using ProspectStudio.Infrastructure.Reference;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

/// <summary>
/// <c>prepare_data</c> from mcp-tools.md §prepare_data: the reference-data pipeline as a background job,
/// because it downloads and parses tens of megabytes and so cannot answer inside a tool call
/// (CLAUDE.md §Hard rules).
/// </summary>
[McpServerToolType]
public sealed class PrepareDataTools(
    JobRunner jobs,
    JobService jobLookup,
    ReferenceDataPreparer reference,
    OvertureDataPreparer overture)
{
    /// <summary>
    /// What a state code buys, said plainly: the reference data is national and the per-state place
    /// extract is the Overture step. Without this, a result echoing <c>states: ["TX"]</c> reads as a
    /// promise that more was prepared than was.
    /// </summary>
    private const string StatesNote =
        "Reference data is national, so it covers every state. The Overture place extract is per state "
        + "and is listed under 'overture'.";

    [McpServerTool(Name = "prepare_data")]
    [Description("Prepares the data the server needs to find leads: county boundaries, metro (CBSA) definitions, the ZIP-to-county table and an Overture Places extract for each state you name. Runs as a background job and returns a jobId to poll with get_job. Steps that are already done are skipped unless force is true.")]
    public async ValueTask<CallToolResult> PrepareDataAsync(
        [Description("Two-letter state codes the campaign covers, as a JSON array of strings: [\"TX\"]. The reference data itself is national; each state named here also gets an Overture Places extract, which is a large download the first time.")]
        string[]? states = null,
        [Description("Redo steps that are already done, for example after a new Census vintage.")]
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var wanted = Validate(states);

        // Two runs would write counties.parquet and the CSVs at the same time, and a half-written file
        // still has content - so the next run would skip it and the corruption would be permanent.
        if (await jobLookup.FindUnfinishedAsync(JobKinds.PrepareData, null, cancellationToken).ConfigureAwait(false)
            is { } running)
        {
            throw new McpToolException(
                ToolErrorCodes.JobRunning,
                $"A prepare_data job ({running}) has not finished yet.",
                $"Wait for {running}.");
        }

        var parameters = JsonSerializer.Serialize(new { states = wanted, force }, ToolResults.Json);

        var jobId = await jobs.EnqueueAsync(
            JobKinds.PrepareData,
            (context, token) => RunAsync(context, wanted, force, token),
            campaignId: null,
            parameters,
            cancellationToken).ConfigureAwait(false);

        return ToolResults.Ok(new QueuedJob(jobId, JobStatuses.Queued));
    }

    private async Task<object?> RunAsync(
        JobContext context,
        IReadOnlyList<string> states,
        bool force,
        CancellationToken cancellationToken)
    {
        await context.ReportAsync(0, "Preparing reference data", cancellationToken).ConfigureAwait(false);

        // The reference half runs first: the Overture extract's bbox pre-filter is built from the county
        // geometry, so there is nothing to narrow to until counties.parquet exists.
        var result = await reference
            .PrepareAsync(force, ReportReference(context), cancellationToken)
            .ConfigureAwait(false);

        var extracts = await overture
            .PrepareAsync(states, force, ReportOverture(context), cancellationToken)
            .ConfigureAwait(false);

        var prepared = result.Steps.Count(step => !step.Skipped) + extracts.Count(step => !step.Skipped);
        var skipped = result.Steps.Count + extracts.Count - prepared;
        await context.ReportAsync(
            1.0,
            $"Data ready ({prepared} step(s) prepared, {skipped} skipped). " + StatesNote,
            cancellationToken).ConfigureAwait(false);

        return new PrepareDataResult(
            states,
            [
                .. result.Steps.Select(step => new PreparedStep(step.Step, step.File, step.Skipped, step.Rows)),
            ],
            [
                .. extracts.Select(step => new PreparedExtract(
                    step.State,
                    step.File,
                    step.Skipped,
                    step.Rows,
                    step.Release)),
            ],
            result.ManifestPath,
            StatesNote);
    }

    /// <summary>How much of the job's progress bar the three reference steps own.</summary>
    private const double ReferenceShare = 0.5;

    private static ReferenceStepReporter ReportReference(JobContext context) =>
        (step, progress, token) => context.ReportAsync(
            progress * ReferenceShare,
            $"{step.Step}: {(step.Skipped ? "already prepared" : $"wrote {step.Rows} row(s) to {step.File}")}",
            token);

    private static OvertureStepReporter ReportOverture(JobContext context) =>
        (step, progress, token) => context.ReportAsync(
            ReferenceShare + (progress * (1 - ReferenceShare)),
            $"{step.Step} {step.State}: "
            + (step.Skipped ? "already prepared" : $"wrote {step.Rows} place(s) to {step.File}"),
            token);

    /// <summary>
    /// Normalizes the state codes and refuses one that is not a US state, rather than accepting it and
    /// echoing it back as though it meant something (mcp-tools.md §Errors, <c>VALIDATION_FAILED</c>).
    /// </summary>
    private static IReadOnlyList<string> Validate(string[]? states)
    {
        var given = (states ?? [])
            .Where(state => !string.IsNullOrWhiteSpace(state))
            .Select(state => state.Trim())
            .ToList();

        var problems = new List<ToolErrorDetail>();
        var resolved = new List<string>(given.Count);

        for (var index = 0; index < given.Count; index++)
        {
            if (UsStates.Find(given[index]) is { } state)
            {
                resolved.Add(state.Abbreviation);
            }
            else
            {
                problems.Add(new ToolErrorDetail(
                    $"/states/{index}",
                    $"'{given[index]}' is not a US state name or two-letter code."));
            }
        }

        if (problems.Count > 0)
        {
            throw new McpToolException(
                ToolErrorCodes.ValidationFailed,
                "states holds something that is not a US state.",
                "Pass two-letter codes as a JSON array, such as [\"TX\", \"OK\"], or omit states entirely.",
                problems);
        }

        return [.. resolved.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}

/// <summary>What a tool returns when it has queued a background job (mcp-tools.md §Conventions).</summary>
public sealed record QueuedJob(string JobId, string Status);

/// <param name="Overture">One entry per requested state, so a skipped extract is reported rather than silent.</param>
/// <param name="Note">What the states did and did not buy, so the echo above cannot be misread.</param>
public sealed record PrepareDataResult(
    IReadOnlyList<string> States,
    IReadOnlyList<PreparedStep> ReferenceData,
    IReadOnlyList<PreparedExtract> Overture,
    string Manifest,
    string Note);

public sealed record PreparedStep(string Step, string File, bool Skipped, int Rows);

public sealed record PreparedExtract(string State, string File, bool Skipped, int Rows, string Release);
