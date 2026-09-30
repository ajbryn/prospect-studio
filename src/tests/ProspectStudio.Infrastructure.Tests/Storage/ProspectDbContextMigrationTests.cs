using Microsoft.EntityFrameworkCore;
using ProspectStudio.Core.Domain;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Storage;

/// <summary>
/// Migrations and connection setup against real SQLite in a temp file (CLAUDE.md: never the EF
/// InMemory provider). Assertions stay provider-neutral - EF's own migration API, not
/// <c>sqlite_master</c> or SQLite type names - except for the three pragmas, which are the one
/// sanctioned SQLite-specific expectation.
/// </summary>
public class ProspectDbContextMigrationTests
{
    [Fact]
    public async Task Migrations_apply_cleanly_to_a_new_database()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);

        await using var context = await database.MigrateAsync(timeout.Token);

        var applied = await context.Database.GetAppliedMigrationsAsync(timeout.Token);
        applied.ShouldNotBeEmpty("MigrateAsync applied no migrations at all.");
        (await context.Database.GetPendingMigrationsAsync(timeout.Token)).ShouldBeEmpty();

        File.Exists(database.DatabasePath).ShouldBeTrue($"no database file at '{database.DatabasePath}'.");
    }

    [Fact]
    public async Task The_C1_Campaigns_migration_creates_a_usable_campaigns_table()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);

        await using var context = await database.MigrateAsync(timeout.Token);

        (await context.Database.GetAppliedMigrationsAsync(timeout.Token))
            .ShouldContain(id => id.EndsWith("_C1_Campaigns", StringComparison.Ordinal));

        context.Campaigns.Add(NewCampaign("cmp_AAAAAA", "Houston Test"));
        await context.SaveChangesAsync(timeout.Token);

        (await context.Campaigns.CountAsync(timeout.Token)).ShouldBe(1);
    }

    [Fact]
    public async Task Migrating_an_already_migrated_database_is_a_no_op()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);

        IReadOnlyList<string> afterFirstRun;
        await using (var first = await database.MigrateAsync(timeout.Token))
        {
            afterFirstRun = [.. await first.Database.GetAppliedMigrationsAsync(timeout.Token)];
            first.Campaigns.Add(NewCampaign("cmp_BBBBBB", "Already Migrated"));
            await first.SaveChangesAsync(timeout.Token);
        }

        await using var second = await database.MigrateAsync(timeout.Token);

        (await second.Database.GetAppliedMigrationsAsync(timeout.Token)).ShouldBe(afterFirstRun);
        (await second.Database.GetPendingMigrationsAsync(timeout.Token)).ShouldBeEmpty();

        // A second run that re-created tables would have thrown, or lost this row.
        (await second.Campaigns.CountAsync(timeout.Token)).ShouldBe(1);
    }

    [Fact]
    public async Task Data_survives_closing_and_reopening_the_database()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();

        await using (var first = TempDatabase.In(directory.Path))
        {
            await using var context = await first.MigrateAsync(timeout.Token);
            context.Campaigns.Add(NewCampaign("cmp_CCCCCC", "Survives A Restart"));
            await context.SaveChangesAsync(timeout.Token);
        }

        await using var reopened = TempDatabase.In(directory.Path);
        await using var second = await reopened.MigrateAsync(timeout.Token);

        var campaign = await second.Campaigns.AsNoTracking()
            .SingleAsync(row => row.Id == "cmp_CCCCCC", timeout.Token);

        campaign.Name.ShouldBe("Survives A Restart");
        campaign.Slug.ShouldBe("survives-a-restart");
        campaign.FolderPath.ShouldBe(@"C:\Workspace\Campaigns\2026-10 Survives A Restart");
        campaign.CreatedAt.ShouldBe(CreatedAt);
    }

    /// <summary>
    /// The pragmas from technical-design §5.3, applied by the connection interceptor. This is the one
    /// place where a SQLite-specific expectation is sanctioned by the provider-neutrality rule.
    /// </summary>
    [Fact]
    public async Task Connections_apply_the_wal_busy_timeout_and_foreign_key_pragmas()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        await using var context = await database.MigrateAsync(timeout.Token);

        // Open through EF, not through the raw DbConnection, so the interceptor runs.
        await context.Database.OpenConnectionAsync(timeout.Token);
        try
        {
            (await ScalarAsync(context, "PRAGMA journal_mode", timeout.Token))
                .ShouldBe("wal", "the connection interceptor must run PRAGMA journal_mode=WAL.");
            (await ScalarAsync(context, "PRAGMA busy_timeout", timeout.Token))
                .ShouldBe("5000", "the connection interceptor must run PRAGMA busy_timeout=5000.");
            (await ScalarAsync(context, "PRAGMA foreign_keys", timeout.Token))
                .ShouldBe("1", "the connection interceptor must run PRAGMA foreign_keys=ON.");
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 14, 30, 0, TimeSpan.Zero);

    private static Campaign NewCampaign(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Slug = name.ToLowerInvariant().Replace(' ', '-'),
        Product = "Scissor & boom lifts",
        FolderPath = $@"C:\Workspace\Campaigns\2026-10 {name}",
        Status = "draft",
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
    };

    private static async Task<string?> ScalarAsync(DbContext context, string sql, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value?.ToString()?.ToLowerInvariant();
    }
}
