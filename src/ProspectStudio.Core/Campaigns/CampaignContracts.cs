namespace ProspectStudio.Core.Campaigns;

/// <summary>The result of <c>create_campaign</c> (mcp-tools.md §Campaign).</summary>
public sealed record CampaignCreated(string CampaignId, string Folder);

/// <summary>One row of <c>list_campaigns</c>.</summary>
public sealed record CampaignRow(
    string CampaignId,
    string Name,
    string Folder,
    string Status,
    string? Product,
    DateTimeOffset CreatedAt,
    int Leads);

public sealed record CampaignPage(int Total, IReadOnlyList<CampaignRow> Campaigns);

/// <summary>
/// The result of <c>get_campaign</c>. <c>GeoLabel</c> stays null until <c>resolve_geography</c>
/// arrives in C2; the raw query is on <see cref="SavedProfileSummary.GeographyQuery"/>.
/// </summary>
public sealed record CampaignDetail(
    string CampaignId,
    string Name,
    string Folder,
    string Status,
    string? Product,
    SavedProfileSummary? Profile,
    string? GeoLabel,
    CampaignCounts Counts,
    DateTimeOffset? LastExportAt,
    DateTimeOffset? LastRenderAt);

public sealed record SavedProfileSummary(string Name, int Segments, string? GeographyQuery, DateTimeOffset SavedAt);

public sealed record CampaignCounts(
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, int> ByTier,
    IReadOnlyList<DealerLeadCount> ByDealer)
{
    /// <summary>A campaign with no leads: empty groupings rather than absent ones.</summary>
    public static CampaignCounts Empty { get; } = new(new Dictionary<string, int>(), new Dictionary<string, int>(), []);
}

public sealed record DealerLeadCount(string DealerId, string Name, int Leads);

/// <summary>The result of <c>save_search_profile</c>.</summary>
public sealed record SearchProfileSaved(bool Saved, string Path, IReadOnlyList<string> Warnings);
