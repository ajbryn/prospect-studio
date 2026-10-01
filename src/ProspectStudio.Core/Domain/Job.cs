namespace ProspectStudio.Core.Domain;

/// <summary>
/// A background job: one row in the <c>jobs</c> table (technical-design §5.2). Persistence-ignorant,
/// like <see cref="Campaign"/>: the mapping lives in an <c>IEntityTypeConfiguration&lt;Job&gt;</c> in
/// Infrastructure/Storage, and <c>ProspectStudio.Core.Jobs.JobSnapshot</c> is the value the rest of
/// the code passes around.
/// </summary>
public sealed class Job
{
    /// <summary><c>job_</c> plus 6 characters, generated in Core.</summary>
    public required string Id { get; set; }

    /// <summary>One of <c>ProspectStudio.Core.Jobs.JobKinds</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The campaign the job belongs to, or null for a server-wide job such as setup.</summary>
    public string? CampaignId { get; set; }

    /// <summary>One of <c>ProspectStudio.Core.Jobs.JobStatuses</c>.</summary>
    public required string Status { get; set; }

    /// <summary>0.0 to 1.0.</summary>
    public double Progress { get; set; }

    /// <summary>What the job last said about itself, for <c>get_job</c>'s <c>message</c>.</summary>
    public string? Message { get; set; }

    /// <summary>The tool arguments as opaque JSON text; never filtered inside.</summary>
    public string? ParametersJson { get; set; }

    /// <summary>The job result as opaque JSON text, set when the job finishes.</summary>
    public string? ResultJson { get; set; }

    /// <summary>
    /// When the job was queued. <c>list_jobs</c> orders by it, which <c>started_at</c> cannot do: a
    /// queued job has not started.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}
