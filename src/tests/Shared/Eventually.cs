using Xunit.Sdk;

namespace ProspectStudio.Tests.Shared;

/// <summary>
/// Waits for a condition instead of sleeping for a guessed duration. Job tests are about state the
/// runner reaches on its own thread, so the choice is between polling with a deadline and a
/// <c>Task.Delay</c> that is either flaky or slow. Everything here resolves in milliseconds when the
/// code is right and fails with a readable message when it is not.
/// </summary>
internal static class Eventually
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(15);

    public static async Task UntilAsync(
        Func<Task<bool>> condition,
        string because,
        TimeSpan? within = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = within ?? DefaultTimeout;
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new XunitException($"Waited {timeout.TotalSeconds:0.#} s for {because}, and it never happened.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until <paramref name="probe"/> returns a non-null value, and returns it.</summary>
    public static async Task<T> OfAsync<T>(
        Func<Task<T?>> probe,
        string because,
        TimeSpan? within = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        T? found = null;
        await UntilAsync(
            async () => (found = await probe().ConfigureAwait(false)) is not null,
            because,
            within,
            cancellationToken).ConfigureAwait(false);

        return found!;
    }
}
