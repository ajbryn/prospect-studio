namespace ProspectStudio.Infrastructure.Workspace;

/// <summary>What a bootstrap run changed. Empty lists mean the workspace was already in shape.</summary>
public sealed record WorkspaceBootstrapResult(IReadOnlyList<string> CreatedFolders, int SeededFiles);

/// <summary>
/// Creates the user-facing workspace folders under <c>PROSPECT_STUDIO_HOME</c> (POC-3,
/// technical-design §5.1) and, as a dev convenience, seeds the fixture brand kit.
/// </summary>
/// <param name="homeDirectory">The workspace root (<c>PROSPECT_STUDIO_HOME</c>).</param>
/// <param name="fixtureBrandKitDirectory">
/// Where the <c>poc/fixtures/brand-kit</c> copy that ships with the server lives. Null means no
/// seeding is possible.
/// </param>
public sealed class WorkspaceBootstrapper(string homeDirectory, string? fixtureBrandKitDirectory = null)
{
    public const string BrandKitFolder = "Brand Kit";
    public const string DealersFolder = "Dealers";
    public const string SuppressionFolder = "Suppression";
    public const string TemplatesFolder = "Templates";
    public const string CampaignsFolder = "Campaigns";

    /// <summary>The folders POC-3 requires, in the order technical-design §5.1 lists them.</summary>
    public static IReadOnlyList<string> FolderNames { get; } =
        [BrandKitFolder, DealersFolder, SuppressionFolder, TemplatesFolder, CampaignsFolder];

    public string HomeDirectory { get; } = homeDirectory;

    public string? FixtureBrandKitDirectory { get; } = fixtureBrandKitDirectory;

    /// <summary>
    /// Creates any missing folder. Safe to run repeatedly. When <paramref name="seedFixtures"/> is
    /// true and <c>Brand Kit</c> is empty, copies the fixture brand kit into it; it never overwrites
    /// an existing file.
    /// </summary>
    public async Task<WorkspaceBootstrapResult> EnsureAsync(bool seedFixtures, CancellationToken cancellationToken)
    {
        var created = new List<string>();
        foreach (var folder in FolderNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = Path.Combine(HomeDirectory, folder);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                created.Add(path);
            }
        }

        var seeded = seedFixtures
            ? await SeedBrandKitAsync(cancellationToken).ConfigureAwait(false)
            : 0;

        return new WorkspaceBootstrapResult(created, seeded);
    }

    private async Task<int> SeedBrandKitAsync(CancellationToken cancellationToken)
    {
        if (FixtureBrandKitDirectory is null || !Directory.Exists(FixtureBrandKitDirectory))
        {
            return 0;
        }

        var brandKit = Path.Combine(HomeDirectory, BrandKitFolder);

        // "Empty" means no files at all: a brand kit the user has started on is theirs, not ours.
        if (Directory.EnumerateFiles(brandKit, "*", SearchOption.AllDirectories).Any())
        {
            return 0;
        }

        var copied = 0;
        foreach (var source in Directory.EnumerateFiles(FixtureBrandKitDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = Path.Combine(brandKit, Path.GetRelativePath(FixtureBrandKitDirectory, source));
            if (File.Exists(destination))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
            copied++;
        }

        return copied;
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var reading = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var writing = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await reading.CopyToAsync(writing, cancellationToken).ConfigureAwait(false);
    }
}
