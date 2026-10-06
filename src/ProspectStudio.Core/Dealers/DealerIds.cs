using System.Security.Cryptography;
using System.Text;

namespace ProspectStudio.Core.Dealers;

/// <summary>
/// The ids <c>import_list</c> mints. Technical-design §5.3: string ids are generated in Core, not by
/// the database.
/// </summary>
/// <remarks>
/// <c>territories.csv</c> and <c>suppression.csv</c> carry no id column, so a re-import has to
/// recognize a row it has already seen. Both ids are therefore derived from the row's own identifying
/// fields rather than minted at random: the same row in the same file keeps the same id, which is what
/// makes a re-import idempotent (NFR-3) and keeps a lead's stored <c>suppression_id</c> pointing at the
/// row that suppressed it. <c>dealers</c> and <c>dealer_branches</c> need none of this: their files
/// name the ids.
/// </remarks>
public static class DealerIds
{
    public const string TerritoryPrefix = "ter_";
    public const string SuppressionPrefix = "sup_";

    /// <summary>Hex characters of the digest kept, which is 64 bits of it.</summary>
    private const int DigestLength = 16;

    /// <summary>
    /// A territory row's id. The identity is the rule itself - who serves which code from which branch -
    /// so editing <c>priority</c> updates the stored row instead of adding a second one.
    /// </summary>
    public static string Territory(string dealerId, string? branchId, string level, string code) =>
        TerritoryPrefix + Digest(dealerId, branchId, level, code);

    /// <summary>
    /// A suppression row's id. The identity is the company the row names, so a changed
    /// <c>reason</c> updates the row rather than suppressing the same company twice.
    /// </summary>
    public static string Suppression(string nameNorm, string? domain, string? addressNorm, string? zip) =>
        SuppressionPrefix + Digest(nameNorm, domain, addressNorm, zip);

    private static string Digest(params string?[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts.Select(part => part ?? string.Empty))));
        return Convert.ToHexStringLower(bytes)[..DigestLength];
    }
}
