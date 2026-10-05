namespace ProspectStudio.Tests.Shared;

/// <summary>
/// Enough of a file's identity to prove a step skipped its work instead of redoing it. Used by both
/// <see cref="ReferenceDataFixture"/> and <see cref="PlacesDataFixture"/>, which is why it lives on
/// its own rather than inside either.
/// </summary>
internal sealed record FileFingerprint(string Name, bool Exists, long Length, DateTime LastWriteUtc)
{
    public static FileFingerprint Of(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? new FileFingerprint(info.Name, true, info.Length, info.LastWriteTimeUtc)
            : new FileFingerprint(Path.GetFileName(path), false, 0, default);
    }

    public override string ToString() => Exists ? $"{Name} ({Length} bytes, {LastWriteUtc:O})" : $"{Name} (missing)";
}
