using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Jobs;
using ProspectStudio.Tests.Shared;
using Xunit.Sdk;

namespace ProspectStudio.Infrastructure.Tests.Infrastructure;

/// <summary>
/// A migrated SQLite database in a temp folder, the <see cref="IJobStore"/> the production
/// registration provides, a hand-driven clock and - on request - a started
/// <see cref="JobRunner"/>. Real SQLite, never the EF InMemory provider (CLAUDE.md).
/// </summary>
internal sealed class JobTestHarness : IAsyncDisposable
{
    private readonly TempDirectory _directory;
    private TempDatabase _database;
    private JobRunner? _runner;

    private JobTestHarness(TempDirectory directory, TempDatabase database, ManualTimeProvider clock)
    {
        _directory = directory;
        _database = database;
        Clock = clock;
    }

    public ManualTimeProvider Clock { get; }

    public IJobStore Store => Resolve(_database);

    /// <summary>The runner, which only exists after <see cref="StartRunnerAsync"/>.</summary>
    public JobRunner Runner => _runner ?? throw new InvalidOperationException("Call StartRunnerAsync first.");

    public static async Task<JobTestHarness> CreateAsync(CancellationToken cancellationToken)
    {
        var directory = new TempDirectory();
        var database = TempDatabase.In(directory.Path);
        await using (await database.MigrateAsync(cancellationToken))
        {
        }

        return new JobTestHarness(directory, database, new ManualTimeProvider());
    }

    /// <summary>
    /// Builds the runner and starts it, which is also what turns jobs an earlier run left
    /// <see cref="JobStatuses.Running"/> into <see cref="JobStatuses.Interrupted"/>.
    /// </summary>
    public async Task<JobRunner> StartRunnerAsync(CancellationToken cancellationToken)
    {
        _runner = new JobRunner(Store, Clock);
        await _runner.StartAsync(cancellationToken);
        return _runner;
    }

    /// <summary>
    /// Closes everything and opens the same database file again, standing in for a server restart. The
    /// returned store reads the rows the previous process left behind.
    /// </summary>
    public async Task ReopenAsync(CancellationToken cancellationToken)
    {
        if (_runner is not null)
        {
            await _runner.DisposeAsync();
            _runner = null;
        }

        await _database.DisposeAsync();
        _database = TempDatabase.In(_directory.Path);
        await using (await _database.MigrateAsync(cancellationToken))
        {
        }
    }

    public async Task<JobSnapshot> RequireJobAsync(string jobId, CancellationToken cancellationToken) =>
        await Store.FindAsync(jobId, cancellationToken)
        ?? throw new XunitException($"There is no job row for '{jobId}'.");

    /// <summary>Waits until the job reaches a status nothing will change again.</summary>
    public async Task<JobSnapshot> WaitForTerminalAsync(string jobId, CancellationToken cancellationToken)
    {
        JobSnapshot? job = null;
        await Eventually.UntilAsync(
            async () =>
            {
                job = await Store.FindAsync(jobId, cancellationToken);
                return job is not null && JobStatuses.IsTerminal(job.Status);
            },
            $"job '{jobId}' to reach a terminal status",
            cancellationToken: cancellationToken);

        return job!;
    }

    public async Task<JobSnapshot> WaitForStatusAsync(string jobId, string status, CancellationToken cancellationToken)
    {
        JobSnapshot? job = null;
        await Eventually.UntilAsync(
            async () =>
            {
                job = await Store.FindAsync(jobId, cancellationToken);
                return job?.Status == status;
            },
            $"job '{jobId}' to be '{status}'",
            cancellationToken: cancellationToken);

        return job!;
    }

    private static IJobStore Resolve(TempDatabase database) =>
        database.Services.GetService<IJobStore>()
        ?? throw new XunitException(
            "AddProspectStudioStorage does not register an IJobStore. Chunk C2 adds the jobs table "
            + "(migration C2_Jobs) and the EF implementation of ProspectStudio.Core.Jobs.IJobStore, "
            + "registered in StorageServiceCollectionExtensions alongside ICampaignStore.");

    public async ValueTask DisposeAsync()
    {
        if (_runner is not null)
        {
            try
            {
                await _runner.DisposeAsync();
            }
            catch (NotImplementedException)
            {
                // The runner is still a stub; disposing it must not hide the real failure.
            }
        }

        await _database.DisposeAsync();
        _directory.Dispose();
    }
}
