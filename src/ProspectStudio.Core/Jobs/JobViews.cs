using System.Text.Json.Nodes;

namespace ProspectStudio.Core.Jobs;

/// <summary>
/// What <c>get_job</c> returns (mcp-tools.md §get_job). <see cref="Result"/> is the stored
/// <c>result_json</c> as a JSON document, not a string holding JSON, and is null until the job
/// finishes.
/// </summary>
public sealed record JobDetail(
    string JobId,
    string Kind,
    string Status,
    double Progress,
    string? Message,
    JsonNode? Result);

/// <summary>
/// One row of <c>list_jobs</c>: the same fields as <c>get_job</c> without the result document, which
/// belongs to a single job and would blow the compact-output budget once a list holds several
/// (CLAUDE.md §Hard rules). Call <c>get_job</c> for the result.
/// </summary>
public sealed record JobRow(string JobId, string Kind, string Status, double Progress, string? Message);

/// <summary>The result of <c>list_jobs</c>, newest first.</summary>
public sealed record JobList(IReadOnlyList<JobRow> Jobs);

/// <summary>The result of <c>cancel_job</c>: the status the job ended up in.</summary>
public sealed record JobCancellation(string Status);
