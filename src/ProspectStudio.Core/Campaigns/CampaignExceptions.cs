using ProspectStudio.Core.SearchProfiles;

namespace ProspectStudio.Core.Campaigns;

/// <summary>
/// Campaign rules broken by the caller. The MCP layer turns each one into the matching error code
/// from mcp-tools.md §Errors; Core stays free of any MCP type.
/// </summary>
public sealed class CampaignNotFoundException(string campaignId)
    : InvalidOperationException($"There is no campaign with id '{campaignId}'.")
{
    public string CampaignId { get; } = campaignId;
}

public sealed class DuplicateCampaignNameException(string name, string existingCampaignId)
    : InvalidOperationException($"A campaign named '{name}' already exists ({existingCampaignId}).")
{
    public string Name { get; } = name;

    public string ExistingCampaignId { get; } = existingCampaignId;
}

public sealed class InvalidCampaignNameException(string message) : ArgumentException(message);

/// <summary>
/// A file in the campaign folder could not be written because something else holds it open - the usual
/// cause being the user having it open in an editor.
/// </summary>
public sealed class CampaignFileLockedException(string path, Exception innerException)
    : IOException($"'{System.IO.Path.GetFileName(path)}' is open in another program.", innerException)
{
    public string FilePath { get; } = path;
}

public sealed class SearchProfileInvalidException(IReadOnlyList<ValidationProblem> problems)
    : InvalidOperationException("The search profile is not valid.")
{
    public IReadOnlyList<ValidationProblem> Problems { get; } = problems;
}
