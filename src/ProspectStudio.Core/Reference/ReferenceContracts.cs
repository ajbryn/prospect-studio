namespace ProspectStudio.Core.Reference;

/// <summary>
/// The reference-data steps of the setup pipeline (technical-design §6.1) and the files they write into
/// <c>refdata\</c>. Named here so the tool, the CLI verb, the manifest and the tests all agree on one
/// set of names.
/// </summary>
public static class ReferenceSteps
{
    public const string Counties = "counties";
    public const string Cbsa = "cbsa";
    public const string Zcta = "zcta";

    public const string CountiesFile = "counties.parquet";
    public const string CbsaFile = "cbsa.csv";
    public const string ZctaFile = "zcta_county.csv";

    /// <summary>Records each step's source URL and when it was fetched.</summary>
    public const string ManifestFile = "manifest.json";

    /// <summary>The folder under <c>PROSPECT_STUDIO_DATA</c> that holds all of it.</summary>
    public const string FolderName = "refdata";

    /// <summary>In pipeline order.</summary>
    public static IReadOnlyList<string> All { get; } = [Counties, Cbsa, Zcta];

    /// <summary>The output file a step writes.</summary>
    public static string FileFor(string step) => step switch
    {
        Counties => CountiesFile,
        Cbsa => CbsaFile,
        Zcta => ZctaFile,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Not a reference-data step."),
    };
}

/// <summary>A source file on local disk, ready to parse, and where it came from.</summary>
/// <param name="Path">Absolute path of the downloaded (or cached) file.</param>
/// <param name="SourceUrl">The URL it was fetched from, which the manifest records.</param>
/// <param name="RetrievedAt">When it was fetched, which the manifest records.</param>
public sealed record ReferenceFile(string Path, Uri SourceUrl, DateTimeOffset RetrievedAt);

/// <summary>
/// Where the setup pipeline gets its raw Census files. The real implementation downloads (probing the
/// county vintage downward) and caches; tests hand back trimmed committed copies, so the
/// download-and-parse direction is covered without the network.
/// </summary>
public interface IReferenceFileSource
{
    /// <param name="step">One of <see cref="ReferenceSteps.All"/>.</param>
    Task<ReferenceFile> GetAsync(string step, CancellationToken cancellationToken);
}

/// <summary>What one step did.</summary>
/// <param name="Skipped">True when the output was already present and <c>force</c> was not set.</param>
/// <param name="Rows">Rows written, or the rows already there when skipped.</param>
public sealed record ReferenceStepResult(
    string Step,
    string File,
    bool Skipped,
    int Rows,
    Uri? SourceUrl,
    DateTimeOffset? RetrievedAt);

public sealed record ReferenceDataResult(IReadOnlyList<ReferenceStepResult> Steps, string ManifestPath);
