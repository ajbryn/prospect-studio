using System.Text.Json;
using ProspectStudio.Core.Domain;
using ProspectStudio.Core.SearchProfiles;

namespace ProspectStudio.Core.Campaigns;

/// <summary>
/// The campaign use cases behind <c>create_campaign</c>, <c>list_campaigns</c>, <c>get_campaign</c>
/// and <c>save_search_profile</c>. All of the rules live here so the MCP layer only maps arguments.
/// </summary>
public sealed class CampaignService(
    ICampaignStore store,
    ICampaignWorkspace workspace,
    SearchProfileValidator validator,
    TimeProvider clock)
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;
    private const int IdAttempts = 8;

    public async Task<CampaignCreated> CreateAsync(
        string name,
        string? product,
        string? notes,
        CancellationToken cancellationToken)
    {
        var campaignName = (name ?? string.Empty).Trim();
        if (campaignName.Length == 0)
        {
            throw new InvalidCampaignNameException("A campaign name is required.");
        }

        var id = await NewIdAsync(cancellationToken).ConfigureAwait(false);
        var slug = CampaignNaming.Slug(campaignName);
        if (slug.Length == 0)
        {
            // A name of nothing but punctuation has no slug to collide on.
            slug = id.ToLowerInvariant();
        }
        else
        {
            var existing = await store.FindBySlugAsync(slug, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                throw new DuplicateCampaignNameException(existing.Name, existing.Id);
            }
        }

        var folderName = CampaignNaming.FolderName(campaignName, clock.GetLocalNow());
        var folder = await workspace.CreateCampaignFolderAsync(folderName, cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        try
        {
            await store.AddAsync(
                new Campaign
                {
                    Id = id,
                    Name = campaignName,
                    Slug = slug,
                    Product = Trimmed(product),
                    Notes = Trimmed(notes),
                    FolderPath = folder,
                    Status = CampaignStatuses.Draft,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Without this, a failed insert leaves an empty folder behind and the retry the caller is
            // about to make gets "<Name> (2)" beside it.
            await workspace.DeleteCampaignFolderIfEmptyAsync(folder, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new CampaignCreated(id, folder);
    }

    public async Task<CampaignPage> ListAsync(int limit, int offset, CancellationToken cancellationToken) =>
        await store.ListAsync(
            limit <= 0 ? DefaultPageSize : Math.Min(limit, MaxPageSize),
            Math.Max(offset, 0),
            cancellationToken).ConfigureAwait(false);

    public async Task<CampaignDetail> GetAsync(string campaignId, CancellationToken cancellationToken)
    {
        var campaign = await store.FindAsync(campaignId, cancellationToken).ConfigureAwait(false)
            ?? throw new CampaignNotFoundException(campaignId);

        return new CampaignDetail(
            campaign.Id,
            campaign.Name,
            campaign.FolderPath,
            campaign.Status,
            campaign.Product,
            Summarize(campaign),
            GeoLabel: CampaignGeography.Label(campaign.GeoJson),
            await store.GetCountsAsync(campaign.Id, cancellationToken).ConfigureAwait(false),
            LastExportAt: null,
            LastRenderAt: null);
    }

    /// <summary>
    /// Validates the profile, writes <c>search-profile.json</c> into the campaign folder and stores the
    /// document. A rejected profile is never written, so the last valid one survives.
    /// </summary>
    public async Task<SearchProfileSaved> SaveProfileAsync(
        string campaignId,
        JsonElement profile,
        CancellationToken cancellationToken)
    {
        var campaign = await store.FindAsync(campaignId, cancellationToken).ConfigureAwait(false)
            ?? throw new CampaignNotFoundException(campaignId);

        var problems = validator.Validate(profile);
        if (problems.Count > 0)
        {
            throw new SearchProfileInvalidException(problems);
        }

        var savedAt = clock.GetUtcNow();
        var previousProfile = campaign.ProfileJson;
        var previousSavedAt = campaign.ProfileSavedAt;

        var stored = await store
            .SaveProfileAsync(campaign.Id, profile.GetRawText(), savedAt, savedAt, cancellationToken)
            .ConfigureAwait(false);
        if (!stored)
        {
            throw new CampaignNotFoundException(campaignId);
        }

        string path;
        try
        {
            path = await workspace
                .WriteSearchProfileAsync(campaign.FolderPath, profile, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The row must not claim a profile the campaign folder does not hold, so put it back.
            // A failure here must not mask why the save actually failed, which was the file write.
            try
            {
                // A false result means the row is already gone, so there is nothing left to restore.
                _ = await store
                    .SaveProfileAsync(campaign.Id, previousProfile, previousSavedAt, savedAt, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Swallowed deliberately: the original exception below is the one worth reporting.
            }

            throw;
        }

        return new SearchProfileSaved(true, path, SearchProfileFacts.Warnings(profile));
    }

    private static SavedProfileSummary? Summarize(Campaign campaign)
    {
        if (string.IsNullOrWhiteSpace(campaign.ProfileJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(campaign.ProfileJson);
        var profile = document.RootElement;

        return new SavedProfileSummary(
            SearchProfileFacts.Name(profile),
            SearchProfileFacts.SegmentCount(profile),
            SearchProfileFacts.GeographyQuery(profile),
            campaign.ProfileSavedAt ?? campaign.UpdatedAt);
    }

    private async Task<string> NewIdAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < IdAttempts; attempt++)
        {
            var id = CampaignIds.New();
            if (!await store.ExistsAsync(id, cancellationToken).ConfigureAwait(false))
            {
                return id;
            }
        }

        throw new InvalidOperationException($"Could not generate an unused campaign id in {IdAttempts} attempts.");
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
