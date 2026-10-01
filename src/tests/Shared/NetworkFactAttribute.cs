using Xunit;

namespace ProspectStudio.Tests;

/// <summary>
/// A test that calls a live external API. Skips itself unless <see cref="EnableVariable"/> is set,
/// so forgetting <c>--filter "Category!=Network"</c> cannot reach the network: a safety property that
/// depends on every caller remembering a flag is not a safety property. Pair it with
/// <c>[Trait("Category", "Network")]</c>, which is what CI filters on.
/// </summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    public const string EnableVariable = "PS_RUN_NETWORK_TESTS";

    public NetworkFactAttribute()
    {
        if (!NetworkTests.Enabled)
        {
            Skip = NetworkTests.SkipReason;
        }
    }
}

/// <summary>
/// The <see cref="TheoryAttribute"/> counterpart of <see cref="NetworkFactAttribute"/>.
/// </summary>
public sealed class NetworkTheoryAttribute : TheoryAttribute
{
    public NetworkTheoryAttribute()
    {
        if (!NetworkTests.Enabled)
        {
            Skip = NetworkTests.SkipReason;
        }
    }
}

internal static class NetworkTests
{
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable(NetworkFactAttribute.EnableVariable) is "1" or "true";

    internal static string SkipReason =>
        $"Calls a live external API. Set {NetworkFactAttribute.EnableVariable}=1 to run it.";
}
