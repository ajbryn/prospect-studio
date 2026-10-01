using ProspectStudio.Core.Reference;

namespace ProspectStudio.Infrastructure.Reference;

/// <summary>
/// Where the prepared reference files live and whether they are all there. File names come from
/// <see cref="ReferenceSteps"/>, so renaming an output renames it everywhere at once.
/// </summary>
public sealed class ReferenceDataFiles(string directory) : IReferenceDataInventory
{
    public string Directory { get; } = Path.GetFullPath(directory);

    public string CountiesParquet => Path.Combine(Directory, ReferenceSteps.CountiesFile);

    public string CbsaCsv => Path.Combine(Directory, ReferenceSteps.CbsaFile);

    public string ZctaCsv => Path.Combine(Directory, ReferenceSteps.ZctaFile);

    public string Manifest => Path.Combine(Directory, ReferenceSteps.ManifestFile);

    public bool IsComplete => MissingFiles.Count == 0;

    public IReadOnlyList<string> MissingFiles =>
    [
        .. Expected().Where(path => !HasContent(path)).Select(Path.GetFileName).OfType<string>(),
    ];

    /// <summary>The file a step writes, which is also how the pipeline decides whether to skip it.</summary>
    public string FileFor(string step) => Path.Combine(Directory, ReferenceSteps.FileFor(step));

    /// <summary>True when the step's output is already there, so it has nothing to do.</summary>
    public bool IsStepComplete(string step) => HasContent(FileFor(step));

    private IEnumerable<string> Expected() =>
        ReferenceSteps.All.Select(FileFor).Append(Manifest);

    private static bool HasContent(string path) => new FileInfo(path) is { Exists: true, Length: > 0 };
}
