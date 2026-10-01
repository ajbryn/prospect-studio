using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProspectStudio.Core.Jobs;

/// <summary>
/// The rules behind <c>get_job</c>, <c>list_jobs</c> and <c>cancel_job</c> (mcp-tools.md §get_job).
/// Reads come from the <see cref="IJobStore"/>; cancelling goes through the <see cref="JobRunner"/>,
/// which also has to signal the work.
/// </summary>
public sealed class JobService(IJobStore store, JobRunner runner)
{
    public async Task<JobDetail> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = await store.FindAsync(Required(jobId), cancellationToken).ConfigureAwait(false)
            ?? throw new JobNotFoundException(jobId);

        return new JobDetail(job.JobId, job.Kind, job.Status, job.Progress, job.Message, Document(job.ResultJson));
    }

    public async Task<JobList> ListAsync(string? campaignId, string? status, CancellationToken cancellationToken)
    {
        var jobs = await store
            .ListAsync(Filter(campaignId), Filter(status)?.ToLowerInvariant(), cancellationToken)
            .ConfigureAwait(false);

        return new JobList([.. jobs.Select(job => new JobRow(job.JobId, job.Kind, job.Status, job.Progress, job.Message))]);
    }

    /// <summary>
    /// The id of a job of this kind that is still running or queued, or null when none is. A tool whose
    /// job writes shared files calls this before queueing a second one, which is <c>JOB_RUNNING</c>.
    /// </summary>
    public async Task<string?> FindUnfinishedAsync(string kind, string? campaignId, CancellationToken cancellationToken)
    {
        // Running first, so the id reported is the job actually holding the files.
        foreach (var status in new[] { JobStatuses.Running, JobStatuses.Queued })
        {
            var jobs = await store.ListAsync(Filter(campaignId), status, cancellationToken).ConfigureAwait(false);
            if (jobs.FirstOrDefault(job => string.Equals(job.Kind, kind, StringComparison.Ordinal)) is { } match)
            {
                return match.JobId;
            }
        }

        return null;
    }

    public async Task<JobCancellation> CancelAsync(string jobId, CancellationToken cancellationToken)
    {
        var status = await runner.CancelAsync(Required(jobId), cancellationToken).ConfigureAwait(false)
            ?? throw new JobNotFoundException(jobId);

        return new JobCancellation(status);
    }

    private static string Required(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return jobId.Trim();
    }

    private static string? Filter(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The stored result as a document, so <c>get_job</c>'s <c>result</c> is JSON rather than a string
    /// holding JSON. Text that is not JSON at all is reported as no result rather than failing the read.
    /// </summary>
    private static JsonNode? Document(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(resultJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
