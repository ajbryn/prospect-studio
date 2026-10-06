using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Core.Domain;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;
using Xunit.Sdk;

namespace ProspectStudio.Infrastructure.Tests.Dealers;

/// <summary>
/// <c>assign_dealers</c> (technical-design §7.4) and <c>apply_suppression</c> (§7.3) over a campaign's
/// real leads in real SQLite: the two halves of chunk C5's goal, "every candidate is routed to a dealer
/// (or flagged as a gap), and existing customers and dealers are removed".
/// </summary>
/// <remarks>
/// <para>
/// The leads are built from <c>poc/fixtures/sample-places.csv</c> through the production field
/// extraction and dedupe, so the arithmetic here is the arithmetic <c>find_candidates</c> reports:
/// <strong>83 leads</strong> (86 rows survive the profile, three collapse as duplicates), of which
/// <strong>7</strong> are suppressed, leaving <strong>76</strong> candidates - 75 routed and 1 gap.
/// </para>
/// <para>
/// <strong>Two readings the spec leaves open, resolved here.</strong> First, a suppressed lead carries
/// no dealer: it will never be mailed, so counting it under a dealer would overstate that dealer's
/// list and counting it as a coverage gap would send someone looking for a territory that is not
/// missing. Second, that choice makes the two tools order-independent, which is what
/// <see cref="Routing_InEitherOrder_ReachesTheSameFinalState"/> pins - otherwise
/// <c>find_candidates</c> calling them in one order and a skill calling them in the other would give
/// different answers.
/// </para>
/// </remarks>
public class LeadRoutingTests
{
    private const string CampaignId = "cmp_C5TEST";

    /// <summary>Leads the sample profile produces: 86 rows survive, three collapse as duplicates.</summary>
    private const int Leads = 83;

    /// <summary>Leads §7.3 removes: 3 dealers, 2 customers, 1 dnc and 1 competitor.</summary>
    private const int Suppressed = 7;

    /// <summary>Leads left to route, of which one is a coverage gap.</summary>
    private const int Candidates = Leads - Suppressed;

