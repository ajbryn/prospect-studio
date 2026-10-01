using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ProspectStudio.Core.Domain;
using ProspectStudio.Core.Jobs;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// EF Core implementation of <see cref="IJobStore"/>. A short-lived context per operation from the
/// factory, so concurrent jobs never share one (technical-design §5.3). State changes are set-based
/// (<c>ExecuteUpdateAsync</c>), which is both provider-neutral and safe when two jobs write at once.
/// </summary>
public sealed class EfJobStore(IDbContextFactory<ProspectDbContext> contextFactory) : IJobStore
{
    private static readonly Expression<Func<Job, JobSnapshot>> _toSnapshot = job => new JobSnapshot(
        job.Id,
        job.Kind,
        job.CampaignId,
        job.Status,
        job.Progress,
        job.Message,
        job.ParametersJson,
        job.ResultJson,
        job.CreatedAt,
        job.StartedAt,
        job.FinishedAt);

    public async Task AddAsync(JobSnapshot job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.Jobs.Add(new Job
        {
            Id = job.JobId,
            Kind = job.Kind,
            CampaignId = job.CampaignId,
            Status = job.Status,
            Progress = job.Progress,
            Message = job.Message,
            ParametersJson = job.ParametersJson,
            ResultJson = job.ResultJson,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            FinishedAt = job.FinishedAt,
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobSnapshot?> FindAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Jobs
            .AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(_toSnapshot)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JobSnapshot>> ListAsync(
        string? campaignId,
        string? status,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = context.Jobs.AsNoTracking();
        if (campaignId is not null)
        {
            query = query.Where(job => job.CampaignId == campaignId);
        }

        if (status is not null)
        {
            query = query.Where(job => job.Status == status);
        }

        return await query
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id)
            .Select(_toSnapshot)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> MarkRunningAsync(string jobId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Queued only: a job cancel_job has already finished must not be started by a worker that
        // picked it out of the queue a moment later.
        return await context.Jobs
            .Where(job => job.Id == jobId && job.Status == JobStatuses.Queued)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, JobStatuses.Running)
                    .SetProperty(job => job.StartedAt, (DateTimeOffset?)startedAt),
                cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    public async Task<bool> UpdateProgressAsync(
        string jobId,
        double progress,
        string? message,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Running only: a report still in flight when the job ends must not overwrite its final state.
        return await context.Jobs
            .Where(job => job.Id == jobId && job.Status == JobStatuses.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Progress, progress)
                    .SetProperty(job => job.Message, message),
                cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    public async Task<bool> FinishAsync(
        string jobId,
        string status,
        double progress,
        string? message,
        string? resultJson,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Unfinished only: work that ignores its cancellation token and returns anyway must not
        // overwrite the 'cancelled' cancel_job already wrote.
        return await context.Jobs
            .Where(job => job.Id == jobId
                && (job.Status == JobStatuses.Queued || job.Status == JobStatuses.Running))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, status)
                    .SetProperty(job => job.Progress, progress)
                    .SetProperty(job => job.Message, message)
                    .SetProperty(job => job.ResultJson, resultJson)
                    .SetProperty(job => job.FinishedAt, (DateTimeOffset?)finishedAt),
                cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    public async Task<int> MarkUnfinishedJobsInterruptedAsync(
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Jobs
            .Where(job => job.Status == JobStatuses.Running || job.Status == JobStatuses.Queued)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, JobStatuses.Interrupted)
                    .SetProperty(job => job.FinishedAt, (DateTimeOffset?)finishedAt),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
