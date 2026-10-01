namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// DuckDB downloads <c>spatial</c> and <c>httpfs</c> on first use and caches them under
/// <c>%USERPROFILE%\.duckdb\extensions</c>. Tests must not hit the network (CLAUDE.md), so CI primes
/// the cache with the <c>doctor</c> verb before running them. When the cache is missing, a geography
/// test fails for a reason that has nothing to do with the code under test - this turns that into an
/// actionable sentence attached to the failure.
/// </summary>
internal static class DuckDbExtensionCache
{
    /// <summary>Empty when the cache looks primed; otherwise what to run to prime it.</summary>
    public static string Advice { get; } = Check();

    private static string Check()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".duckdb",
            "extensions");

        var cached = Directory.Exists(root)
            && Directory.EnumerateFiles(root, "spatial.duckdb_extension", SearchOption.AllDirectories).Any();

        return cached
            ? string.Empty
            : $"""
               Note: no 'spatial.duckdb_extension' under '{root}', so DuckDB would have to download it -
               which a unit test must never do. Prime the cache once with:

                   dotnet run --project src/ProspectStudio.Mcp -- doctor

               (CI does this in its "Prime DuckDB extensions" step.)
               """;
    }
}
