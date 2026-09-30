using Microsoft.EntityFrameworkCore;
using ProspectStudio.Core.Domain;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// The Prospect Studio database. Tables arrive per chunk through migrations named
/// <c>C&lt;N&gt;_&lt;Description&gt;</c> (technical-design §5.3).
/// </summary>
public sealed class ProspectDbContext(DbContextOptions<ProspectDbContext> options) : DbContext(options)
{
    public DbSet<Campaign> Campaigns => Set<Campaign>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProspectDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }
}
