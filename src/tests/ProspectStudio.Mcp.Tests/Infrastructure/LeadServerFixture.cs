using System.Text.Json;
using ModelContextProtocol.Client;
using ProspectStudio.Core.Leads;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// One server process in the state chunk C6's tools are used from: the three business lists imported,
/// a campaign with the sample profile, <c>find_candidates</c> run over the committed Overture extract,
/// and <c>score_leads</c> run once - which is exactly what the <c>find-leads</c> skill will have done
/// before anyone calls <c>list_leads</c>.
/// </summary>
/// <remarks>
/// It composes <see cref="DealerServerFixture"/> rather than repeating its setup, so a change to C5's
/// workspace bootstrap is felt here too. Separate from it for the reason C5 was separate from C4: the
/// C5 tests assert against a server with no scores, and tests in one collection share the process, so
/// scoring it would let test order decide which assertions held.
/// </remarks>
public sealed class LeadServerFixture : IAsyncLifetime
{
    private readonly DealerServerFixture _dealers = new();

    public McpClient Client => _dealers.Client;

    /// <summary>The data folder holding the server's SQLite file, for <see cref="ServerResearch"/>.</summary>
    public string Data => _dealers.Data;

    public string Diagnostics => _dealers.Diagnostics;

    /// <summary>
    /// A campaign with candidates found and scored once on §7.6's default weights. Shared by the
    /// read-only tests; anything that writes takes a campaign of its own from
    /// <see cref="NewScoredCampaignAsync"/>.
    /// </summary>
    public string ScoredCampaignId { get; private set; } = string.Empty;

    /// <summary>What <c>score_leads</c> returned for <see cref="ScoredCampaignId"/> on its first run.</summary>
    public JsonElement FirstScoreRun { get; private set; }

    /// <summary>
    /// A campaign with candidates found, for calls a test expects to be <strong>refused</strong>. Nothing
    /// reads its scores or its research, so a refusal that fails to happen damages only the test that
    /// expected it.
    /// </summary>
    /// <remarks>
    /// Learned the hard way: a range check that was not yet implemented let a "this must be rejected" call
    /// through, and it re-scored <see cref="ScoredCampaignId"/> with weights of 2.0 and −1.0. Two unrelated
    /// tests went red in the full run and passed in isolation, which is the most expensive kind of failure
    /// to read. A test that expects a refusal must not be able to mutate state another test trusts.
    /// </remarks>
    public string RejectionCampaignId { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _dealers.InitializeAsync();

        ScoredCampaignId = await _dealers.NewCampaignWithProfileAsync();
        await FindCandidatesAsync(ScoredCampaignId);
        FirstScoreRun = await ScoreLeadsAsync(ScoredCampaignId);

        RejectionCampaignId = await NewCampaignWithCandidatesAsync();
    }

    public async Task DisposeAsync() => await _dealers.DisposeAsync();

    /// <summary>
    /// A campaign with the sample profile saved and nothing else: no <c>find_candidates</c>, so no leads.
    /// </summary>
    public Task<string> NewEmptyCampaignAsync() => _dealers.NewCampaignWithProfileAsync();

    /// <summary>
    /// <c>save_research</c> with the document's dates left exactly as given, for the few tests whose subject
    /// <strong>is</strong> a date - a stale signal next to a current one, say.
    /// <see cref="SaveResearchAsync"/> re-dates every signal to keep the scoring tests from expiring, which
    /// would erase the distinction those tests are making.
    /// </summary>
    public async Task<JsonElement> SaveResearchExactAsync(string campaignId, string leadId, JsonElement research) =>
        await ToolCall.OkAsync(
            Client,
            "save_research",
            new Dictionary<string, object?>
            {
                ["campaignId"] = campaignId,
                ["leadId"] = leadId,
                ["research"] = research,
            },
            Diagnostics);

    /// <summary>A campaign nobody else is writing to, with candidates found but not yet scored.</summary>
    public async Task<string> NewCampaignWithCandidatesAsync()
    {
        var campaignId = await _dealers.NewCampaignWithProfileAsync();
        await FindCandidatesAsync(campaignId);
        return campaignId;
    }

    /// <summary>A campaign nobody else is writing to, with candidates found and scored.</summary>
    public async Task<string> NewScoredCampaignAsync()
    {
        var campaignId = await NewCampaignWithCandidatesAsync();
        await ScoreLeadsAsync(campaignId);
        return campaignId;
    }

    public async Task<JsonElement> FindCandidatesAsync(string campaignId) =>
        await ToolCall.OkAsync(
            Client,
            "find_candidates",
            new Dictionary<string, object?> { ["campaignId"] = campaignId },
            Diagnostics);

    public async Task<JsonElement> ScoreLeadsAsync(string campaignId, object? weights = null) =>
        await ToolCall.OkAsync(
            Client,
            "score_leads",
            new Dictionary<string, object?> { ["campaignId"] = campaignId, ["weights"] = weights },
            Diagnostics);

    public async Task<JsonElement> ListLeadsAsync(string campaignId, Dictionary<string, object?>? filters = null)
    {
        var arguments = new Dictionary<string, object?>(filters ?? [], StringComparer.Ordinal)
        {
            ["campaignId"] = campaignId,
        };

        return await ToolCall.OkAsync(Client, "list_leads", arguments, Diagnostics);
    }

    public async Task<JsonElement> GetLeadAsync(string campaignId, string leadId, bool includeWebExcerpt = false) =>
        await ToolCall.OkAsync(
            Client,
            "get_lead",
            new Dictionary<string, object?>
            {
                ["campaignId"] = campaignId,
                ["leadId"] = leadId,
                ["includeWebExcerpt"] = includeWebExcerpt,
            },
            Diagnostics);

