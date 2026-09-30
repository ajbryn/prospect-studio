using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context without starting the MCP server, whose
/// entry point owns stdout. The path is never opened: the tool only needs the model and the provider.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ProspectDbContext>
{
    public ProspectDbContext CreateDbContext(string[] args)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = "design-time.db" }.ToString();
        var options = new DbContextOptionsBuilder<ProspectDbContext>()
            .UseSqlite(connectionString)
            .Options;

        return new ProspectDbContext(options);
    }
}
