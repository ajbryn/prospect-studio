using ProspectStudio.Infrastructure.Tests.Infrastructure;
using ProspectStudio.Infrastructure.Workspace;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Workspace;

/// <summary>
/// POC-3 and implementation-plan C1: the five workspace folders appear under
/// <c>PROSPECT_STUDIO_HOME</c> when they are missing, running again changes nothing, and the fixture
/// brand kit is copied in <em>only</em> when seeding is asked for and <c>Brand Kit</c> is empty
/// (technical-design §5.1). Every test gets its own temp home and leaves nothing behind.
/// </summary>
public class WorkspaceBootstrapperTests
{
    /// <summary>The folders POC-3 requires, in the order technical-design §5.1 lists them.</summary>
    private static readonly string[] ExpectedFolders = ["Brand Kit", "Dealers", "Suppression", "Templates", "Campaigns"];

    [Fact]
    public void The_folder_list_is_exactly_the_five_folders_POC_3_requires()
    {
        WorkspaceBootstrapper.FolderNames.ShouldBe(ExpectedFolders);
    }

    [Fact]
    public async Task Ensure_creates_the_five_workspace_folders_when_the_home_folder_is_missing()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var home = directory.Combine("home");

        var result = await Bootstrapper(home).EnsureAsync(seedFixtures: false, timeout.Token);

        foreach (var folder in ExpectedFolders)
        {
            Directory.Exists(Path.Combine(home, folder)).ShouldBeTrue(
                $"POC-3 requires '{folder}' under PROSPECT_STUDIO_HOME.");
        }

