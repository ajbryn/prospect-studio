using System.Security.Cryptography;

namespace ProspectStudio.Core.Ids;

/// <summary>
/// The unambiguous alphabet CLAUDE.md §Conventions uses for campaign ids and tracking codes: no
/// 0/O/1/I/L, so a code read off a printed postcard cannot be mistyped.
/// </summary>
public static class ShortCodes
{
    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public static string Generate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        return new string(RandomNumberGenerator.GetItems<char>(Alphabet, length));
    }

    public static bool IsWellFormed(ReadOnlySpan<char> code, int length) =>
        code.Length == length && !code.ContainsAnyExcept(Alphabet.AsSpan());
}
