using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Jobs;
using ProspectStudio.Core.Reference;
using ProspectStudio.Infrastructure.Reference;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

/// <summary>
/// <c>prepare_data</c> from mcp-tools.md §prepare_data: the reference-data pipeline as a background job,
/// because it downloads and parses tens of megabytes and so cannot answer inside a tool call
/// (CLAUDE.md §Hard rules).
/// </summary>
[McpServerToolType]
public sealed class PrepareDataTools(JobRunner jobs, JobService jobLookup, ReferenceDataPreparer reference)
{
    /// <summary>
    /// What a state code buys today, said plainly: the reference data is national, and the per-state
    /// place extract is chunk C4. Without this, a result echoing <c>states: ["TX"]</c> reads as a promise
    /// that state data was prepared.
    /// </summary>
    private const string StatesNote =
        "Reference data is national, so it covers every state. Per-state place data (the Overture "
        + "extract) is not prepared yet; it arrives in chunk C4.";

    [McpServerTool(Name = "prepare_data")]
    [Description("Prepares the reference data the server needs to understand US geography: county boundaries, metro (CBSA) definitions and the ZIP-to-county table. Runs as a background job and returns a jobId to poll with get_job. Steps that are already done are skipped unless force is true.")]
    public async ValueTask<CallToolResult> PrepareDataAsync(
        [Description("Two-letter state codes the campaign covers, as a JSON array of strings: [\"TX\"]. The reference data itself is national; per-state place data arrives in a later chunk.")]
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

        var result = await reference
            .PrepareAsync(force, Report(context), cancellationToken)
            .ConfigureAwait(false);

        var prepared = result.Steps.Count(step => !step.Skipped);
        await context.ReportAsync(
            1.0,
            $"Reference data ready ({prepared} step(s) prepared, {result.Steps.Count - prepared} skipped). "
            + StatesNote,
            cancellationToken).ConfigureAwait(false);

        return new PrepareDataResult(
            states,
            [
                .. result.Steps.Select(step => new PreparedStep(step.Step, step.File, step.Skipped, step.Rows)),
            ],
            result.ManifestPath,
            StatesNote);
    }

    private static ReferenceStepReporter Report(JobContext context) => (step, progress, token) =>
        context.ReportAsync(
            progress,
            $"{step.Step}: {(step.Skipped ? "already prepared" : $"wrote {step.Rows} row(s) to {step.File}")}",
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

/// <param name="Note">What the states did and did not buy, so the echo above cannot be misread.</param>
public sealed record PrepareDataResult(
    IReadOnlyList<string> States,
    IReadOnlyList<PreparedStep> ReferenceData,
    string Manifest,
    string Note);

public sealed record PreparedStep(string Step, string File, bool Skipped, int Rows);