    private static CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(60));

    [Fact]
    public async Task ApplySuppression_RemovesEveryListedCompanyWithItsReasonAndMatchingRowId()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        var result = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        result.Suppressed.ShouldBe(Suppressed, Describe(result));
        result.ByReason.ShouldContainKeyAndValue(SuppressionReasons.Dealer, 3, "all three dealers in dealers.csv.");
        result.ByReason.ShouldContainKeyAndValue(SuppressionReasons.Customer, 2);
        result.ByReason.ShouldContainKeyAndValue(SuppressionReasons.Dnc, 1, "reachable only by §7.3's exact-name rule.");
        result.ByReason.ShouldContainKeyAndValue(SuppressionReasons.Competitor, 1);
        result.ByReason.Values.Sum().ShouldBe(Suppressed, "the breakdown and the total cannot disagree.");

        var leads = await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token);
        var suppressed = leads
            .Where(lead => lead.Status == LeadStatuses.Suppressed)
            .ToDictionary(lead => lead.OvertureId, lead => lead, StringComparer.Ordinal);

        suppressed.Keys.Order(StringComparer.Ordinal).ToList().ShouldBe(
            ["fx_0015", "fx_0016", "fx_0019", "fx_0020", "fx_0117", "fx_0118", "fx_0119"],
            "the two dealers and the competitor C4 placed in target categories, the customer matched by "
            + "domain, and C5's three new rows. Anything missing here means a §7.3 rule did not run.");

        var rows = (await fixture.Dealers.GetSuppressionAsync(timeout.Token))
            .ToDictionary(row => row.Id, row => row.NameNorm, StringComparer.Ordinal);

        foreach (var (overtureId, expectedName) in new[]
                 {
                     ("fx_0117", "northfield storage"),
                     ("fx_0118", "pineland equipment"),
                     ("fx_0119", "coastal crane and rigging"),
                 })
        {
            var lead = suppressed[overtureId];

            lead.SuppressionId.ShouldNotBeNull(
                $"{overtureId}: §7.3 says to store the reason AND the matching row id. Without the id "
                + "the marketer cannot find out which list entry did this.");
            rows.ShouldContainKey(
                lead.SuppressionId,
                $"{overtureId}: the stored id must be a suppression row's id, not a reason or a name.");
            rows[lead.SuppressionId].ShouldBe(
                expectedName,
                $"{overtureId} has to point at the row that actually matched it.");
        }

        suppressed["fx_0117"].SuppressionReason.ShouldBe(SuppressionReasons.Dnc);
        suppressed["fx_0118"].SuppressionReason.ShouldBe(SuppressionReasons.Dealer);
        suppressed["fx_0119"].SuppressionReason.ShouldBe(SuppressionReasons.Customer);
    }

    [Fact]
    public async Task ApplySuppression_LeavesTheNearThresholdControlAlone()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        var control = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Single(lead => lead.OvertureId == SampleDealers.FuzzyControlPlaceId);

        control.Status.ShouldBe(
            LeadStatuses.Candidate,
            "'Coastal Crane and Haul' scores 0.908 against 'coastal crane and rigging' at the same ZIP - "
            + "below §7.3's 0.92 and above §7.9's 0.90. Suppressing it would lose a real prospect, and "
            + "would mean the matchback threshold had been wired into suppression.");
        control.SuppressionReason.ShouldBeNull();
        control.SuppressionId.ShouldBeNull();
    }

    [Fact]
    public async Task ApplySuppression_ACompanyTheMarketerAlreadyApproved_IsStillSuppressed()
    {
        // The compliance case, and the one time suppression matters most. A do-not-contact request that
        // arrives AFTER approval has to override the approval: the lead is already on its way to a
        // mailing list, and "we approved them last week" is not a defence for posting to somebody who
        // asked not to be contacted. If routing only ever looks at `candidate`, importing that dnc row
        // does nothing at all and nothing anywhere says so.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        var target = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Single(lead => lead.OvertureId == SampleDealers.NameMatchPlaceId);

        await SetStatusAsync(database, target.LeadId, LeadStatuses.Approved, timeout.Token);

        var result = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        var after = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Single(lead => lead.OvertureId == SampleDealers.NameMatchPlaceId);

        after.Status.ShouldBe(
            LeadStatuses.Suppressed,
            $"{SampleDealers.NameMatchPlaceId} is named by the dnc row in suppression.csv. An approved "
            + "lead is not exempt from the suppression list - approval is a decision about whether to "
            + "mail somebody, and this is a decision about whether we are allowed to.");
        after.SuppressionReason.ShouldBe(SuppressionReasons.Dnc);
        after.SuppressionId.ShouldNotBeNull();

        result.ApprovedSuppressed.ShouldBe(
            1,
            "and it is reported on its own, not folded into the reason totals. The marketer's mailing "
            + "list just got one shorter; a figure hidden inside a bigger number is a change they would "
            + $"never see. {Describe(result)}");
        result.ByReason.ShouldContainKeyAndValue(
            SuppressionReasons.Dnc,
            1,
            $"it still counts under its reason as well. {Describe(result)}");
    }

    [Fact]
    public async Task ApplySuppression_WithNothingApproved_LeavesTheDowngradeCountAtZero()
    {
        // The other half: the figure has to mean something. If it were simply the suppressed total, a
        // run that touched nobody's approved list would still report a shrinking mailing list.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        var result = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        result.Suppressed.ShouldBe(Suppressed, Describe(result));
        result.ApprovedSuppressed.ShouldBe(
            0,
            $"seven leads were suppressed and none of them had been approved. {Describe(result)}");
    }

    [Fact]
    public async Task ApplySuppression_DoesNotTouchAnApprovedLeadThatIsNotOnTheList()
    {
        // Suppression ignoring status must not become suppression rewriting status. Only the leads the
        // list actually names may move.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        var safe = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Single(lead => lead.OvertureId == SampleDealers.FuzzyControlPlaceId);

        await SetStatusAsync(database, safe.LeadId, LeadStatuses.Approved, timeout.Token);

        var result = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        var after = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Single(lead => lead.OvertureId == SampleDealers.FuzzyControlPlaceId);

        after.Status.ShouldBe(
            LeadStatuses.Approved,
            "'Coastal Crane and Haul' scores 0.908, below §7.3's threshold, so nothing about it matched "
            + "and its approval stands.");
        result.ApprovedSuppressed.ShouldBe(0, Describe(result));
    }

    [Fact]
    public async Task ApplySuppression_AManualOverride_KeepsItsDealerAndStaysAnOverride()
    {
        // Suppression clears the dealer columns so that it and assign_dealers are order-independent -
        // but an override is the user's own edit, and §7.4 says re-assignment never overwrites it.
        // Clearing it here would lose that edit by the back door: assign_dealers would then re-route the
        // lead on its next run and the marketer's decision would be gone with nothing to show it.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        await fixture.Routing.AssignDealersAsync(CampaignId, timeout.Token);

        var target = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Single(lead => lead.OvertureId == SampleDealers.NameMatchPlaceId);

        await OverrideAsync(database, target.LeadId, "pine", "pine-north", timeout.Token);

        await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        var after = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Single(lead => lead.OvertureId == SampleDealers.NameMatchPlaceId);

        after.Status.ShouldBe(LeadStatuses.Suppressed, "the dnc row still suppresses it.");
        after.Assignment.ShouldBe(
            Assignments.Override,
            "the override survives. Nothing writes 'override' before C6, so this is latent today - which "
            + "is exactly why it needs pinning now, before update_leads and the workbook are built on "
            + "top of it.");
        after.DealerId.ShouldBe("pine", "and so does the dealer the user moved it to.");
        after.BranchId.ShouldBe("pine-north");
    }

    [Fact]
    public async Task Routing_WithAnOverriddenLead_IsStillOrderIndependent()
    {
        // The two properties have to hold together, and the obvious fix for the one above breaks this.
        // Leaving the dealer columns alone for every suppressed lead would make suppress-then-assign end
        // with dealer=null where assign-then-suppress ends with dealer=bay, because assign_dealers skips
        // leads that are already suppressed. The hybrid - clear the columns unless the lead is an
        // override - is the only shape that satisfies both.
        using var timeout = Timeout();

        using var first = new TempDirectory();
        await using var suppressFirst = TempDatabase.In(first.Path);
        var a = await SeedAsync(suppressFirst, timeout.Token);
        await OverrideTargetAsync(suppressFirst, a, timeout.Token);
        await a.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);
        await a.Routing.AssignDealersAsync(CampaignId, timeout.Token);

        using var second = new TempDirectory();
        await using var assignFirst = TempDatabase.In(second.Path);
        var b = await SeedAsync(assignFirst, timeout.Token);
        await OverrideTargetAsync(assignFirst, b, timeout.Token);
        await b.Routing.AssignDealersAsync(CampaignId, timeout.Token);
        await b.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        var left = await StateAsync(a, timeout.Token);
        var right = await StateAsync(b, timeout.Token);

        right.ShouldBe(
            left,
            "an overridden lead that is also on the suppression list has to end in the same state "
            + "whichever tool ran last.");

        left.ShouldContain(
            row => row.StartsWith($"{SampleDealers.NameMatchPlaceId} suppressed pine pine-north override", StringComparison.Ordinal),
            "and that state is: suppressed, with the override and its dealer intact. "
            + string.Join(" | ", left.Where(row => row.StartsWith(SampleDealers.NameMatchPlaceId, StringComparison.Ordinal))));

        // Overrides the lead the dnc row names, so one lead is both suppressed and overridden.
        static async Task OverrideTargetAsync(TempDatabase database, Fixture fixture, CancellationToken cancellationToken)
        {
            var target = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, cancellationToken))
                .Single(lead => lead.OvertureId == SampleDealers.NameMatchPlaceId);

            await OverrideAsync(database, target.LeadId, "pine", "pine-north", cancellationToken);
        }
    }

    [Fact]
    public async Task ApplySuppression_AnApprovedLeadReleasedFromTheList_GetsItsApprovalBack()
    {
        // The round trip. A do-not-contact request arrives after approval, so the lead is suppressed;
        // the request is later withdrawn and the row comes off the list. Without somewhere to remember
        // what the lead was, release hands it back as a plain candidate and the approval is gone - which
        // looks like nothing at all happened, because the lead is still there.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        var leadId = await LeadIdAsync(fixture, SampleDealers.NameMatchPlaceId, timeout.Token);
        await SetStatusAsync(database, leadId, LeadStatuses.Approved, timeout.Token);

        var suppressed = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        suppressed.ApprovedSuppressed.ShouldBe(1, Describe(suppressed));
        (await StatusOfAsync(database, leadId, timeout.Token)).ShouldBe(LeadStatuses.Suppressed);
        (await PreSuppressionStatusAsync(database, leadId, timeout.Token)).ShouldBe(
            LeadStatuses.Approved,
            "the status it came from has to be written down at the moment it is taken away; there is "
            + "nowhere else to recover it from afterwards.");

        // The step that matters. A second pass sees a lead that is already suppressed, and the obvious
        // shape - write down whatever status you found - would record 'suppressed' as the status to go
        // back to. The decision would then be lost one run later instead of immediately, which is worse:
        // the first run looks correct, so nothing points at the bug.
        var again = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        again.Changed.ShouldBe(0, $"nothing to do the second time. {Describe(again)}");
        again.ApprovedSuppressed.ShouldBe(
            0,
            $"it was already suppressed, so this run took nobody out of approved. {Describe(again)}");
        (await PreSuppressionStatusAsync(database, leadId, timeout.Token)).ShouldBe(
            LeadStatuses.Approved,
            "still 'approved' after a re-run. If this is 'suppressed', the remembered status is being "
            + "overwritten with the current one and the release below would restore the lead to the "
            + "state it was already in.");

        // The row comes off the list, which needs replace: an ordinary upsert leaves it in place.
        var shortened = await WithoutNorthfieldAsync(directory.Path, timeout.Token);
        var reimported = await fixture.Importer.ImportAsync(
            ImportListKinds.Suppression,
            shortened,
            reason: null,
            cancellationToken: timeout.Token,
            replace: true);

        reimported.Removed.ShouldBe(1, "the dnc row is gone from the list.");

        var released = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        released.DecisionsRestored.ShouldBe(
            1,
            "a mailing list growing back is as much a change as one shrinking, so it is reported rather "
            + $"than left to be inferred from a total. {Describe(released)}");
        released.Suppressed.ShouldBe(Suppressed - 1, Describe(released));

        (await StatusOfAsync(database, leadId, timeout.Token)).ShouldBe(
            LeadStatuses.Approved,
            "the approval comes back. A release to 'candidate' would quietly discard a decision a person "
            + "made, and the lead would drop out of the approved set with nothing to say why.");

        var lead = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Single(row => row.OvertureId == SampleDealers.NameMatchPlaceId);

        lead.SuppressionReason.ShouldBeNull("it is not suppressed any more.");
        lead.SuppressionId.ShouldBeNull();
        (await PreSuppressionStatusAsync(database, leadId, timeout.Token)).ShouldBeNull(
            "and the remembered status is cleared with it, or the next suppression would restore a "
            + "status from two rounds ago.");
    }

    [Theory]
    [InlineData(LeadStatuses.Approved)]
    [InlineData(LeadStatuses.Review)]
    [InlineData(LeadStatuses.Hold)]
    [InlineData(LeadStatuses.Rejected)]
    public async Task ApplySuppression_RemembersAndRestoresEveryStatusAPersonDecided(string decided)
    {
        // All four are decisions somebody made, so all four survive the round trip. 'rejected' matters
        // as much as 'approved': restoring it as 'candidate' would put a lead the marketer had already
        // turned down back in front of them.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        var leadId = await LeadIdAsync(fixture, SampleDealers.NameMatchPlaceId, timeout.Token);
        await SetStatusAsync(database, leadId, decided, timeout.Token);

        var suppressed = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        suppressed.ApprovedSuppressed.ShouldBe(
            decided == LeadStatuses.Approved ? 1 : 0,
            "approvedSuppressed counts leads taken out of 'approved' specifically - that is the one that "
            + "shortens the mailing list. The other three are decisions too, but they were not going to "
            + $"be mailed. {Describe(suppressed)}");

        var shortened = await WithoutNorthfieldAsync(directory.Path, timeout.Token);
        await fixture.Importer.ImportAsync(
            ImportListKinds.Suppression,
            shortened,
            reason: null,
            cancellationToken: timeout.Token,
            replace: true);

        var released = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        released.DecisionsRestored.ShouldBe(1, Describe(released));
        (await StatusOfAsync(database, leadId, timeout.Token)).ShouldBe(decided, Describe(released));
    }

    [Fact]
    public async Task ApplySuppression_ReleasingAnOrdinaryCandidate_IsNotCountedAsARestoredDecision()
    {
        // The count has to mean something. Releasing a lead that was only ever a candidate is routine -
        // it happens every time somebody tidies up the suppression list - and reporting it as a restored
        // decision would make the figure worthless for the case it exists for.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        var leadId = await LeadIdAsync(fixture, SampleDealers.NameMatchPlaceId, timeout.Token);

        var shortened = await WithoutNorthfieldAsync(directory.Path, timeout.Token);
        await fixture.Importer.ImportAsync(
            ImportListKinds.Suppression,
            shortened,
            reason: null,
            cancellationToken: timeout.Token,
            replace: true);

        var released = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        released.Changed.ShouldBe(1, $"one lead came off the list. {Describe(released)}");
        released.DecisionsRestored.ShouldBe(
            0,
            $"nobody had decided anything about it, so nothing was restored. {Describe(released)}");
        (await StatusOfAsync(database, leadId, timeout.Token)).ShouldBe(LeadStatuses.Candidate);
    }

    [Fact]
    public async Task ApplySuppression_ReleasingALeadWithNoRememberedStatus_FallsBackToCandidate()
    {
        // The upgrade path, and it covers every row already in a real database: those leads were
        // suppressed before the column existed, so there is nothing to restore them to. They have to
        // release cleanly as candidates rather than failing or sticking at 'suppressed' for good.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        var leadId = await LeadIdAsync(fixture, SampleDealers.NameMatchPlaceId, timeout.Token);
        await SetStatusAsync(database, leadId, LeadStatuses.Approved, timeout.Token);
        await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        // Exactly what a row written by the previous build looks like: suppressed, with nothing
        // remembered.
        await ForgetPreSuppressionStatusAsync(database, leadId, timeout.Token);

        var shortened = await WithoutNorthfieldAsync(directory.Path, timeout.Token);
        await fixture.Importer.ImportAsync(
            ImportListKinds.Suppression,
            shortened,
            reason: null,
            cancellationToken: timeout.Token,
            replace: true);

        var released = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        (await StatusOfAsync(database, leadId, timeout.Token)).ShouldBe(
            LeadStatuses.Candidate,
            "with nothing recorded, 'candidate' is the only honest answer - and it has to be reached "
            + "rather than left suppressed for ever, which is what a missing fallback would do.");
        released.DecisionsRestored.ShouldBe(
            0,
            $"nothing was known, so nothing is claimed to have been restored. {Describe(released)}");
    }

    [Fact]
    public async Task ApplySuppression_RunTwice_ChangesNothingTheSecondTime()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        var first = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);
        var second = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        first.Changed.ShouldBe(Suppressed, Describe(first));
        second.Changed.ShouldBe(
            0,
            $"mcp-tools.md §apply_suppression: 'Manual overrides are preserved' and NFR-3 makes every "
            + $"tool re-runnable. {Describe(second)}");
        second.Suppressed.ShouldBe(
            Suppressed,
            $"the total is what the campaign holds, not what this run did. {Describe(second)}");
    }

    [Fact]
    public async Task AssignDealers_RoutesEachLeadToItsTerritoryDealer()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);
        var result = await fixture.Routing.AssignDealersAsync(CampaignId, timeout.Token);

        result.Assigned.ShouldBe(75, Describe(result));
        result.Gaps.ShouldBe(1, Describe(result));
        result.Overrides.ShouldBe(0, $"nothing has been overridden yet. {Describe(result)}");
        (result.Assigned + result.Gaps).ShouldBe(
            Candidates,
            $"every candidate is either routed or a gap - that is chunk C5's goal. {Describe(result)}");

        var byDealer = result.ByDealer.ToDictionary(row => row.DealerId, row => row.Leads, StringComparer.Ordinal);

        byDealer.ShouldContainKeyAndValue("gulf", 32, Describe(result));
        byDealer.ShouldContainKeyAndValue("bay", 24, Describe(result));
        byDealer.ShouldContainKeyAndValue("pine", 19, Describe(result));
        result.ByDealer.ShouldAllBe(
            row => row.Name.Length > 0,
            "byDealer is shown to a person, so it carries the dealer's name as well as its id "
            + "(mcp-tools.md §find_candidates shows 'Gulf Lift Equipment').");

        var leads = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .ToDictionary(lead => lead.OvertureId, lead => lead, StringComparer.Ordinal);

        // The four cases the plan names, plus the ZIP+4 one that makes truncation load-bearing.
        leads["fx_0002"].DealerId.ShouldBe(
            "bay",
            "Pasadena 77506 is a bay ZIP override, although Harris County defaults to gulf (§7.4: "
            + "'ZIP5 match wins').");
        leads["fx_0001"].DealerId.ShouldBe("gulf", "Katy 77494, Fort Bend County.");
        leads["fx_0022"].DealerId.ShouldBe("pine", "Conroe, Montgomery County.");
        leads["fx_0026"].DealerId.ShouldBe("bay", "Galveston County.");
        leads["fx_0065"].DealerId.ShouldBe(
            "bay",
            "its raw postcode is 77504-1877; routing depends on the ZIP having been truncated to five "
            + "digits on the way in.");

        leads["fx_0002"].BranchId.ShouldBe("bay-pas", "the branch comes from the winning territory row.");
        leads["fx_0002"].Assignment.ShouldBe(Assignments.Auto);
    }

    [Fact]
    public async Task AssignDealers_ALeadInACountyWithNoTerritory_IsAGapWithNoDealer()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        await fixture.Routing.AssignDealersAsync(CampaignId, timeout.Token);

        var gap = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Single(lead => lead.OvertureId == SampleDealers.CoverageGapPlaceId);

        gap.Assignment.ShouldBe(
            Assignments.Gap,
            $"San Jacinto {SampleDealers.UncoveredCountyFips} is a Houston CBSA county with no territory "
            + "row. §7.4: 'No match -> assignment=gap, dealer_id=null'.");
        gap.DealerId.ShouldBeNull();
        gap.BranchId.ShouldBeNull();
        gap.Status.ShouldBe(LeadStatuses.Candidate, "a gap is still a lead; it just has nobody to send it to.");
    }

    [Fact]
    public async Task AssignDealers_AManualOverride_SurvivesAReRun()
    {
        // The one that silently loses the user's work if it regresses: they moved a lead to another
        // dealer by hand, and the next search must not move it back.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);
        await fixture.Routing.AssignDealersAsync(CampaignId, timeout.Token);

        var overridden = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Single(lead => lead.OvertureId == "fx_0002");

        overridden.DealerId.ShouldBe("bay", "it routes to bay by ZIP before anyone touches it.");

        // update_leads arrives in C6, so the override is written the way that tool will write it.
        await OverrideAsync(database, overridden.LeadId, "pine", "pine-north", timeout.Token);

        var result = await fixture.Routing.AssignDealersAsync(CampaignId, timeout.Token);

        result.Overrides.ShouldBe(1, $"the run has to report what it left alone. {Describe(result)}");
        result.Changed.ShouldBe(0, $"nothing else moved either. {Describe(result)}");

        var after = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Single(lead => lead.OvertureId == "fx_0002");

        after.DealerId.ShouldBe(
            "pine",
            "§7.4: 'Manual overrides (update_leads, workbook) set assignment=override and are never "
            + "overwritten by re-assignment.' A silent revert to 'bay' loses an edit the user made "
            + "deliberately, and nothing else in the system would tell them.");
        after.BranchId.ShouldBe("pine-north");
        after.Assignment.ShouldBe(Assignments.Override, "and it stays an override, not reset to auto.");

        var byDealer = result.ByDealer.ToDictionary(row => row.DealerId, row => row.Leads, StringComparer.Ordinal);
        byDealer.ShouldContainKeyAndValue("bay", 23, $"one fewer than before. {Describe(result)}");
        byDealer.ShouldContainKeyAndValue(
            "pine",
            20,
            $"an overridden lead counts under the dealer it was moved to, or the dealer packet would "
            + $"not match the workbook. {Describe(result)}");
    }

    [Fact]
    public async Task AssignDealers_RunTwice_ChangesNothingTheSecondTime()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        var first = await fixture.Routing.AssignDealersAsync(CampaignId, timeout.Token);
        var second = await fixture.Routing.AssignDealersAsync(CampaignId, timeout.Token);

        first.Changed.ShouldBe(Candidates, $"every candidate was routed or flagged. {Describe(first)}");
        second.Changed.ShouldBe(0, $"NFR-3: re-running changes nothing. {Describe(second)}");
        second.Assigned.ShouldBe(first.Assigned, Describe(second));
        second.Gaps.ShouldBe(first.Gaps, Describe(second));
    }

    [Fact]
    public async Task Routing_InEitherOrder_ReachesTheSameFinalState()
    {
        // find_candidates runs suppression first; a skill may call the two tools either way round. If a
        // suppressed lead kept a dealer, the two orders would disagree and byDealer would depend on
        // which tool was called last.
        using var timeout = Timeout();

        using var first = new TempDirectory();
        await using var suppressFirst = TempDatabase.In(first.Path);
        var a = await SeedAsync(suppressFirst, timeout.Token);
        await a.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);
        await a.Routing.AssignDealersAsync(CampaignId, timeout.Token);

        using var second = new TempDirectory();
        await using var assignFirst = TempDatabase.In(second.Path);
        var b = await SeedAsync(assignFirst, timeout.Token);
        await b.Routing.AssignDealersAsync(CampaignId, timeout.Token);
        await b.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        var left = await StateAsync(a, timeout.Token);
        var right = await StateAsync(b, timeout.Token);

        right.ShouldBe(
            left,
            "the two tools are independent, so the order cannot change the answer. The lead ids are "
            + "excluded from this comparison; everything that routing writes is in it.");

    }

    /// <summary>
    /// Every column routing writes, per lead, as comparable text. The lead ids are left out: they are
    /// allocated in insertion order and are the same in both databases anyway.
    /// </summary>
    private static async Task<List<string>> StateAsync(Fixture fixture, CancellationToken cancellationToken) =>
    [
        .. (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, cancellationToken))
            .OrderBy(lead => lead.OvertureId, StringComparer.Ordinal)
            .Select(lead =>
                $"{lead.OvertureId} {lead.Status} {lead.DealerId ?? "-"} {lead.BranchId ?? "-"} "
                + $"{lead.Assignment ?? "-"} {lead.SuppressionReason ?? "-"}"),
    ];

    [Fact]
    public async Task ApplySuppression_ASuppressedLeadIsNotRoutedToAnyDealer()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        await fixture.Routing.AssignDealersAsync(CampaignId, timeout.Token);
        await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);

        var suppressed = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Where(lead => lead.Status == LeadStatuses.Suppressed)
            .ToList();

        suppressed.Count.ShouldBe(Suppressed);
        suppressed.ShouldAllBe(
            lead => lead.DealerId == null,
            "a company on the suppression list is never mailed, so counting it under a dealer would "
            + "overstate that dealer's list in byDealer and in its packet. Assigning it first and "
            + "suppressing it afterwards has to leave no dealer behind.");
    }

    [Fact]
    public async Task Routing_IgnoresDuplicateLeads()
    {
        // A duplicate is kept for provenance (§7.2) and is not a prospect: routing it would double-count
        // its company under a dealer, and suppressing it would inflate the suppression counts.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token);

        await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);
        await fixture.Routing.AssignDealersAsync(CampaignId, timeout.Token);

        var duplicates = (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, timeout.Token))
            .Where(lead => lead.Status == LeadStatuses.Duplicate)
            .ToList();

        duplicates.Select(lead => lead.OvertureId).Order(StringComparer.Ordinal).ToList().ShouldBe(
            ["fx_0013", "fx_0014", "fx_0113"],
            "the three duplicate pairs from src/tests/Fixtures/places/README.md.");

        duplicates.ShouldAllBe(lead => lead.DealerId == null && lead.Assignment == null);
    }

    [Fact]
    public async Task Routing_WithNoListsImported_MakesEveryLeadAGapAndSuppressesNothing()
    {
        // The state every user is in before import_list runs. Reporting 83 coverage gaps is the honest
        // answer and tells the skill exactly what is missing; reporting 0 would read as "all routed".
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var fixture = await SeedAsync(database, timeout.Token, importLists: false);

        var suppression = await fixture.Routing.ApplySuppressionAsync(CampaignId, timeout.Token);
        var assignment = await fixture.Routing.AssignDealersAsync(CampaignId, timeout.Token);

        suppression.Suppressed.ShouldBe(0, Describe(suppression));
        suppression.ByReason.ShouldBeEmpty("no reasons, rather than four zeroes.");

        assignment.Gaps.ShouldBe(Leads, Describe(assignment));
        assignment.Assigned.ShouldBe(0, Describe(assignment));
        assignment.ByDealer.ShouldBeEmpty(Describe(assignment));
    }

    private static string Describe(AssignDealersResult result) =>
        $"assigned={result.Assigned} changed={result.Changed} gaps={result.Gaps} "
        + $"overrides={result.Overrides} byDealer=["
        + string.Join("; ", result.ByDealer.Select(row => $"{row.DealerId}={row.Leads}")) + "]";

    private static string Describe(ApplySuppressionResult result) =>
        $"suppressed={result.Suppressed} changed={result.Changed} byReason=["
        + string.Join("; ", result.ByReason.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => $"{row.Key}={row.Value}"))
        + "]";

    /// <summary>The campaign's lead id for one fixture place.</summary>
    private static async Task<string> LeadIdAsync(
        Fixture fixture,
        string overtureId,
        CancellationToken cancellationToken) =>
        (await fixture.Routing.ListRoutedLeadsAsync(CampaignId, cancellationToken))
            .Single(lead => lead.OvertureId == overtureId)
            .LeadId;

    private static async Task<string> StatusOfAsync(
        TempDatabase database,
        string leadId,
        CancellationToken cancellationToken) =>
        (await LeadRowAsync(database, leadId, cancellationToken)).Status;

    /// <summary>
    /// The <c>pre_suppression_status</c> column. Read straight off the row because
    /// <see cref="RoutedLead"/> does not carry it - it is bookkeeping for the release path rather than
    /// something a tool reports, and a test is the only thing that needs to see it.
    /// </summary>
    private static async Task<string?> PreSuppressionStatusAsync(
        TempDatabase database,
        string leadId,
        CancellationToken cancellationToken) =>
        (await LeadRowAsync(database, leadId, cancellationToken)).PreSuppressionStatus;

    /// <summary>Makes one suppressed lead look like a row written before the column existed.</summary>
    private static async Task ForgetPreSuppressionStatusAsync(
        TempDatabase database,
        string leadId,
        CancellationToken cancellationToken)
    {
        await using var context = await database.CreateContextAsync(cancellationToken);

        var lead = await context.Set<Lead>()
            .SingleAsync(row => row.CampaignId == CampaignId && row.Id == leadId, cancellationToken);

        lead.Status.ShouldBe(
            LeadStatuses.Suppressed,
            "this only makes sense for a suppressed lead; otherwise the test is not reproducing the "
            + "upgrade path it claims to.");

        lead.PreSuppressionStatus = null;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Lead> LeadRowAsync(
        TempDatabase database,
        string leadId,
        CancellationToken cancellationToken)
    {
        await using var context = await database.CreateContextAsync(cancellationToken);

        return await context.Set<Lead>()
            .AsNoTracking()
            .SingleAsync(row => row.CampaignId == CampaignId && row.Id == leadId, cancellationToken);
    }

    /// <summary>
    /// The committed suppression list with the <c>dnc</c> row taken out, in the test's own temp folder.
    /// Dropping that row is what releases <c>fx_0117</c>, which is the lead every restore test turns on.
    /// </summary>
    private static async Task<string> WithoutNorthfieldAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(RepoFixtures.SuppressionCsv, cancellationToken);
        var kept = lines
            .Where(line => !line.StartsWith("Northfield Storage Co,", StringComparison.Ordinal))
            .ToList();

        kept.Count.ShouldBe(
            lines.Length - 1,
            "exactly one row is dropped. If this is wrong, suppression.csv's first column changed and "
            + "the test is no longer removing what it thinks it is.");

        var path = Path.Combine(directory, "suppression-without-dnc.csv");
        await File.WriteAllLinesAsync(path, kept, cancellationToken);
        return path;
    }

    /// <summary>Puts one lead into a status only a person (or a later chunk) can set.</summary>
    private static async Task SetStatusAsync(
        TempDatabase database,
        string leadId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var context = await database.CreateContextAsync(cancellationToken);

        var lead = await context.Set<Lead>()
            .SingleAsync(row => row.CampaignId == CampaignId && row.Id == leadId, cancellationToken);

        lead.Status = status;
        lead.UpdatedAt = DateTimeOffset.UtcNow;

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Writes an override the way <c>update_leads</c> (C6) will, so §7.4 has one to preserve.</summary>
    private static async Task OverrideAsync(
        TempDatabase database,
        string leadId,
        string dealerId,
        string branchId,
        CancellationToken cancellationToken)
    {
        await using var context = await database.CreateContextAsync(cancellationToken);

        var lead = await context.Set<Lead>()
            .SingleAsync(row => row.CampaignId == CampaignId && row.Id == leadId, cancellationToken);

        lead.DealerId = dealerId;
        lead.BranchId = branchId;
        lead.Assignment = Assignments.Override;
        lead.UpdatedAt = DateTimeOffset.UtcNow;

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A campaign holding the leads the sample profile produces, with the three business lists
    /// imported. Everything goes through the production stores, so the row shapes are the real ones.
    /// </summary>
    private static async Task<Fixture> SeedAsync(
        TempDatabase database,
        CancellationToken cancellationToken,
        bool importLists = true)
    {
        await using var context = await database.MigrateAsync(cancellationToken);

        var campaigns = Resolve<ICampaignStore>(database, "C1");
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        await campaigns.AddAsync(
            new Campaign
            {
                Id = CampaignId,
                Name = "Houston C5",
                Slug = "houston c5",
                FolderPath = Path.Combine(Path.GetTempPath(), "houston-c5"),
                Status = CampaignStatuses.Draft,
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken);

        var candidates = Resolve<ICandidateStore>(database, "C4");
        await candidates.StoreAsync(CampaignId, SampleGroups(), cancellationToken);

        var importer = Resolve<IListImporter>(database, "C5");
        if (importLists)
        {
            await importer.ImportAsync(
                ImportListKinds.Dealers,
                RepoFixtures.DealersCsv,
                null,
                cancellationToken: cancellationToken);
            await importer.ImportAsync(
                ImportListKinds.Territories,
                RepoFixtures.TerritoriesCsv,
                null,
                cancellationToken: cancellationToken);
            await importer.ImportAsync(
                ImportListKinds.Suppression,
                RepoFixtures.SuppressionCsv,
                null,
                cancellationToken: cancellationToken);
        }

        return new Fixture(
            Resolve<ILeadRoutingStore>(database, "C5"),
            Resolve<IDealerStore>(database, "C5"),
            importer);
    }

    private static TService Resolve<TService>(TempDatabase database, string chunk)
        where TService : class =>
        database.Services.GetService<TService>()
        ?? throw new XunitException(
            $"AddProspectStudioStorage does not register a {typeof(TService).Name}; chunk {chunk} adds it.");

    /// <summary>
    /// The leads the sample profile produces, built with the production extraction and dedupe so the
    /// 83/7/76 arithmetic below is the same arithmetic <c>find_candidates</c> computes.
    /// </summary>
    private static IReadOnlyList<CandidateGroup> SampleGroups()
    {
        var profile = SampleProfile.Load();

        var sites = SamplePlaces.All
            .Where(place => profile.Selects(place) && !profile.IsExcluded(place))
            .Select(place => PlaceFields.ToSite(ToOverture(place), Release))
            .ToList();

        sites.Count.ShouldBe(86, "the rows the profile keeps; see src/tests/Fixtures/places/README.md.");

        var groups = CandidateDedupe.Group(sites);
        groups.Count.ShouldBe(Leads, "three of those collapse as duplicates.");

        return groups;
    }

    private const string Release = "2026-09-23.1";

    private static OverturePlace ToOverture(SamplePlace place) =>
        new(
            place.Id,
            place.Name,
            place.BasicCategory,
            new PlaceTaxonomy(place.TaxonomyPrimary, place.TaxonomyHierarchy, []),
            place.Confidence,
            place.Websites,
            place.Phones,
            [new PlaceAddress(place.Freeform, place.Locality, place.Postcode, place.Region, place.Country)],
            place.Lat,
            place.Lon,
            place.CountyFips);

    private sealed record Fixture(ILeadRoutingStore Routing, IDealerStore Dealers, IListImporter Importer);
}
