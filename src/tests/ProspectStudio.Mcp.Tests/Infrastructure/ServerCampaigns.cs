using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Domain;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Tests.Shared;
using Xunit.Sdk;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// Writes a campaign and its search profile straight into a server's SQLite file through the
/// production storage registration, the same way <see cref="ServerJobs"/> seeds a job row.
/// </summary>
/// <remarks>
/// <see cref="RawServerSession"/> sends a fixed list of JSON-RPC lines and cannot read an id out of
/// one response to use in the next, so a test that needs to call <c>find_candidates</c> over the raw
/// protocol has to know the campaign id up front. Seeding it beforehand is how.
/// </remarks>
internal static class ServerCampaigns
{
    public static async Task AddWithProfileAsync(
        string dataDirectory,
        string campaignId,
        CancellationToken cancellationToken)
    {
        await using var services = new ServiceCollection()
            .AddProspectStudioStorage(Path.Combine(dataDirectory, ServerJobs.DatabaseFileName))
            .BuildServiceProvider();

        await using (var context = await services
            .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ProspectDbContext>>()
            .CreateDbContextAsync(cancellationToken))
        {
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions
                .MigrateAsync(context.Database, cancellationToken);
        }

        var campaigns = services.GetService<ICampaignStore>()
            ?? throw new XunitException("AddProspectStudioStorage must still register an ICampaignStore (C1).");

        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        await campaigns.AddAsync(
            new Campaign
            {
                Id = campaignId,
                Name = "Stdout hygiene",
                Slug = "stdout hygiene",
                FolderPath = Path.Combine(dataDirectory, "Campaigns", "stdout-hygiene"),
                Status = CampaignStatuses.Draft,
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken);

        await campaigns.SaveProfileAsync(
            campaignId,
            await File.ReadAllTextAsync(RepoFixtures.SampleSearchProfile, cancellationToken),
            now,
            now,
            cancellationToken);

        // Teardown only: the pooled connection would otherwise keep the server's database file open.
        SqliteConnection.ClearAllPools();
    }
}
