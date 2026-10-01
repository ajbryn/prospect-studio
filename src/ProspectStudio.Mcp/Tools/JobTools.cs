using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Jobs;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

/// <summary>
/// The job tools from mcp-tools.md §get_job / §list_jobs / §cancel_job. Every rule lives in
/// <see cref="JobService"/>; this class only maps arguments in and results or error codes out.
/// </summary>
[McpServerToolType]
public sealed class JobTools(JobService jobs)
{
    private const string NotFoundHint = "Call list_jobs to see which jobs exist.";

    [McpServerTool(Name = "get_job")]
    [Description("Reports one background job: its kind, status (queued, running, succeeded, failed, interrupted or cancelled), progress from 0 to 1, latest message and, once it has finished, its result. Use it to poll a job after a tool returned a jobId.")]
    public async ValueTask<CallToolResult> GetJobAsync(
        [Description("The job id a long-running tool returned, for example 'job_ab12cd'.")]
        string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(await jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false));
        }
        catch (JobNotFoundException exception)
        {
            throw new McpToolException(ToolErrorCodes.NotFound, exception.Message, NotFoundHint);
        }
    }

    [McpServerTool(Name = "list_jobs")]
    [Description("Lists background jobs, newest first, optionally only those for one campaign or in one status. Use it to find a job id, or to check whether a job was left interrupted by a server restart.")]
    public async ValueTask<CallToolResult> ListJobsAsync(
        [Description("Only jobs for this campaign, for example 'cmp_7Q3KXM'. Omit for every job.")]
        string? campaignId = null,
        [Description("Only jobs in this status: queued, running, succeeded, failed, interrupted or cancelled.")]
        string? status = null,
        CancellationToken cancellationToken = default) =>
        ToolResults.Ok(await jobs.ListAsync(campaignId, status, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "cancel_job")]
    [Description("Asks a queued or running background job to stop and reports the status it ended in. Safe to call on a job that has already finished: it then reports that job's own final status instead of failing.")]
    public async ValueTask<CallToolResult> CancelJobAsync(
        [Description("The job id to cancel, for example 'job_ab12cd'.")]
        string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(await jobs.CancelAsync(jobId, cancellationToken).ConfigureAwait(false));
        }
        catch (JobNotFoundException exception)
        {
            throw new McpToolException(ToolErrorCodes.NotFound, exception.Message, NotFoundHint);
        }
    }
}
