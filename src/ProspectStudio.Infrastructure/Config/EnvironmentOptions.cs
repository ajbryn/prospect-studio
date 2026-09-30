using System.Collections;
using ProspectStudio.Core.Configuration;

namespace ProspectStudio.Infrastructure.Config;

/// <summary>
/// Adapter around <see cref="PsOptionsFactory"/>: reads the real process environment, resolves
/// relative paths against the working directory and creates the data directories.
/// </summary>
public static class EnvironmentOptions
{
    public static PsOptions Load()
    {
        var options = PsOptionsFactory.Create(ReadEnvironment());

        return options with
        {
            Home = Path.GetFullPath(options.Home),
            Data = Path.GetFullPath(options.Data),
        };
    }

    public static PsOptions EnsureDataDirectories(PsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(options.Data);
        Directory.CreateDirectory(options.LogsDirectory);
        return options;
    }

    private static Dictionary<string, string?> ReadEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name)
            {
                environment[name] = entry.Value as string;
            }
        }

        Fallback(environment, PsOptionsFactory.UserProfileVariable, Environment.SpecialFolder.UserProfile);
        Fallback(environment, PsOptionsFactory.LocalAppDataVariable, Environment.SpecialFolder.LocalApplicationData);
        return environment;
    }

    private static void Fallback(Dictionary<string, string?> environment, string name, Environment.SpecialFolder folder)
    {
        if (!environment.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            environment[name] = Environment.GetFolderPath(folder);
        }
    }
}
