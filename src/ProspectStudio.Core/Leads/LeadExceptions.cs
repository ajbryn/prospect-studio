using ProspectStudio.Core.SearchProfiles;

namespace ProspectStudio.Core.Leads;

/// <summary>
/// Lead rules broken by the caller. The MCP layer turns each one into the matching code from
/// mcp-tools.md §Errors; Core stays free of any MCP type.
/// </summary>
public sealed class LeadNotFoundException(string campaignId, string leadId)
    : InvalidOperationException($"There is no lead '{leadId}' in campaign '{campaignId}'.")
{
    public string CampaignId { get; } = campaignId;

    public string LeadId { get; } = leadId;
}

/// <summary>A filter or argument the lead tools cannot honour: <c>VALIDATION_FAILED</c>.</summary>
public sealed class LeadRequestException(string message, string? hint = null)
    : ArgumentException(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>
/// A research document that does not match <c>schemas/research.schema.json</c>. The problems become the
/// <c>details[]</c> of a <c>VALIDATION_FAILED</c>, so Claude can name the field to fix.
/// </summary>
public sealed class ResearchInvalidException(IReadOnlyList<ValidationProblem> problems)
    : InvalidOperationException("The research document is not valid.")
{
    public IReadOnlyList<ValidationProblem> Problems { get; } = problems;
}

/// <summary>
/// Scoring weights that break §7.6's sum rule. Separate from
/// <see cref="ResearchInvalidException"/> only because the pointer is <c>/weights</c> and the hint
/// differs; both become <c>VALIDATION_FAILED</c> with details.
/// </summary>
public sealed class ScoringWeightsInvalidException(IReadOnlyList<ValidationProblem> problems)
    : InvalidOperationException("The scoring weights are not valid.")
{
    public IReadOnlyList<ValidationProblem> Problems { get; } = problems;
}
