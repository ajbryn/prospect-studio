using ProspectStudio.Core.Jobs;
using Serilog;

namespace ProspectStudio.Mcp.Hosting;

/// <summary>
/// Writes how each background job ended to the log (NFR-7). Without it a failed <c>prepare_data</c> on a
/// user's machine leaves no trace outside <c>jobs.message</c>, while the tool call that queued it logged
/// a successful "finished" - which is worse than silence.
/// </summary>
public static class JobLogging
{
    /// <summary>The observer to hand <see cref="JobRunner"/>.</summary>
    public static JobFinishedObserver Observer { get; } = Report;

    private static void Report(JobOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        switch (outcome.Status)
        {
            case JobStatuses.Failed:
                Log.Error(
                    outcome.Exception,
                    "Job {JobId} ({Kind}) failed: {Reason}",
                    outcome.JobId,
                    outcome.Kind,
                    outcome.Message);
                break;

            case JobStatuses.Interrupted:
            case JobStatuses.Cancelled:
                Log.Warning(
                    "Job {JobId} ({Kind}) ended as {Status}: {Reason}",
                    outcome.JobId,
                    outcome.Kind,
                    outcome.Status,
                    outcome.Message);
                break;

            default:
                Log.Information(
                    "Job {JobId} ({Kind}) {Status}: {Reason}",
                    outcome.JobId,
                    outcome.Kind,
                    outcome.Status,
                    outcome.Message);
                break;
        }
    }
}
