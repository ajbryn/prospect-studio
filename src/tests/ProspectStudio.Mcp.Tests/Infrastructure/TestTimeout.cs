namespace ProspectStudio.Mcp.Tests.Infrastructure;

internal static class TestTimeout
{
    public static CancellationTokenSource Start(int seconds = 60) => new(TimeSpan.FromSeconds(seconds));
}
