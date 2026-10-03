using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Reference;
using ProspectStudio.Infrastructure.Overture;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Places;

/// <summary>
/// The Overture step of the setup pipeline (technical-design §6.1), driven through a substituted
/// <see cref="IOvertureExtractSource"/>. Without these the seam would be dead weight: every other
/// test pre-installs the extract so the step skips, and the only exercise of extract-and-write would
/// be the opt-in network test - the same gap C2's <see cref="IReferenceFileSource"/> was added to close
/// for the Census downloads.
/// </summary>
public class OvertureDataPreparerTests
{
    private const string Release = PsOptionsFactory.DefaultOvertureRelease;

    [Fact]
    public async Task An_extract_is_written_to_a_part_file_and_then_moved_into_place()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var (preparer, files, source) = Preparer(directory);

        var results = await preparer.PrepareAsync(["TX"], force: false, timeout.Token);

        var result = results.ShouldHaveSingleItem();
        result.Step.ShouldBe(OvertureSteps.Places);
        result.State.ShouldBe("TX");
        result.File.ShouldBe("places_TX.parquet");
        result.Skipped.ShouldBeFalse();
        result.Rows.ShouldBe(7, "the row count comes back from the source, not from counting the file.");
        result.Release.ShouldBe(Release);
        result.RetrievedAt.ShouldNotBeNull("a written extract records when it was fetched, for the manifest.");

        source.Destinations.ShouldHaveSingleItem().ShouldEndWith(
            ".part",
            Case.Sensitive,
            "the source must never be handed the final path. A step decides it has nothing to do by "
            + "seeing a non-empty file, so a half-written Parquet under the real name is reported as "
            + "finished for ever.");

