using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Jobs;
using ProspectStudio.Infrastructure.Storage;
using Xunit.Sdk;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// Writes job rows straight into a server's SQLite file through the production storage registration.
/// It is the only way to put a job in a given state from outside the process: the C2 tools offer no
/// way to park a job, and the only long-running tool is <c>prepare_data</c>, which must not be made to
/// hang in a test. WAL mode plus the busy timeout make a second writer safe here.
/// </summary>
internal static class ServerJobs
{
    /// <summary>Same file name as <c>PsOptions.DatabasePath</c>.</summary>
    public const string DatabaseFileName = "prospect.db";

    public static async Task AddAsync(string dataDirectory, JobSnapshot job, CancellationToken cancellationToken)
    {
        await using var services = new ServiceCollection()
            .AddProspectStudioStorage(Path.Combine(dataDirectory, DatabaseFileName))
            .BuildServiceProvider();

        var store = services.GetService<IJobStore>()
            ?? throw new XunitException(
                "AddProspectStudioStorage does not register an IJobStore. Chunk C2 adds the jobs table "
                + "(migration C2_Jobs) and the EF implementation of ProspectStudio.Core.Jobs.IJobStore.");

        await store.AddAsync(job, cancellationToken);

        // Teardown only: the pooled connection would otherwise keep the server's database file open.
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// A job row with every field the <c>get_job</c> contract shows. <paramref name="minute"/> moves
    /// <c>createdAt</c>, which is how a test pins <c>list_jobs</c>' newest-first order.
    /// </summary>
    public static JobSnapshot Snapshot(
        string jobId,
        string kind = JobKinds.PrefetchWebsites,
        string? campaignId = null,
        string status = JobStatuses.Running,
        double progress = 0.42,
        string? message = "Fetched 336/800 sites (12 blocked by robots.txt)",
        string? resultJson = null,
        int minute = 5)
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 13, minute, 0, TimeSpan.Zero);

        return new JobSnapshot(
            jobId,
            kind,
            campaignId,
            status,
            progress,
            message,
            ParametersJson: null,
            ResultJson: resultJson,
            CreatedAt: createdAt,
            StartedAt: createdAt.AddSeconds(2),
            FinishedAt: JobStatuses.IsTerminal(status) ? createdAt.AddMinutes(4) : null);
    }
}
