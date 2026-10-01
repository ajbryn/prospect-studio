using System.Text.Json;
using ProspectStudio.Core.Reference;

namespace ProspectStudio.Tests.Shared;

/// <summary>
/// Puts the committed geography excerpts into a data folder as if <c>prepare_data</c> had already run,
/// so a server under test knows US geography without downloading anything.
/// </summary>
/// <remarks>
/// File names come from <see cref="ReferenceSteps"/> rather than being spelled out here, so renaming an
/// output in production renames it in the fixture too. That matters: a reference step that cannot see
/// its own output downloads its source again, which would put an 18 MB census.gov fetch inside
/// <c>dotnet test</c>. The manifest shape below is the contract -
/// <c>ReferenceDataPreparerTests</c> asserts the same shape against a real run.
/// </remarks>
internal static class ReferenceDataFixture
{
    public const string CountiesSourceUrl =
        "https://www2.census.gov/geo/tiger/GENZ2025/shp/cb_2025_us_county_500k.zip";

    public const string CbsaSourceUrl =
        "https://www2.census.gov/programs-surveys/metro-micro/geographies/reference-files/2023/delineation-files/list1_2023.xlsx";

    public const string ZctaCountySourceUrl =
        "https://www2.census.gov/geo/docs/maps-data/data/rel2020/zcta520/tab20_zcta520_county20_natl.txt";

    /// <summary>Every file a complete reference-data folder holds, in setup-pipeline order.</summary>
    public static IReadOnlyList<string> FileNames { get; } =
        [.. ReferenceSteps.All.Select(ReferenceSteps.FileFor), ReferenceSteps.ManifestFile];

    public static string Directory(string dataDirectory) =>
        Path.Combine(dataDirectory, ReferenceSteps.FolderName);

    public static string SourceUrlFor(string step) => step switch
    {
        ReferenceSteps.Counties => CountiesSourceUrl,
        ReferenceSteps.Cbsa => CbsaSourceUrl,
        ReferenceSteps.Zcta => ZctaCountySourceUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Not a reference-data step."),
    };

    /// <summary>
    /// Copies the excerpts into <c><paramref name="dataDirectory"/>\refdata</c> under their production
    /// names and writes a manifest that records where each one came from. Returns the refdata folder.
    /// </summary>
    public static string Install(string dataDirectory)
    {
        var refdata = Directory(dataDirectory);
        System.IO.Directory.CreateDirectory(refdata);

        File.Copy(RepoFixtures.CountiesHoustonParquet, Path.Combine(refdata, ReferenceSteps.CountiesFile), overwrite: true);
        File.Copy(RepoFixtures.CbsaExcerptCsv, Path.Combine(refdata, ReferenceSteps.CbsaFile), overwrite: true);
        File.Copy(RepoFixtures.ZctaCountyExcerptCsv, Path.Combine(refdata, ReferenceSteps.ZctaFile), overwrite: true);

        var retrieved = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var rows = new Dictionary<string, int>
        {
            [ReferenceSteps.Counties] = 11,
            [ReferenceSteps.Cbsa] = 142,
            [ReferenceSteps.Zcta] = 321,
        };

        var manifest = new
        {
            version = 1,
            updatedAt = retrieved,
            steps = ReferenceSteps.All.Select(step => new
            {
                step,
                file = ReferenceSteps.FileFor(step),
                sourceUrl = SourceUrlFor(step),
                retrievedAt = retrieved,
                rows = rows[step],
            }).ToArray(),
        };

        File.WriteAllText(
            Path.Combine(refdata, ReferenceSteps.ManifestFile),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return refdata;
    }

    /// <summary>
    /// Length and last-write time of every reference file, so a test can prove a second setup run left
    /// them alone instead of downloading them again.
    /// </summary>
    public static IReadOnlyList<FileFingerprint> Fingerprint(string dataDirectory)
    {
        var refdata = Directory(dataDirectory);
        return [.. FileNames.Select(name => FileFingerprint.Of(Path.Combine(refdata, name)))];
    }
}

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
