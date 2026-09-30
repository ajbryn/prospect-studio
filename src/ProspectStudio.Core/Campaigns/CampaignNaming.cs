using System.Buffers;
using System.Globalization;
using System.Text;

namespace ProspectStudio.Core.Campaigns;

/// <summary>
/// Turns a campaign name into the two derived forms it needs: a slug for case-insensitive duplicate
/// detection, and the <c>yyyy-MM &lt;Name&gt;</c> folder name from implementation-plan C1.
/// </summary>
public static class CampaignNaming
{
    public const int MaxFolderNameLength = 120;

    private static readonly SearchValues<char> InvalidFolderCharacters =
        SearchValues.Create(new string(Path.GetInvalidFileNameChars()));

    /// <summary>
    /// Lower-cased, hyphen-separated letters and digits. Duplicate detection compares slugs, so
    /// "Houston Test" and "houston test" collide without relying on the database's text comparison
    /// (CLAUDE.md §Conventions, provider neutrality).
    /// </summary>
    public static string Slug(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var slug = new StringBuilder(name.Length);
        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().TrimEnd('-');
    }

    public static string FolderName(string name, DateTimeOffset when) =>
        $"{when.ToString("yyyy-MM", CultureInfo.InvariantCulture)} {SafeSegment(name)}";

    /// <summary>
    /// The name with the characters Windows forbids in a folder name removed, runs of whitespace
    /// collapsed, and the trailing dots and spaces Windows silently drops trimmed off.
    /// </summary>
    public static string SafeSegment(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var segment = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (InvalidFolderCharacters.Contains(character))
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (segment.Length > 0 && segment[^1] != ' ')
                {
                    segment.Append(' ');
                }
            }
            else
            {
                segment.Append(character);
            }
        }

        var trimmed = segment.ToString().Trim();
        if (trimmed.Length > MaxFolderNameLength)
        {
            trimmed = trimmed[..MaxFolderNameLength];
        }

        trimmed = trimmed.TrimEnd('.', ' ');
        return trimmed.Length == 0 ? "Campaign" : trimmed;
    }
}
