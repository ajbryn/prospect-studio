using System.Globalization;
using System.Text.Json;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Configuration;

namespace ProspectStudio.Infrastructure.Workspace;

/// <summary>
/// The campaign folders under <c>PROSPECT_STUDIO_HOME\Campaigns</c> (technical-design §5.1).
/// </summary>
public sealed class FileSystemCampaignWorkspace(PsOptions options) : ICampaignWorkspace
{
    public const string SearchProfileFileName = "search-profile.json";

    private const int MaxFolderAttempts = 50;

    private static readonly JsonSerializerOptions Readable = new() { WriteIndented = true };

    public string CampaignsDirectory => Path.Combine(options.Home, WorkspaceBootstrapper.CampaignsFolder);

    public Task<string> CreateCampaignFolderAsync(string folderName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(CampaignsDirectory);

        for (var attempt = 1; attempt <= MaxFolderAttempts; attempt++)
        {
            var candidate = attempt == 1
                ? folderName
                : string.Create(CultureInfo.InvariantCulture, $"{folderName} ({attempt})");
            var path = Path.Combine(CampaignsDirectory, candidate);

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                return Task.FromResult(path);
            }
        }

        throw new IOException($"Could not find an unused campaign folder name for '{folderName}'.");
    }

    public async Task<string> WriteSearchProfileAsync(
        string campaignFolderPath,
        JsonElement profile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignFolderPath);

        Directory.CreateDirectory(campaignFolderPath);
        var path = Path.Combine(campaignFolderPath, SearchProfileFileName);

        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile, Readable), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException exception) when (IsHeldOpen(path))
        {
            throw new CampaignFileLockedException(path, exception);
        }

        return path;
    }

    /// <summary>
    /// Whether the file is held open by something else. Asked by trying to open it exclusively rather
    /// than by reading an error code, which would differ per platform: if the write failed and the file
    /// cannot be opened either, another program has it. A write that failed for any other reason (no
    /// space, no permission) opens fine here and keeps its original exception.
    /// </summary>
    private static bool IsHeldOpen(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Task<bool> DeleteCampaignFolderIfEmptyAsync(string campaignFolderPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignFolderPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(campaignFolderPath)
            || Directory.EnumerateFileSystemEntries(campaignFolderPath).Any())
        {
            return Task.FromResult(false);
        }

        try
        {
            Directory.Delete(campaignFolderPath);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An empty folder left behind is untidy, not a failure worth replacing the real error with.
            return Task.FromResult(false);
        }
    }
}
