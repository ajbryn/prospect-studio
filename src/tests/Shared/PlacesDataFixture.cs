using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Reference;

namespace ProspectStudio.Tests.Shared;

/// <summary>
/// Puts the committed Overture Places Parquet into a data folder as if the Overture step of
/// <c>prepare_data</c> had already run for Texas, so candidate search has an extract to read with no
/// network and no 205 MB download.
/// </summary>
/// <remarks>
/// The release and file names come from <see cref="PsOptionsFactory.DefaultOvertureRelease"/> and
/// <see cref="OvertureSteps"/> rather than being spelled out, so renaming an output in production
/// renames it in the fixture too - the same reason <see cref="ReferenceDataFixture"/> does it.
/// </remarks>
internal static class PlacesDataFixture
{
    /// <summary>The release the committed fixture stands in for, verified in C4.</summary>
    public const string Release = PsOptionsFactory.DefaultOvertureRelease;

    /// <summary>The state the fixture covers.</summary>
    public const string State = "TX";

    public static string Directory(string dataDirectory) =>
        Path.Combine(dataDirectory, OvertureSteps.FolderName);

    public static string ReleaseDirectory(string dataDirectory) =>
        Path.Combine(Directory(dataDirectory), Release);

    public static string PlacesParquet(string dataDirectory, string state = State) =>
        Path.Combine(ReleaseDirectory(dataDirectory), OvertureSteps.FileFor(state));

    /// <summary>
    /// Copies <c>sample_places.parquet</c> to
    /// <c>&lt;data&gt;\overture\&lt;release&gt;\places_&lt;ST&gt;.parquet</c> (technical-design §5.1)
    /// and returns the <c>overture\</c> folder.
    /// </summary>
    /// <param name="state">
    /// Usually <c>TX</c>, which is what the fixture actually holds. Any other state installs the same
    /// Texas rows as a <strong>stand-in</strong>, purely so the Overture step of <c>prepare_data</c>
    /// finds its output and skips: without it the step would download hundreds of megabytes from S3
    /// inside <c>dotnet test</c>, which no test may do (CLAUDE.md). Nothing asserts on the contents of
    /// a stand-in extract.
    /// </param>
    public static string Install(string dataDirectory, string state = State)
    {
        System.IO.Directory.CreateDirectory(ReleaseDirectory(dataDirectory));
        File.Copy(RepoFixtures.SamplePlacesParquet, PlacesParquet(dataDirectory, state), overwrite: true);
        return Directory(dataDirectory);
    }

    /// <summary>
    /// Length and last-write time of every installed extract, so a test can prove a second
    /// <c>prepare_data</c> left them alone instead of downloading them again.
    /// </summary>
    public static IReadOnlyList<FileFingerprint> Fingerprint(string dataDirectory) =>
        System.IO.Directory.Exists(ReleaseDirectory(dataDirectory))
            ? [.. System.IO.Directory
                .EnumerateFiles(ReleaseDirectory(dataDirectory), "places_*.parquet")
                .Order(StringComparer.Ordinal)
                .Select(FileFingerprint.Of)]
            : [];
}
