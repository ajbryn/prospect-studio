using ProspectStudio.Core.Reference;

namespace ProspectStudio.Infrastructure.Overture;

/// <summary>
/// Where the per-state Overture extracts live: <c>overture\&lt;release&gt;\places_&lt;ST&gt;.parquet</c>
/// (technical-design §5.1). Path math only - the reading is <see cref="DuckDbPlacesSource"/>'s job.
/// </summary>
public sealed class OvertureDataFiles(string directory, string release) : IOvertureDataInventory
{
    private readonly string _root = Path.GetFullPath(directory);

    public string Release { get; } = release;

    public string Directory => Path.Combine(_root, Release);

    public IReadOnlyList<string> States =>
        System.IO.Directory.Exists(Directory)
            ? [.. System.IO.Directory
                .EnumerateFiles(Directory, "places_*.parquet")
                .Where(path => new FileInfo(path).Length > 0)
                .Select(path => Path.GetFileNameWithoutExtension(path)["places_".Length..].ToUpperInvariant())
                .Order(StringComparer.Ordinal)]
            : [];

    public bool HasState(string state) =>
        new FileInfo(PlacesParquet(state)) is { Exists: true, Length: > 0 };

    public string PlacesParquet(string state) =>
        Path.Combine(Directory, OvertureSteps.FileFor(state));
}
