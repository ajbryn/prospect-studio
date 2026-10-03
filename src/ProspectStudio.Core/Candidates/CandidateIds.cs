using System.Globalization;

namespace ProspectStudio.Core.Candidates;

/// <summary>
/// The ids C4 generates. Technical-design §5.3: string ids are generated in Core, not by the
/// database, so the desktop app and the MCP server mint them the same way.
/// </summary>
public static class CandidateIds
{
    public const string CompanyPrefix = "co_";
    public const string SitePrefix = "st_";
    public const string SourceRecordPrefix = "sr_";

    /// <summary>Lead ids are <c>L0001</c>, numbered per campaign (CLAUDE.md §Conventions).</summary>
    public const string LeadPrefix = "L";

    public static string Company() => CompanyPrefix + Guid.NewGuid().ToString("N")[..16];

    public static string Site() => SitePrefix + Guid.NewGuid().ToString("N")[..16];

    public static string SourceRecord() => SourceRecordPrefix + Guid.NewGuid().ToString("N")[..16];

    public static string Lead(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        return LeadPrefix + number.ToString("D4", CultureInfo.InvariantCulture);
    }

    /// <summary>The number inside a lead id, or 0 when it is not one this class minted.</summary>
    public static int LeadNumber(string? leadId) =>
        leadId is not null
        && leadId.StartsWith(LeadPrefix, StringComparison.Ordinal)
        && int.TryParse(leadId.AsSpan(LeadPrefix.Length), CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
}
