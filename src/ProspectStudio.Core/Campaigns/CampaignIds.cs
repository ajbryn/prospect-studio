using ProspectStudio.Core.Ids;

namespace ProspectStudio.Core.Campaigns;

/// <summary>Campaign ids are <c>cmp_</c> plus 6 characters (CLAUDE.md §Conventions).</summary>
public static class CampaignIds
{
    public const string Prefix = "cmp_";
    public const int CodeLength = 6;

    public static string New() => Prefix + ShortCodes.Generate(CodeLength);

    public static bool IsWellFormed(string? campaignId) =>
        campaignId is not null
        && campaignId.StartsWith(Prefix, StringComparison.Ordinal)
        && ShortCodes.IsWellFormed(campaignId.AsSpan(Prefix.Length), CodeLength);
}