    public async Task<JsonElement> UpdateLeadsAsync(string campaignId, params Dictionary<string, object?>[] updates) =>
        await ToolCall.OkAsync(
            Client,
            "update_leads",
            new Dictionary<string, object?> { ["campaignId"] = campaignId, ["updates"] = updates },
            Diagnostics);

    /// <summary>
    /// <c>save_research</c> with the signal dates moved into §7.6's window relative to the real clock -
    /// the server runs on <c>TimeProvider.System</c> and cannot be handed a fixed one, and a committed
    /// fixture date would make these tests expire. See
    /// <c>SampleResearch.WithSignalsDatedOneMonthBefore</c>.
    /// </summary>
    public async Task<JsonElement> SaveResearchAsync(string campaignId, string leadId, JsonElement research) =>
        await ToolCall.OkAsync(
            Client,
            "save_research",
            new Dictionary<string, object?>
            {
                ["campaignId"] = campaignId,
                ["leadId"] = leadId,
                ["research"] = SampleResearch.WithSignalsDatedOneMonthBefore(research, DateTimeOffset.UtcNow),
            },
            Diagnostics);

    /// <summary>
    /// Every lead in the campaign, read a page at a time, so the helper the other tests lean on also
    /// exercises paging rather than asking for an undocumented page size.
    /// </summary>
    /// <param name="pageSize">
    /// The page size to walk with. Varying it is how a test proves the order is stable: a sort that is not
    /// totally ordered gives a different answer when the page boundaries move.
    /// </param>
    public async Task<IReadOnlyList<JsonElement>> AllLeadsAsync(
        string campaignId,
        string sort = LeadSorts.ScoreDesc,
        int pageSize = LeadResponseLimits.DefaultPageSize)
    {
        var rows = new List<JsonElement>();
        var offset = 0;

        while (true)
        {
            var page = await ListLeadsAsync(campaignId, new Dictionary<string, object?>
            {
                ["limit"] = pageSize,
                ["offset"] = offset,
                ["sort"] = sort,
            });

            var batch = page.GetProperty("rows").EnumerateArray().Select(row => row.Clone()).ToList();
            rows.AddRange(batch);

            if (batch.Count == 0 || rows.Count >= page.GetProperty("total").GetInt32())
            {
                return rows;
            }

            offset += pageSize;

            if (offset > 10_000)
            {
                throw new InvalidOperationException("list_leads never ran out of pages.");
            }
        }
    }

    /// <summary>
    /// Adds one suppression row to the server's list and gives back a handle that restores the committed
    /// seven-row file when disposed. It is how a test drives the realistic late-<c>dnc</c> order: the lead
    /// is already found, routed and scored when compliance names it.
    /// </summary>
    /// <remarks>
    /// The <c>suppression</c> table is global rather than per-campaign, so this changes state every later
    /// <c>find_candidates</c> would see. Tests in one collection run sequentially and the restore is in a
    /// <c>finally</c>, so no other test observes the extra row - but it is the reason this is a scoped
    /// handle rather than a plain call.
    /// </remarks>
    public async Task<IAsyncDisposable> AddSuppressionRowAsync(
        string companyName,
        string domain,
        string zip,
        string reason)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"prospect-studio-extra-suppression-{Guid.NewGuid():N}.csv");

        await File.WriteAllTextAsync(
            path,
            "company_name,domain,address,city,state,zip,reason" + Environment.NewLine
            + $"{companyName},{domain},,,TX,{zip},{reason}" + Environment.NewLine);

        var imported = await ToolCall.OkAsync(
            Client,
            "import_list",
            new Dictionary<string, object?> { ["kind"] = "suppression", ["path"] = path },
            Diagnostics);

        // The handle is taken before anything is asserted, so a failed assertion still restores the list.
        // Leaving the extra row behind would quietly change the suppression count every later test reads.
        var handle = new ExtraSuppressionRow(this, path);

        try
        {
            imported.GetProperty("errors").EnumerateArray().ShouldBeEmpty($"Got: {imported}");
            imported.GetProperty("imported").GetInt32().ShouldBe(
                1,
                $"'{companyName}' is not already on the committed list, so the row is new. Got: {imported}");
        }
        catch
        {
            await handle.DisposeAsync();
            throw;
        }

        return handle;
    }

    /// <summary>Restores the committed suppression list and removes the temporary file.</summary>
    private sealed class ExtraSuppressionRow(LeadServerFixture server, string path) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // replace: true makes the committed workspace file authoritative again, which deletes the
                // extra row rather than leaving it to suppress leads in every campaign created afterwards.
                await ToolCall.OkAsync(
                    server.Client,
                    "import_list",
                    new Dictionary<string, object?> { ["kind"] = "suppression", ["replace"] = true },
                    server.Diagnostics);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    /// <summary>The lead id of one company by name, which is how a test names a fixture lead.</summary>
    public async Task<string> LeadIdForAsync(string campaignId, string companyName)
    {
        var rows = await AllLeadsAsync(campaignId);

        var matches = rows
            .Where(row => string.Equals(row.GetProperty("name").GetString(), companyName, StringComparison.Ordinal))
            .ToList();

        if (matches.Count != 1)
        {
            throw new Xunit.Sdk.XunitException(
                $"expected exactly one lead named '{companyName}' in {campaignId}, found {matches.Count}. "
                + $"The campaign has {rows.Count} leads.{Environment.NewLine}{Diagnostics}");
        }

        return matches[0].GetProperty("id").GetString()
            ?? throw new Xunit.Sdk.XunitException("a list_leads row has no id.");
    }
}

[CollectionDefinition(Name)]
public sealed class LeadServerCollection : ICollectionFixture<LeadServerFixture>
{
    public const string Name = "mcp-server-with-scored-leads";
}
