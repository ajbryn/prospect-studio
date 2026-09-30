using ProspectStudio.Core.Configuration;
using ProspectStudio.Infrastructure.Config;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Config;

public class EnvironmentOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prospect-studio-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_resolves_both_paths_to_absolute_paths()
    {
        var options = EnvironmentOptions.Load();

        Path.IsPathRooted(options.Home).ShouldBeTrue();
        Path.IsPathRooted(options.Data).ShouldBeTrue();
        options.Home.ShouldNotBeEmpty();
        options.Data.ShouldNotBeEmpty();
    }

    [Fact]
    public void EnsureDataDirectories_creates_the_data_and_logs_folders()
    {
        var options = OptionsUnder(_root);

        Directory.Exists(options.Data).ShouldBeFalse();

        EnvironmentOptions.EnsureDataDirectories(options);

        Directory.Exists(options.Data).ShouldBeTrue();
        Directory.Exists(options.LogsDirectory).ShouldBeTrue();
    }

    [Fact]
    public void EnsureDataDirectories_is_idempotent()
    {
        var options = OptionsUnder(_root);

        EnvironmentOptions.EnsureDataDirectories(options);
        EnvironmentOptions.EnsureDataDirectories(options);

        Directory.Exists(options.LogsDirectory).ShouldBeTrue();
    }

    [Fact]
    public void EnsureDataDirectories_does_not_create_the_workspace_folder()
    {
        var options = OptionsUnder(_root);

        EnvironmentOptions.EnsureDataDirectories(options);

        Directory.Exists(options.Home).ShouldBeFalse();
    }

    private static PsOptions OptionsUnder(string root) => PsOptionsFactory.Create(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [PsOptionsFactory.HomeVariable] = Path.Combine(root, "home"),
            [PsOptionsFactory.DataVariable] = Path.Combine(root, "data"),
        });

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