        File.Exists(files.PlacesParquet("TX")).ShouldBeTrue("and the .part is then moved into place.");
        File.Exists(files.PlacesParquet("TX") + ".part").ShouldBeFalse("the move consumes it.");
        files.HasState("TX").ShouldBeTrue();
        files.States.ShouldBe(["TX"]);
    }

    [Fact]
    public async Task An_extract_that_dies_halfway_leaves_nothing_that_a_later_run_would_skip()
    {
        // This is the test that pays for the seam. The .part discipline exists for exactly one
        // scenario - a download that fails after writing some bytes - and nothing else can reach it:
        // the real source would have to be made to fail mid-transfer against S3.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var (preparer, files, source) = Preparer(directory);

        source.ThrowAfterWriting = new IOException("the connection dropped at 40 MB");

        var failure = await Should.ThrowAsync<IOException>(
            () => preparer.PrepareAsync(["TX"], force: false, timeout.Token));
        failure.Message.ShouldContain("connection dropped");

        File.Exists(files.PlacesParquet("TX")).ShouldBeFalse(
            "a truncated extract must not appear under the real name. If it does, every later run "
            + "skips it as finished and find_candidates searches a partial file - silently, because a "
            + "short Parquet still reads.");
        files.HasState("TX").ShouldBeFalse("so the state is still NOT_READY, which is the truth.");

        // And the state is genuinely recoverable: "run it again" is the documented recovery for every
        // job (mcp-tools.md §get_job), so the second run has to actually re-extract.
        source.ThrowAfterWriting = null;

        var results = await preparer.PrepareAsync(["TX"], force: false, timeout.Token);

        results.ShouldHaveSingleItem().Skipped.ShouldBeFalse(
            "the failed run left nothing behind, so this one has work to do.");
        source.Calls.ShouldBe(2, "the source was asked a second time.");
        files.HasState("TX").ShouldBeTrue();
    }

    [Fact]
    public async Task A_state_that_is_already_extracted_is_skipped_without_touching_the_source()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var (preparer, files, source) = Preparer(directory);

        await preparer.PrepareAsync(["TX"], force: false, timeout.Token);
        var fingerprint = FileFingerprint.Of(files.PlacesParquet("TX"));

        var again = await preparer.PrepareAsync(["TX"], force: false, timeout.Token);

        var result = again.ShouldHaveSingleItem();
        result.Skipped.ShouldBeTrue();
        result.Rows.ShouldBe(0, "nothing was written, so there is no row count to report.");
        result.RetrievedAt.ShouldBeNull("nothing was fetched.");

        source.Calls.ShouldBe(
            1,
            "the source must not be called at all for a state that is already there. It is a 205 MB "
            + "download, and this is what lets the whole test suite run the step offline (CLAUDE.md).");

        FileFingerprint.Of(files.PlacesParquet("TX")).ShouldBe(fingerprint, "and the file is untouched.");
    }

    [Fact]
    public async Task Force_re_extracts_a_state_that_is_already_there()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var (preparer, _, source) = Preparer(directory);

        await preparer.PrepareAsync(["TX"], force: false, timeout.Token);
        var forced = await preparer.PrepareAsync(["TX"], force: true, timeout.Token);

        forced.ShouldHaveSingleItem().Skipped.ShouldBeFalse(
            "mcp-tools.md §prepare_data: 'Skips completed steps unless force.' Without this, a user who "
            + "knows their extract is stale has no way to replace it.");
        source.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Each_state_gets_its_own_file_and_its_own_progress_report()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var (preparer, files, source) = Preparer(directory);

        var progress = new List<double>();
        var reported = new List<string>();

        await preparer.PrepareAsync(
            ["TX", "OK"],
            force: false,
            (step, fraction, _) =>
            {
                reported.Add(step.State);
                progress.Add(fraction);
                return Task.CompletedTask;
            },
            timeout.Token);

        source.States.ShouldBe(["TX", "OK"]);
        reported.ShouldBe(["TX", "OK"], "a per-state report is what lets a 70-second-per-state job show progress.");
        progress.ShouldBe([0.5, 1.0]);

        File.Exists(files.PlacesParquet("TX")).ShouldBeTrue();
        File.Exists(files.PlacesParquet("OK")).ShouldBeTrue();
        files.States.ShouldBe(["OK", "TX"], "sorted, because get_status reports them.");
    }

    [Fact]
    public async Task No_states_means_no_work_and_no_folder()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        var (preparer, files, source) = Preparer(directory);

        var results = await preparer.PrepareAsync([], force: false, timeout.Token);

        results.ShouldBeEmpty();
        source.Calls.ShouldBe(0);
        Directory.Exists(files.Directory).ShouldBeFalse(
            "prepare_data with no states must not leave an empty release folder behind, or get_status "
            + "has to tell an empty folder from a missing one.");
    }

    [Fact]
    public async Task A_newer_published_release_is_reported_and_the_configured_one_is_not()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();

        var newer = new FakeOvertureExtractSource(latestRelease: "2026-11-18.0");
        var sameAsOurs = new FakeOvertureExtractSource(latestRelease: Release);
        var files = new OvertureDataFiles(Path.Combine(directory.Path, OvertureSteps.FolderName), Release);

        (await new OvertureDataPreparer(files, newer).FindNewerReleaseAsync(timeout.Token))
            .ShouldBe("2026-11-18.0", "so the job can say the extract is a release behind.");

        (await new OvertureDataPreparer(files, sameAsOurs).FindNewerReleaseAsync(timeout.Token))
            .ShouldBeNull("nothing to report when the catalog agrees with PS_OVERTURE_RELEASE.");
    }

    private static CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(30));

    private static (OvertureDataPreparer Preparer, OvertureDataFiles Files, FakeOvertureExtractSource Source)
        Preparer(TempDirectory directory)
    {
        var files = new OvertureDataFiles(Path.Combine(directory.Path, OvertureSteps.FolderName), Release);
        var source = new FakeOvertureExtractSource();
        return (new OvertureDataPreparer(files, source), files, source);
    }
}
