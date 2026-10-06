using System.Text.Json;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Reference;

namespace ProspectStudio.Core.Candidates;

/// <summary>
/// The rules behind <c>find_candidates</c> (mcp-tools.md §find_candidates, technical-design §6.3 and
/// §7.1-7.4): resolve the scope, read the targets and exclusions off the saved profile, query the
/// local extract per state, normalize and dedupe, store, suppress, route to dealers, and hand back a
/// compact summary.
/// </summary>
public sealed class CandidateSearchService(
    ICampaignStore campaigns,
    ICandidateStore candidates,
    ILeadRoutingStore routing,
    IPlacesSource places,
    IOvertureDataInventory overture,
    GeographyService geography,
    TimeProvider clock)
{
    /// <summary>The default cap on rows pulled from one state's extract.</summary>
    public const int DefaultLimit = 5000;

    /// <summary>How many example leads the summary carries (mcp-tools.md: "10 compact leads").</summary>
    public const int SampleSize = 10;

    /// <summary>The default <c>lookup_overture_categories</c> page, as its example uses.</summary>
    public const int DefaultCategoryLimit = 15;

    public async Task<FindCandidatesSummary> FindAsync(
        FindCandidatesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var campaign = await campaigns.FindAsync(request.CampaignId, cancellationToken).ConfigureAwait(false)
            ?? throw new CampaignNotFoundException(request.CampaignId);

        using var profileDocument = campaign.ProfileJson is { Length: > 0 } saved
            ? JsonDocument.Parse(saved)
            : null;
        var profile = profileDocument?.RootElement ?? default;

        var scope = await ResolveAsync(request, profile, cancellationToken).ConfigureAwait(false);
        var query = BuildQuery(request, profile, scope);

        foreach (var state in scope.States)
        {
            if (!places.IsReady(state))
            {
                throw new CandidatesNotReadyException(
                    $"No Overture Places extract for {state} (release {overture.Release}).",
                    $"Run prepare_data for {state} first, or "
                    + $"'ProspectStudio.Mcp setup --states {state}' from a terminal.");
            }
        }

        var found = new List<OverturePlace>();
        foreach (var state in scope.States)
        {
            var perState = query with { CountyFips = CountiesIn(state, scope.CountyFips) };
            if (perState.CountyFips.Count == 0)
            {
                continue;
            }

            found.AddRange(await places.FindAsync(state, perState, cancellationToken).ConfigureAwait(false));
        }

        var sites = found
            .DistinctBy(place => place.Id, StringComparer.Ordinal)
            .Select(place => PlaceFields.ToSite(place, overture.Release))
            .ToList();

        var groups = CandidateDedupe.Group(sites);

        await campaigns
            .SaveGeographyAsync(
                campaign.Id,
                CampaignGeography.Serialize(scope),
                clock.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);

        if (request.Replace)
        {
            await candidates
                .ClearCandidatesWithoutResearchAsync(campaign.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        await candidates.StoreAsync(campaign.Id, groups, cancellationToken).ConfigureAwait(false);

        // Suppression before assignment, so a company that is never going to be mailed is not counted
        // under a dealer. The two are order-independent either way (§7.3, §7.4), which is what lets a
        // skill call the tools separately and reach the same state.
        var suppression = await routing
            .ApplySuppressionAsync(campaign.Id, cancellationToken)
            .ConfigureAwait(false);
        var assignment = await routing
            .AssignDealersAsync(campaign.Id, cancellationToken)
            .ConfigureAwait(false);

        // The sample is read back from the database rather than taken off the groups, so a company that
        // suppression has just removed is never offered as an example.
        var sampled = await candidates
            .SampleCandidatesAsync(campaign.Id, SampleSize, cancellationToken)
            .ConfigureAwait(false);

        var duplicates = groups.Sum(group => group.Duplicates.Count);

        return new FindCandidatesSummary(
            Found: groups.Count + duplicates,
            Stored: groups.Count,
            Duplicates: duplicates,
            Suppressed: suppression.ByReason,
            CoverageGaps: assignment.Gaps,
            ByCategory: ByCategory(groups),
            // byDealer is shown to a person, so it carries the dealer's name rather than its id
            // (mcp-tools.md §find_candidates shows "Gulf Lift Equipment").
            ByDealer: [.. assignment.ByDealer.Select(row => new DealerCandidateCount(row.Name, row.Leads))],
            Sample: sampled);
    }

    public async Task<IReadOnlyList<OvertureCategoryCount>> LookupCategoriesAsync(
        string? query,
        string? state,
        int? limit,
        CancellationToken cancellationToken)
    {
        var wanted = string.IsNullOrWhiteSpace(state)
            ? overture.States.FirstOrDefault()
            : state.Trim().ToUpperInvariant();

        if (wanted is null || !places.IsReady(wanted))
        {
            throw new CandidatesNotReadyException(
                wanted is null
                    ? $"No Overture Places extract is prepared (release {overture.Release})."
                    : $"No Overture Places extract for {wanted} (release {overture.Release}).",
                $"Run prepare_data for {wanted ?? "the states you need"} first.");
        }

        return await places
            .CountCategoriesAsync(wanted, query, limit is > 0 ? limit.Value : DefaultCategoryLimit, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ResolvedGeography> ResolveAsync(
        FindCandidatesRequest request,
        JsonElement profile,
        CancellationToken cancellationToken)
    {
        var asked = request.Geo ?? SearchProfileTargets.Geography(profile)
            ?? throw new CandidatesNotReadyException(
                "The campaign has no geography: neither the call nor the saved search profile names one.",
                "Pass geo, or save a search profile with a geography first.");

        try
        {
            return await geography.ResolveAsync(asked, cancellationToken).ConfigureAwait(false);
        }
        catch (GeographyNotReadyException exception)
        {
            throw new CandidatesNotReadyException(
                exception.Message,
                exception.Hint ?? "Run prepare_data first.");
        }
    }

    private static CandidateQuery BuildQuery(
        FindCandidatesRequest request,
        JsonElement profile,
        ResolvedGeography scope)
    {
        var categories = request.Categories ?? SearchProfileTargets.Categories(profile);
        var keywords = request.Keywords ?? SearchProfileTargets.Keywords(profile);

        if (categories.Count == 0 && keywords.Count == 0)
        {
            throw new CandidateRequestException(
                "No categories and no keywords to search for.",
                "Pass categories or keywords, or save a search profile whose segments name some.");
        }

        return new CandidateQuery(
            scope.CountyFips,
            categories,
            keywords,
            request.MinConfidence
                ?? SearchProfileTargets.MinConfidence(profile)
                ?? SearchProfileTargets.DefaultMinConfidence,
            // An override replaces the profile's list; an empty override means "exclude nothing".
            request.ExcludedCategories ?? SearchProfileTargets.ExcludedCategories(profile),
            SearchProfileTargets.ExcludedKeywords(profile),
            request.Limit is > 0 ? request.Limit.Value : DefaultLimit);
    }

    private static IReadOnlyList<string> CountiesIn(string state, IReadOnlyList<string> countyFips)
    {
        var fips = UsStates.Find(state)?.Fips;
        return fips is null ? countyFips : [.. countyFips.Where(county => county.StartsWith(fips, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Counted over the leads, so a duplicate is not counted twice, and under each place's own
    /// <c>taxonomy.primary</c> rather than the segment category that matched it - a breakdown that
    /// renamed categories would hide what was actually found.
    /// </summary>
    private static IReadOnlyList<CategoryLeadCount> ByCategory(IReadOnlyList<CandidateGroup> groups) =>
    [
        .. groups
            .GroupBy(group => group.Primary.TaxonomyPrimary ?? "(none)", StringComparer.Ordinal)
            .Select(group => new CategoryLeadCount(group.Key, group.Count()))
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.Category, StringComparer.Ordinal),
    ];

}
