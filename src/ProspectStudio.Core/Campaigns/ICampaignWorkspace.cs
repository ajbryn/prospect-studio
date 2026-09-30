using System.Text.Json;

namespace ProspectStudio.Core.Campaigns;

/// <summary>
/// The campaign's files on disk. Core decides what belongs in a campaign folder; Infrastructure owns
/// the file system.
/// </summary>
public interface ICampaignWorkspace
{
    /// <summary>
    /// Creates the campaign folder under the workspace's <c>Campaigns</c> folder and returns its
    /// absolute path. When the folder name is already taken, a distinct one is used.
    /// </summary>
    Task<string> CreateCampaignFolderAsync(string folderName, CancellationToken cancellationToken);

    /// <summary>Writes <c>search-profile.json</c> into the campaign folder and returns its path.</summary>
    Task<string> WriteSearchProfileAsync(
        string campaignFolderPath,
        JsonElement profile,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a campaign folder that was created moments ago but whose row could not be stored, so a
    /// retry finds the name free instead of landing in "&lt;Name&gt; (2)". Only ever deletes an empty
    /// folder: anything the user may have put there stays.
    /// </summary>
    Task<bool> DeleteCampaignFolderIfEmptyAsync(string campaignFolderPath, CancellationToken cancellationToken);
}