        LeafNames(result.CreatedFolders).ShouldBe(ExpectedFolders, ignoreOrder: true);
    }

    [Fact]
    public async Task Ensure_creates_only_the_folders_that_are_missing()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var home = directory.Combine("home");
        Directory.CreateDirectory(Path.Combine(home, "Dealers"));
        Directory.CreateDirectory(Path.Combine(home, "Templates"));

        var result = await Bootstrapper(home).EnsureAsync(seedFixtures: false, timeout.Token);

        LeafNames(result.CreatedFolders).ShouldBe(["Brand Kit", "Suppression", "Campaigns"], ignoreOrder: true);
    }

    [Fact]
    public async Task Ensure_run_twice_creates_nothing_the_second_time_and_leaves_user_files_alone()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var home = directory.Combine("home");
        var bootstrapper = Bootstrapper(home);

        await bootstrapper.EnsureAsync(seedFixtures: false, timeout.Token);

        var userFile = Path.Combine(home, "Dealers", "dealers.csv");
        await File.WriteAllTextAsync(userFile, "dealer_id,name\ngulf,Gulf Lift Equipment\n", timeout.Token);

        var second = await bootstrapper.EnsureAsync(seedFixtures: false, timeout.Token);

        second.CreatedFolders.ShouldBeEmpty("a second run must not report folders it did not create.");
        second.SeededFiles.ShouldBe(0);

        foreach (var folder in ExpectedFolders)
        {
            Directory.Exists(Path.Combine(home, folder)).ShouldBeTrue();
        }

        (await File.ReadAllTextAsync(userFile, timeout.Token))
            .Contains("Gulf Lift Equipment", StringComparison.Ordinal)
            .ShouldBeTrue("a re-run must never touch a user's file.");
    }

    [Fact]
    public async Task Ensure_without_seeding_leaves_the_brand_kit_empty()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var home = directory.Combine("home");

        var result = await Bootstrapper(home).EnsureAsync(seedFixtures: false, timeout.Token);

        result.SeededFiles.ShouldBe(0);
        BrandKitEntries(home).ShouldBeEmpty(
            "the fixture brand kit is a dev convenience and must only be copied when seeding is asked for.");
    }

    [Fact]
    public async Task Ensure_with_seeding_copies_the_whole_fixture_brand_kit_into_an_empty_brand_kit()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var home = directory.Combine("home");

        var result = await Bootstrapper(home).EnsureAsync(seedFixtures: true, timeout.Token);

        var expected = RelativeFiles(RepoFixtures.BrandKitDirectory);
        expected.ShouldNotBeEmpty("the brand-kit fixture itself is empty, so this test proves nothing.");

        RelativeFiles(Path.Combine(home, "Brand Kit")).ShouldBe(expected, ignoreOrder: true);
        result.SeededFiles.ShouldBe(expected.Count);

        var brand = Path.Combine(home, "Brand Kit", "brand.json");
        (await File.ReadAllTextAsync(brand, timeout.Token))
            .ShouldBe(await File.ReadAllTextAsync(Path.Combine(RepoFixtures.BrandKitDirectory, "brand.json"), timeout.Token));
    }

    [Fact]
    public async Task Ensure_with_seeding_fills_a_brand_kit_folder_that_already_exists_but_is_empty()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var home = directory.Combine("home");
        Directory.CreateDirectory(Path.Combine(home, "Brand Kit"));

        var result = await Bootstrapper(home).EnsureAsync(seedFixtures: true, timeout.Token);

        result.SeededFiles.ShouldBeGreaterThan(0);
        File.Exists(Path.Combine(home, "Brand Kit", "brand.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Ensure_with_seeding_skips_a_brand_kit_that_already_holds_any_file()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var home = directory.Combine("home");
        var notes = Path.Combine(home, "Brand Kit", "my-notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(notes)!);
        await File.WriteAllTextAsync(notes, "mine", timeout.Token);

        var result = await Bootstrapper(home).EnsureAsync(seedFixtures: true, timeout.Token);

        result.SeededFiles.ShouldBe(0, "'empty' means no files at all, not 'no brand-kit files'.");
        File.Exists(Path.Combine(home, "Brand Kit", "brand.json")).ShouldBeFalse();
        (await File.ReadAllTextAsync(notes, timeout.Token)).ShouldBe("mine");
    }

    [Fact]
    public async Task Ensure_with_seeding_never_overwrites_an_existing_brand_kit_file()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var home = directory.Combine("home");
        var bootstrapper = Bootstrapper(home);

        await bootstrapper.EnsureAsync(seedFixtures: true, timeout.Token);

        var brand = Path.Combine(home, "Brand Kit", "brand.json");
        await File.WriteAllTextAsync(brand, """{"brand":"edited by the user"}""", timeout.Token);

        var again = await bootstrapper.EnsureAsync(seedFixtures: true, timeout.Token);

        again.SeededFiles.ShouldBe(0);
        (await File.ReadAllTextAsync(brand, timeout.Token))
            .ShouldBe("""{"brand":"edited by the user"}""", "seeding must never overwrite a file the user owns.");
    }

    [Fact]
    public async Task Ensure_with_seeding_but_no_fixture_folder_still_creates_the_workspace()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var home = directory.Combine("home");

        var result = await new WorkspaceBootstrapper(home, fixtureBrandKitDirectory: null)
            .EnsureAsync(seedFixtures: true, timeout.Token);

        LeafNames(result.CreatedFolders).ShouldBe(ExpectedFolders, ignoreOrder: true);
        result.SeededFiles.ShouldBe(0, "with nothing to copy from, seeding is a no-op, not a failure.");
    }

    private static WorkspaceBootstrapper Bootstrapper(string home) => new(home, RepoFixtures.BrandKitDirectory);

    private static CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(30));

    /// <summary>
    /// Folder names, whether the result reports bare names or absolute paths, so the assertion does
    /// not pin down which the implementer chose.
    /// </summary>
    private static List<string> LeafNames(IEnumerable<string> folders) =>
        [.. folders.Select(folder => Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))];

    private static List<string> BrandKitEntries(string home)
    {
        var brandKit = Path.Combine(home, "Brand Kit");
        return Directory.Exists(brandKit) ? [.. Directory.EnumerateFileSystemEntries(brandKit)] : [];
    }

    private static List<string> RelativeFiles(string root) =>
        Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)]
            : [];
}
