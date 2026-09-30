namespace ProspectStudio.Tests.Shared;

/// <summary>
/// Locates the spec pack's fixtures and schemas. The test projects copy <c>poc/fixtures</c> and
/// <c>poc/schemas</c> into their output as content (technical-design §3), so tests read them from
/// next to the test assembly and never from a path relative to the repo checkout.
/// </summary>
internal static class RepoFixtures
{
    public static string FixturesDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "poc", "fixtures");

    public static string SchemasDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "poc", "schemas");

    /// <summary>The brand kit that chunk C1 seeds into an empty <c>Brand Kit</c> folder.</summary>
    public static string BrandKitDirectory => Fixture("brand-kit");

    public static string SampleSearchProfile => Fixture("sample-search-profile.json");

    public static string SearchProfileSchema => Schema("search-profile.schema.json");

    /// <summary>A path under <c>poc/fixtures</c>, checked so a broken content copy is obvious.</summary>
    public static string Fixture(params string[] parts) => Existing(FixturesDirectory, "poc/fixtures", parts);

    /// <summary>A path under <c>poc/schemas</c>, checked so a broken content copy is obvious.</summary>
    public static string Schema(params string[] parts) => Existing(SchemasDirectory, "poc/schemas", parts);

    private static string Existing(string root, string label, string[] parts)
    {
        var full = Path.Combine([root, .. parts]);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException(
                $"'{label}/{string.Join('/', parts)}' was not copied to the test output. Expected it at "
                + $"'{full}'. The test project copies {label} as Content with CopyToOutputDirectory.",
                full);
        }

        return full;
    }
}
