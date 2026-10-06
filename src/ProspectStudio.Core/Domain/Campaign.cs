namespace ProspectStudio.Core.Domain;

/// <summary>
/// A campaign: one search profile, one folder under <c>Campaigns\</c> and one row in the
/// <c>campaigns</c> table (technical-design §5.2). Persistence-ignorant: no EF attributes here, the
/// mapping lives in an <c>IEntityTypeConfiguration&lt;Campaign&gt;</c> in Infrastructure/Storage.
/// </summary>
public sealed class Campaign
{
    /// <summary><c>cmp_</c> plus 6 characters, generated in Core.</summary>
    public required string Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Normalized name, used for duplicate detection instead of case-sensitive text comparison.</summary>
    public required string Slug { get; set; }

    public string? Product { get; set; }

    /// <summary>Whatever the user said about the campaign when creating it.</summary>
    public string? Notes { get; set; }

    /// <summary>Absolute path of the campaign folder (<c>yyyy-MM &lt;Name&gt;</c> under <c>Campaigns\</c>).</summary>
    public required string FolderPath { get; set; }

    public required string Status { get; set; }

    /// <summary>The saved search profile as opaque JSON text; never filtered inside.</summary>
    public string? ProfileJson { get; set; }

    /// <summary>When the profile was last saved, for <c>get_campaign</c>'s <c>profile.savedAt</c>.</summary>
    public DateTimeOffset? ProfileSavedAt { get; set; }

    /// <summary>The resolved geography as opaque JSON text; never filtered inside.</summary>
    public string? GeoJson { get; set; }

    /// <summary>
    /// The effective scoring weights of the last <c>score_leads</c> run, as opaque JSON text
    /// (technical-design §5.2), so <c>save_research</c> re-scores one lead on the same scale as the rest
    /// of the list. Null for a campaign nothing has scored yet - an invented default would claim a scale
    /// nobody chose.
    /// </summary>
    /// <remarks>
    /// A record of the last run, not a sticky setting: a later <c>score_leads</c> with no <c>weights</c>
    /// argument writes the profile's weights back here, so an override is always undone by re-running
    /// without one.
    /// </remarks>
    public string? ScoringWeightsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The campaign lifecycle values stored in <c>campaigns.status</c>.</summary>
public static class CampaignStatuses
{
    public const string Draft = "draft";
}
