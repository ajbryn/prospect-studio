namespace ProspectStudio.Core.Jobs;

/// <summary>
/// One row of the <c>jobs</c> table (technical-design §5.2) as Core sees it. Persistence-ignorant:
/// the EF mapping lives in Infrastructure/Storage.
/// </summary>
/// <param name="JobId"><c>job_</c> plus 6 characters, as in mcp-tools.md's <c>job_ab12cd</c>.</param>
/// <param name="Status">One of <see cref="JobStatuses"/>.</param>
/// <param name="Progress">0.0 to 1.0.</param>
/// <param name="ParametersJson">The tool arguments as opaque JSON text; never filtered inside.</param>
/// <param name="ResultJson">The job result as opaque JSON text, set when the job finishes.</param>
public sealed record JobSnapshot(
    string JobId,
    string Kind,
    string? CampaignId,
    string Status,
    double Progress,
    string? Message,
    string? ParametersJson,
    string? ResultJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>The values stored in <c>jobs.status</c> (technical-design §5.2).</summary>
public static class JobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// <summary>The server stopped while the job was <see cref="Running"/> (technical-design §8).</summary>
    public const string Interrupted = "interrupted";

    public const string Cancelled = "cancelled";

    /// <summary>A job in one of these states will never change again.</summary>
    public static bool IsTerminal(string status) =>
        status is Succeeded or Failed or Interrupted or Cancelled;
}

/// <summary>The job kinds from technical-design §8.</summary>
public static class JobKinds
{
    public const string PrepareData = "prepare_data";
    public const string PrefetchWebsites = "prefetch_websites";
    public const string RenderCampaign = "render_campaign";
    public const string BuildDealerPackets = "build_dealer_packets";
}

/// <summary>
/// Job persistence. Core defines it, Infrastructure implements it with EF Core against the
/// <c>jobs</c> table, and <c>AddProspectStudioStorage</c> registers it.
/// </summary>
public interface IJobStore
{
    Task AddAsync(JobSnapshot job, CancellationToken cancellationToken);

    Task<JobSnapshot?> FindAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Newest first. Both filters are optional, which is how <c>list_jobs</c> takes them.
    /// </summary>
    Task<IReadOnlyList<JobSnapshot>> ListAsync(string? campaignId, string? status, CancellationToken cancellationToken);

    /// <summary>Moves a queued job to <see cref="JobStatuses.Running"/>. False when it is gone.</summary>
    Task<bool> MarkRunningAsync(string jobId, DateTimeOffset startedAt, CancellationToken cancellationToken);

    Task<bool> UpdateProgressAsync(string jobId, double progress, string? message, CancellationToken cancellationToken);

    /// <summary>Writes a terminal status, the final progress and the result document.</summary>
    Task<bool> FinishAsync(
        string jobId,
        string status,
        double progress,
        string? message,
        string? resultJson,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Turns every <see cref="JobStatuses.Running"/> <em>and</em> <see cref="JobStatuses.Queued"/> row
    /// into <see cref="JobStatuses.Interrupted"/> and returns how many. Called once at startup: nothing
    /// is running when the process begins, and the queue itself lives in memory, so a queued row has
    /// nothing left to run it either (technical-design §8, mcp-tools.md §get_job).
    /// </summary>
    Task<int> MarkUnfinishedJobsInterruptedAsync(DateTimeOffset finishedAt, CancellationToken cancellationToken);
}
