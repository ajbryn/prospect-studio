using Xunit;

namespace ProspectStudio.Tests;

/// <summary>
/// A test that calls a live external API. Skips itself unless <see cref="EnableVariable"/> is set,
/// so forgetting <c>--filter "Category!=Network"</c> cannot reach the network: a safety property that
/// depends on every caller remembering a flag is not a safety property. Pair it with
/// <c>[Trait("Category", "Network")]</c>, which is what CI filters on.
/// </summary>
/// <param name="alsoRequires">
/// Further environment variables the call needs, named so the skip message can say which one is
/// missing - a live CBP query cannot run without <c>CENSUS_API_KEY</c>, and failing for that would
/// punish anyone who has no key. Variable <em>names</em> only; no value ever appears in test source.
/// </param>
public sealed class NetworkFactAttribute : FactAttribute
{
    public const string EnableVariable = "PS_RUN_NETWORK_TESTS";

    public NetworkFactAttribute(params string[] alsoRequires)
    {
        if (NetworkTests.SkipReasonFor(alsoRequires) is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// The <see cref="TheoryAttribute"/> counterpart of <see cref="NetworkFactAttribute"/>.
/// </summary>
public sealed class NetworkTheoryAttribute : TheoryAttribute
{
    public NetworkTheoryAttribute(params string[] alsoRequires)
    {
        if (NetworkTests.SkipReasonFor(alsoRequires) is { } reason)
        {
            Skip = reason;
        }
    }
}

internal static class NetworkTests
{
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable(NetworkFactAttribute.EnableVariable) is "1" or "true";

    internal static string SkipReason =>
        $"Calls a live external API. Set {NetworkFactAttribute.EnableVariable}=1 to run it.";

    /// <summary>Null when the test may run, otherwise why it is skipped.</summary>
    internal static string? SkipReasonFor(IReadOnlyList<string> alsoRequires)
    {
        if (!Enabled)
        {
            return SkipReason;
        }

        var missing = alsoRequires
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToList();

        return missing.Count == 0
            ? null
            : $"Calls a live external API that needs {string.Join(" and ", missing)}, which is not set.";
    }
}
