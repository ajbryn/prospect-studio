using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Domain;
using ProspectStudio.Infrastructure.Storage;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// Reads a server's <c>research</c> and <c>signals</c> rows straight out of its SQLite file through the
/// production storage registration, for the one claim no tool response can show: that
/// <c>research.llm_adjustment</c> and the <c>llmAdjustment</c> inside <c>research_json</c> agree.
/// </summary>
/// <remarks>
/// The column is a deliberate denormalization - the document stays opaque TEXT (CLAUDE.md), so scoring
/// reads the column instead of filtering inside the JSON. Two copies of one number can drift, and
/// nothing else would notice: the score would come from the column while <c>get_lead</c> showed the
/// document.
/// </remarks>
internal static class ServerResearch
{
    public static async Task<StoredResearch?> ReadAsync(
        string dataDirectory,
        string campaignId,
        string leadId,
        CancellationToken cancellationToken)
    {
        await using var services = new ServiceCollection()
            .AddProspectStudioStorage(Path.Combine(dataDirectory, ServerJobs.DatabaseFileName))
            .BuildServiceProvider();

        var factory = services.GetRequiredService<IDbContextFactory<ProspectDbContext>>();

        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);

            var research = await context.Set<Research>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.CampaignId == campaignId && row.LeadId == leadId,
                    cancellationToken);

            if (research is null)
            {
                return null;
            }

            var signals = await context.Set<Signal>()
                .AsNoTracking()
                .Where(row => row.CampaignId == campaignId && row.LeadId == leadId)
                .OrderBy(row => row.Id)
                .Select(row => new StoredSignal(row.Type, row.Text, row.Url, row.Date))
                .ToListAsync(cancellationToken);

            return new StoredResearch(research.ResearchJson, research.LlmAdjustment, research.SavedAt, signals);
        }
        finally
        {
            // Teardown only: the pooled connection would otherwise keep the server's database open.
            SqliteConnection.ClearAllPools();
        }
    }
}

internal sealed record StoredSignal(string Type, string Text, string Url, string Date);

internal sealed record StoredResearch(
    string ResearchJson,
    int LlmAdjustment,
    DateTimeOffset SavedAt,
    IReadOnlyList<StoredSignal> Signals);
