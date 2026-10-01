namespace ProspectStudio.Core.Jobs;

/// <summary>
/// How one job ended, for whoever is watching the runner.
/// </summary>
/// <param name="Status">The terminal <see cref="JobStatuses"/> value that was written.</param>
/// <param name="Message">What the job last said, or why it failed.</param>
/// <param name="Exception">The failure, for a <see cref="JobStatuses.Failed"/> job only.</param>
public sealed record JobOutcome(
    string JobId,
    string Kind,
    string Status,
    string? Message,
    Exception? Exception);

/// <summary>
/// Told how each job ended so the host can log it (NFR-7). Without this the only trace of a failed
/// background job is <c>jobs.message</c>, which no log file and no support request ever sees - while the
/// tool call that queued the job logged a perfectly successful "finished".
/// </summary>
/// <remarks>
/// A delegate rather than an <c>ILogger</c> keeps Core free of a logging package: the MCP host passes a
/// Serilog-backed lambda, and the runner behaves exactly as before when nobody is watching.
/// </remarks>
public delegate void JobFinishedObserver(JobOutcome outcome);
