using System.Globalization;
using System.Net;
using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Market;
using ProspectStudio.Infrastructure.Census;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Census;

/// <summary>
/// The CBP client against recorded response bodies through an injected handler, so none of this touches
/// the network. What is asserted here is mostly the <em>request</em>: whether it is split per state,
/// whether it asks for the columns the bands are parsed from, whether a second identical call happens at
/// all, and what the client makes of a 302, a 404 and a 503.
/// </summary>
public class CensusCbpClientTests
{
    /// <summary>
    /// A stand-in key. It is not a Census key and never will be; the point is that the client sends one
    /// and that it reaches neither the cache nor an assertion message.
    /// </summary>
    private const string PlaceholderKey = "cbp-tests-placeholder-not-a-census-key";

    private static readonly string[] _houstonCounties = [.. CbpFixtures.HoustonCountyFips];

    // --------------------------------------------------- one request per state

    [Fact]
    public async Task Counties_in_two_states_are_fetched_with_one_request_per_state()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Harris();
        using var client = Client(handler, directory);

        // 48201 Harris and 22071 Orleans Parish. Asking for both in one request is HTTP 400 "wildcard
        // mismatch in geography hierarchy", which the handler answers with that recorded body. Both
        // states are answered with the recorded Texas body because no Louisiana response was recorded;
        // what is asserted here is the requests, not the numbers.
        await client.GetEstablishmentsAsync(["4931"], ["48201", "22071"], timeout.Token);

        handler.DataRequests.Count.ShouldBe(2, Describe(handler));
        foreach (var uri in handler.DataRequests)
        {
            RecordedCensusHandler.StatesIn(uri).Count.ShouldBe(
                1,
                "in=state: may name exactly one state; two is a 400 from the live API. " + Describe(handler));
        }

        handler.DataRequests
            .SelectMany(RecordedCensusHandler.StatesIn)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["22", "48"], Case.Sensitive, Describe(handler));
    }

    [Fact]
    public async Task Counties_in_one_state_are_fetched_with_a_single_request()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        using var client = Client(handler, directory);

        await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);

        handler.DataRequests.Count.ShouldBe(
            1,
            "ten counties in one state are one request, not ten. " + Describe(handler));
    }

    [Fact]
    public async Task The_query_asks_for_the_band_code_and_its_label()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        using var client = Client(handler, directory);

        await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);

        var query = Uri.UnescapeDataString(handler.DataRequests.Single().Query);

        foreach (var column in new[] { "ESTAB", "EMPSZES", "EMPSZES_LABEL" })
        {
            query.ShouldContain(
                column, Case.Sensitive,
                $"'{column}' has to be in get=: the band bounds come from EMPSZES_LABEL, because the 2023 "
                + $"metadata publishes no value list for EMPSZES. Query: {query}");
        }

        query.ShouldContain("NAICS", Case.Sensitive, $"the vintage's NAICS variable filters the query: {query}");
        query.ShouldContain("4931", Case.Sensitive, query);
        query.ShouldContain("201", Case.Sensitive, $"the county list has to reach the query: {query}");
    }

    [Fact]
    public async Task Every_band_including_all_establishments_comes_back()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        using var client = Client(handler, directory);

        var rows = await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);

        rows.Count.ShouldBe(35);
        rows.ShouldContain(
            row => row.SizeBandCode == CbpSizeBands.AllEstablishmentsCode,
            "the 001 rows are what makes the gap between the total and the band sum visible. Filtering "
            + "EMPSZES down to the bands above the threshold would hide every suppressed row.");
        rows.Select(row => row.SizeBandCode).Distinct().Count().ShouldBe(11);
    }

    // ------------------------------------------------------------- year probe

    [Fact]
    public async Task The_year_is_probed_downward_over_the_metadata_endpoint()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        using var client = Client(handler, directory);

        var year = await client.GetYearAsync(timeout.Token);

        year.ShouldBe(
            CbpFixtures.LatestCbpYear,
            "2024 and 2025 are 404; 2023 is the newest published CBP. " + Describe(handler));

        handler.ProbeRequests.ShouldNotBeEmpty(
            "the probe goes to <year>/cbp/variables.json. A data query cannot probe: with a key a missing "
            + "year is a 404, but without one every year is a 302, so the two are indistinguishable.");
        handler.DataRequests.ShouldBeEmpty("probing is metadata only. " + Describe(handler));

        var probed = handler.ProbeRequests.Select(RecordedCensusHandler.YearIn).ToList();
        probed.ShouldBeInOrder(SortDirection.Descending, "the probe walks downward. " + Describe(handler));
        probed.ShouldContain(CbpFixtures.LatestCbpYear + 1, "the first 404 is what proves it probed. " + Describe(handler));
        probed.Min().ShouldBe(CbpFixtures.LatestCbpYear, "it stops at the first year that answers. " + Describe(handler));
    }

    [Fact]
    public async Task A_configured_cbp_year_is_used_as_given()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        handler.PublishedYears.Add(2021);
        using var client = Client(handler, directory, cbpYear: 2021);

        (await client.GetYearAsync(timeout.Token)).ShouldBe(
            2021,
            "PS_CBP_YEAR pins the vintage, which is how a user works around a bad release. " + Describe(handler));
    }

    [Fact]
    public async Task No_published_year_is_reported_as_a_missing_year_not_a_key_problem()
    {
        // Generous, because the probe has to give up on its own rather than be cancelled.
        using var timeout = Deadline(TimeSpan.FromSeconds(10));
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        handler.PublishedYears.Clear();
        using var client = Client(handler, directory);

        var error = await Should.ThrowAsync<MarketNotReadyException>(
            () => client.GetYearAsync(timeout.Token));

        error.Message.ShouldContain("year", Case.Insensitive, error.Message);
        error.Message.ShouldNotContain(
            PsOptionsFactory.CensusKeyVariable, Case.Sensitive,
            "a 404 on a probed year is a missing year. Blaming the key sends the user hunting for a "
            + $"problem they do not have: {error.Message}");
    }

    // ---------------------------------------------------------- key problems

    [Fact]
    public async Task Without_a_census_key_nothing_is_requested_and_the_variable_is_named()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        using var client = Client(handler, directory, key: null);

        var error = await Should.ThrowAsync<MarketNotReadyException>(
            () => client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token));

        error.Message.ShouldContain(
            PsOptionsFactory.CensusKeyVariable, Case.Sensitive,
            $"the user has to be told which variable to set: {error.Message}");
        error.Hint.ShouldNotBeNullOrWhiteSpace("NOT_READY always carries a hint (mcp-tools.md §Errors).");
        handler.DataRequests.ShouldBeEmpty(
            "a keyless data query can only 302, so there is nothing to gain by sending it. "
            + Describe(handler));
    }

    [Theory]
    [InlineData("missing_key.html")]
    [InlineData("invalid_key.html")]
    public async Task A_redirect_is_read_as_a_key_problem_rather_than_parsed_as_json(string page)
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        handler.RedirectTo = page;
        using var client = Client(handler, directory);

        // MarketNotReadyException and nothing else: a JsonException here would mean the redirect was
        // followed to its 200 HTML page, which is the failure mode AllowAutoRedirect = false exists for.
        var error = await Should.ThrowAsync<MarketNotReadyException>(
            () => client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token));

        error.Message.ShouldContain(
            PsOptionsFactory.CensusKeyVariable, Case.Sensitive,
            $"a 302 from the data API always means a key problem ({page}): {error.Message}");
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    // -------------------------------------------------------------- failures

    /// <remarks>
    /// The one slow test here: it waits out whatever backoff the client uses. Keep that backoff short, or
    /// drive it from the injected <see cref="TimeProvider"/>, so this stays under a second.
    /// </remarks>
    [Fact]
    public async Task A_server_error_is_retried_and_then_reported_as_an_external_failure()
    {
        using var timeout = Deadline(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        handler.DataStatus = HttpStatusCode.ServiceUnavailable;
        using var client = Client(handler, directory);

        var error = await Should.ThrowAsync<MarketExternalException>(
            () => client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token));

        error.Message.ShouldContain("503", Case.Sensitive, $"say what the API answered: {error.Message}");
        handler.DataRequests.Count.ShouldBeGreaterThan(
            1,
            "EXTERNAL_API is 'Census/S3/website failures after retries', so a 503 is retried at least "
            + "once before giving up. " + Describe(handler));
    }

    [Fact]
    public async Task A_cross_state_request_would_fail_loudly()
    {
        // Guards the guard: if the client ever did put two states in one URL, the handler answers with
        // the recorded 400 body and this is the error the test above would surface.
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Harris();
        handler.DataStatus = HttpStatusCode.BadRequest;
        using var client = Client(handler, directory);

        var error = await Should.ThrowAsync<MarketExternalException>(
            () => client.GetEstablishmentsAsync(["4931"], ["48201"], timeout.Token));

        error.Message.ShouldContain("400", Case.Sensitive, error.Message);
        CbpFixtures.CrossStateError.ShouldContain(
            "wildcard mismatch in geography hierarchy", Case.Sensitive,
            "the recorded 400 body, so the fixture still says what the API said.");
    }

    [Fact]
    public async Task A_204_with_no_body_is_not_reported_as_a_json_failure()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        handler.DataStatus = HttpStatusCode.NoContent;
        using var client = Client(handler, directory);

        // A valid query that matches no rows answers 204 with an empty body. Handing "" to
        // JsonDocument.Parse turns "there is nothing here" into EXTERNAL_API "not JSON", which sends the
        // user chasing an outage that is not happening. 204 is an answer, not a failure.
        var rows = await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);

        rows.ShouldBeEmpty("204 means the query was fine and matched nothing.");
    }

    [Fact]
    public async Task Nested_detail_bands_survive_the_round_trip_so_the_service_can_discard_them()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = new RecordedCensusHandler { Bodies = { ["00"] = CbpFixtures.Harris00 } };
        using var client = Client(handler, directory);

        var rows = await client.GetEstablishmentsAsync(["00"], [CbpFixtures.HarrisCountyFips], timeout.Token);

        // The client hands over every published row; deciding which bands nest is the service's job, and
        // it cannot do it with rows the client has already filtered out.
        rows.Select(row => row.SizeBandCode).Order(StringComparer.Ordinal).ShouldBe(
            ["001", "210", "220", "230", "241", "242", "251", "252", "254", "260", "262", "263", "271", "273"],
            Case.Sensitive);

        rows.Single(row => row.SizeBandCode == "273").SizeBandLabel.ShouldBe(
            "Establishments with 5,000 employees or more",
            "the label is the only source of a band's bounds, so an open-ended one has to arrive intact.");
    }

    // ------------------------------------------- the request cap and budget

    [Fact]
    public async Task More_queries_than_one_call_allows_is_refused_before_anything_is_sent()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Harris();
        using var client = Client(handler, directory);

        // One request per NAICS-and-state pair, run one after another. Counted from the constant rather
        // than a literal so lowering the cap does not quietly stop testing it.
        var codes = Enumerable.Range(0, CensusCbpClient.MaxRequestsPerCall + 1)
            .Select(index => (100_000 + index).ToString(CultureInfo.InvariantCulture))
            .ToArray();

        var error = await Should.ThrowAsync<MarketRequestException>(
            () => client.GetEstablishmentsAsync(codes, [CbpFixtures.HarrisCountyFips], timeout.Token));

        error.Message.ShouldContain(
            CensusCbpClient.MaxRequestsPerCall.ToString(CultureInfo.InvariantCulture),
            Case.Sensitive,
            $"the message has to say what the limit is: {error.Message}");

        handler.DataRequests.ShouldBeEmpty(
            "refusing beats truncating: a market sized from some of the codes is a wrong total presented "
            + "as a right one, and no caller would know. " + Describe(handler));

        // The cap needs its own hint. The generic "pass NAICS codes of 2 to 6 digits and a geography that
        // covers at least one county" is actively misleading here: both are already fine, and the caller
        // would go looking for a fault that is not there.
        error.Hint.ShouldNotBeNullOrWhiteSpace(
            "VALIDATION_FAILED carries a hint, and this one has to name the real remedy - fewer codes, or "
            + "a smaller area.");
        error.Hint!.ShouldNotContain(
            "2 to 6 digits",
            Case.Insensitive,
            $"that is the generic hint for a malformed code, not for too many queries: {error.Hint}");
    }

    [Fact]
    public async Task Exactly_as_many_queries_as_the_cap_allows_is_accepted()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();

        // Guards the boundary: a cap of N that refuses N is as broken as one that allows N+1.
        var handler = RecordedCensusHandler.Harris();
        handler.Fallback = CbpFixtures.Harris4931;
        using var client = Client(handler, directory);

        var codes = Enumerable.Range(0, CensusCbpClient.MaxRequestsPerCall)
            .Select(index => (100_000 + index).ToString(CultureInfo.InvariantCulture))
            .ToArray();

        await client.GetEstablishmentsAsync(codes, [CbpFixtures.HarrisCountyFips], timeout.Token);

        handler.DataRequests.Count.ShouldBe(CensusCbpClient.MaxRequestsPerCall, Describe(handler));
    }

    [Fact]
    public async Task The_cap_counts_pairs_so_two_states_halve_the_codes_allowed()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Harris();
        using var client = Client(handler, directory);

        // Two states doubles the round trips per code, so the cap bites at half the codes.
        var codes = Enumerable.Range(0, (CensusCbpClient.MaxRequestsPerCall / 2) + 1)
            .Select(index => (100_000 + index).ToString(CultureInfo.InvariantCulture))
            .ToArray();

        await Should.ThrowAsync<MarketRequestException>(
            () => client.GetEstablishmentsAsync(codes, [CbpFixtures.HarrisCountyFips, "22071"], timeout.Token));

        handler.DataRequests.ShouldBeEmpty(Describe(handler));
    }

    [Fact]
    public async Task A_failure_part_way_through_returns_no_rows_at_all()
    {
        using var timeout = Deadline(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();

        // Texas answers, Louisiana does not. The call has real rows in hand when it fails, and they must
        // not reach the caller: a partial market presented as a total is the failure mode this whole chunk
        // is about.
        var handler = RecordedCensusHandler.Harris();
        handler.FailFrom = (2, HttpStatusCode.ServiceUnavailable);
        using var client = Client(handler, directory);

        await Should.ThrowAsync<MarketExternalException>(
            () => client.GetEstablishmentsAsync(["4931"], [CbpFixtures.HarrisCountyFips, "22071"], timeout.Token));

        handler.DataRequests.Count.ShouldBeGreaterThan(1, "the first state answered. " + Describe(handler));
    }

    [Fact]
    public async Task A_census_that_stops_answering_runs_out_of_budget_and_yields_nothing()
    {
        // The clock is steppable so the budget can be reached without the test sitting through it. If the
        // budget is still wired to the real clock this takes CallBudget in wall time instead of none,
        // which is the cost of leaving it unmockable rather than a failure.
        var clock = new SteppableTimeProvider();
        using var timeout = Deadline(CensusCbpClient.CallBudget + TimeSpan.FromSeconds(10));
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Harris();
        handler.HangForever = true;
        using var client = Client(handler, directory, clock: clock);

        var call = client.GetEstablishmentsAsync(["4931"], [CbpFixtures.HarrisCountyFips], timeout.Token);

        // Step past the budget, repeatedly, so a clock-driven deadline fires.
        for (var step = 0; step < 5 && !call.IsCompleted; step++)
        {
            clock.Advance(CensusCbpClient.CallBudget);
            await Task.Yield();
        }

        var error = await Should.ThrowAsync<MarketExternalException>(() => call);

        error.Message.ShouldContain(
            $"within {CensusCbpClient.CallBudget.TotalSeconds:0} seconds",
            Case.Insensitive,
            "say what the deadline was, so the user can tell a slow Census from a broken query: "
            + error.Message);
    }

    [Fact]
    public async Task A_call_at_the_request_cap_still_fits_inside_the_budget_on_production_spacing()
    {
        using var directory = new TempDirectory();

        // The arithmetic the cap exists to satisfy, on a cold process: the year probe plus one request per
        // NAICS code, each held a second behind the last, has to finish inside the call budget. Both the
        // spacing and the budget run on the injected clock, so this is measured rather than reasoned
        // about - which is how the 15 seconds of enforced delay went unnoticed in the first place.
        var clock = new SteppableTimeProvider();
        var handler = RecordedCensusHandler.Harris();
        handler.Clock = clock;
        handler.Fallback = CbpFixtures.Harris4931;
        using var client = Client(
            handler,
            directory,
            clock: clock,
            requestGap: CensusCbpClient.DefaultRequestGap);

        var codes = Enumerable.Range(0, CensusCbpClient.MaxRequestsPerCall)
            .Select(index => (100_000 + index).ToString(CultureInfo.InvariantCulture))
            .ToArray();

        var started = clock.GetUtcNow();
        var call = client.GetEstablishmentsAsync(codes, [CbpFixtures.HarrisCountyFips], CancellationToken.None);

        using var realDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!call.IsCompleted && !realDeadline.IsCancellationRequested)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1, realDeadline.Token);
        }

        var rows = await call;
        rows.ShouldNotBeEmpty();

        var spent = clock.GetUtcNow() - started;

        // Without this the "under budget" assertion would also pass if the spacing never engaged at all,
        // which is the state this whole test was added to escape.
        spent.ShouldBeGreaterThanOrEqualTo(
            CensusCbpClient.DefaultRequestGap * (CensusCbpClient.MaxRequestsPerCall - 1),
            "the requests really were spaced out, so the margin below is measuring something.");

        spent.ShouldBeLessThan(
            CensusCbpClient.CallBudget,
            $"{CensusCbpClient.MaxRequestsPerCall} capped requests plus the year probe spend {spent} at "
            + $"{CensusCbpClient.DefaultRequestGap.TotalSeconds:0} s apart, against a "
            + $"{CensusCbpClient.CallBudget.TotalSeconds:0} s budget. Raising the cap without raising the "
            + "budget turns a legitimate query into an EXTERNAL_API timeout.");

        handler.DataRequests.Count.ShouldBe(CensusCbpClient.MaxRequestsPerCall, Describe(handler));
    }

    // --------------------------------------------------------- the throttle

    [Fact]
    public async Task Requests_are_held_a_second_apart_on_the_production_spacing()
    {
        using var directory = new TempDirectory();

        // The spacing is CLAUDE.md's 1 request/second per domain. Until requestGap became a parameter it
        // was switched off by the very act of injecting a handler, so the production transport path could
        // not be exercised at all: the 11 seconds of enforced delay a 12-request call incurs never showed
        // up in a test run. Passing DefaultRequestGap explicitly puts this test on that path.
        var clock = new SteppableTimeProvider();
        var handler = RecordedCensusHandler.Harris();
        handler.Clock = clock;
        handler.Fallback = CbpFixtures.Harris4931;
        using var client = Client(
            handler,
            directory,
            cbpYear: CbpFixtures.LatestCbpYear,
            clock: clock,
            requestGap: CensusCbpClient.DefaultRequestGap);

        var call = client.GetEstablishmentsAsync(
            ["4931", "238210", "49311"],
            [CbpFixtures.HarrisCountyFips],
            CancellationToken.None);

        // The waits are Task.Delay(wait, clock, ...), so the clock is what lets them finish. Stepping it
        // forward in small increments keeps the spacing observable instead of collapsing it.
        using var realDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!call.IsCompleted && !realDeadline.IsCancellationRequested)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1, realDeadline.Token);
        }

        await call;

        var times = handler.DataRequestTimes;
        times.Count.ShouldBe(3, Describe(handler));

        for (var index = 1; index < times.Count; index++)
        {
            (times[index] - times[index - 1]).ShouldBeGreaterThanOrEqualTo(
                CensusCbpClient.DefaultRequestGap,
                $"request {index + 1} came {times[index] - times[index - 1]} after request {index}, closer "
                + "than the one-per-second the configured User-Agent promises the host.");
        }
    }

    [Fact]
    public async Task A_recorded_handler_is_not_throttled_by_default()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();

        // The flip side: fixtures reach no host, so there is nobody to be polite to and the suite should
        // not pay 1 second per request. A manual clock nothing advances would otherwise hang here.
        var handler = RecordedCensusHandler.Harris();
        handler.Fallback = CbpFixtures.Harris4931;
        using var client = Client(handler, directory);

        await client.GetEstablishmentsAsync(["4931", "238210"], [CbpFixtures.HarrisCountyFips], timeout.Token);

        handler.DataRequests.Count.ShouldBe(2, Describe(handler));
    }

    // ----------------------------------------------------------- rate limits

    [Fact]
    public async Task A_429_is_reported_as_a_rate_limit_rather_than_a_generic_failure()
    {
        using var timeout = Deadline(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Harris();
        handler.DataStatus = HttpStatusCode.TooManyRequests;
        using var client = Client(handler, directory);

        // §Errors has a RATE_LIMITED code, so being rate limited must not arrive as EXTERNAL_API: the
        // remedy is to wait, not to investigate an outage.
        var error = await Should.ThrowAsync<MarketRateLimitedException>(
            () => client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token));

        error.Message.ShouldNotBeNullOrWhiteSpace();
        handler.DataRequests.Count.ShouldBeGreaterThan(
            1,
            "a 429 is transient, so it is retried before being reported. " + Describe(handler));
    }

    // --------------------------------------------------- the vintage cache

    [Fact]
    public async Task A_fresh_client_reads_the_cached_vintage_without_probing_again()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        var clock = new ManualTimeProvider();

        using (var first = Client(handler, directory, clock: clock))
        {
            (await first.GetYearAsync(timeout.Token)).ShouldBe(CbpFixtures.LatestCbpYear);
        }

        var afterDiscovery = handler.ProbeRequests.Count;
        afterDiscovery.ShouldBeGreaterThan(1, "discovery walks the years down. " + Describe(handler));

        using (var second = Client(handler, directory, clock: clock))
        {
            (await second.GetYearAsync(timeout.Token)).ShouldBe(CbpFixtures.LatestCbpYear);
        }

        handler.ProbeRequests.Count.ShouldBe(
            afterDiscovery,
            "the point of caching the vintage is that a restarted server sends no metadata request at all. "
            + "Getting the right year by probing again would pass a year assertion while costing four "
            + "round trips out of every call's budget. " + Describe(handler));
    }

    [Fact]
    public async Task The_cached_vintage_expires_after_thirty_days()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        var clock = new ManualTimeProvider();

        using (var first = Client(handler, directory, clock: clock))
        {
            await first.GetYearAsync(timeout.Token);
        }

        var afterDiscovery = handler.ProbeRequests.Count;
        clock.Advance(CensusCbpClient.CacheLifetime + TimeSpan.FromDays(1));

        using (var later = Client(handler, directory, clock: clock))
        {
            (await later.GetYearAsync(timeout.Token)).ShouldBe(CbpFixtures.LatestCbpYear);
        }

        handler.ProbeRequests.Count.ShouldBeGreaterThan(
            afterDiscovery,
            $"a vintage pinned for longer than {CensusCbpClient.CacheLifetime.TotalDays:0} days would miss a "
            + "new CBP release for a month, and nothing would say so. " + Describe(handler));
    }

    [Fact]
    public async Task Pinning_or_unpinning_the_year_re_probes_rather_than_reading_the_other_entry()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        handler.PublishedYears.Add(CbpFixtures.LatestCbpYear - 1);
        var clock = new ManualTimeProvider();

        // Discovery and a pinned year keep separate entries, so neither can serve the other: an entry
        // written while PS_CBP_YEAR said 2022 must never answer a later unpinned call, or the server would
        // size every market against a year nobody asked for - for thirty days, silently.
        using (var discovered = Client(handler, directory, clock: clock))
        {
            (await discovered.GetYearAsync(timeout.Token)).ShouldBe(CbpFixtures.LatestCbpYear);
        }

        var afterDiscovery = handler.ProbeRequests.Count;

        using (var pinned = Client(handler, directory, cbpYear: CbpFixtures.LatestCbpYear, clock: clock))
        {
            (await pinned.GetYearAsync(timeout.Token)).ShouldBe(CbpFixtures.LatestCbpYear);
        }

        var afterPinning = handler.ProbeRequests.Count;
        afterPinning.ShouldBeGreaterThan(
            afterDiscovery,
            "a pinned year cannot read the discovered entry, even when it names the same year. "
            + Describe(handler));

        using (var repinned = Client(handler, directory, cbpYear: CbpFixtures.LatestCbpYear - 1, clock: clock))
        {
            (await repinned.GetYearAsync(timeout.Token)).ShouldBe(CbpFixtures.LatestCbpYear - 1);
        }

        var afterRepinning = handler.ProbeRequests.Count;
        afterRepinning.ShouldBeGreaterThan(
            afterPinning,
            "re-pinning to a different year lands on a third entry. " + Describe(handler));

        // And back to discovery, which still has its own entry and must not pick up either pinned one.
        using (var again = Client(handler, directory, clock: clock))
        {
            (await again.GetYearAsync(timeout.Token)).ShouldBe(
                CbpFixtures.LatestCbpYear,
                "the discovered entry survived the pinned calls untouched. " + Describe(handler));
        }

        handler.ProbeRequests.Count.ShouldBe(
            afterRepinning,
            "that last call came from the entry the first one wrote. " + Describe(handler));

        VintageFiles(directory).Count.ShouldBe(
            3,
            "one entry per distinct vintage request: discovered, pinned, re-pinned. Found: "
            + string.Join(", ", VintageFiles(directory)));
    }

    [Fact]
    public async Task A_probe_that_finds_no_published_year_caches_nothing()
    {
        using var timeout = Deadline(TimeSpan.FromSeconds(10));
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        handler.PublishedYears.Clear();
        var clock = new ManualTimeProvider();

        using (var client = Client(handler, directory, clock: clock))
        {
            await Should.ThrowAsync<MarketNotReadyException>(() => client.GetYearAsync(timeout.Token));
        }

        VintageFiles(directory).ShouldBeEmpty(
            "caching the failure would turn a transient census.gov outage into thirty days of 'no CBP year "
            + "published', which no amount of retrying would clear. Found: "
            + string.Join(", ", VintageFiles(directory)));

        // And the next attempt, once the API is answering, gets a real answer rather than the stored one.
        handler.PublishedYears.Add(CbpFixtures.LatestCbpYear);

        using (var retry = Client(handler, directory, clock: clock))
        {
            (await retry.GetYearAsync(timeout.Token)).ShouldBe(CbpFixtures.LatestCbpYear);
        }
    }

    // ----------------------------------------------------------- disk cache

    [Fact]
    public async Task An_identical_second_call_is_served_from_the_cache()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        using var client = Client(handler, directory);

        var first = await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);
        var second = await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);

        handler.DataRequests.Count.ShouldBe(1, "the second call is the same query. " + Describe(handler));
        second.Count.ShouldBe(first.Count);
        second.ShouldBe(first, "a cached answer is the same answer.");
    }

    [Fact]
    public async Task The_cache_is_on_disk_so_a_new_client_reuses_it()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        var clock = new ManualTimeProvider();

        using (var first = Client(handler, directory, clock: clock))
        {
            await first.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);
        }

        using (var second = Client(handler, directory, clock: clock))
        {
            await second.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);
        }

        handler.DataRequests.Count.ShouldBe(
            1,
            "the cache lives under the data folder, not in the process, so restarting the server does not "
            + "re-ask Census. " + Describe(handler));
    }

    [Fact]
    public async Task An_entry_older_than_thirty_days_is_fetched_again()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        var clock = new ManualTimeProvider();
        using var client = Client(handler, directory, clock: clock);

        await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);
        clock.Advance(CensusCbpClient.CacheLifetime + TimeSpan.FromDays(1));
        await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);

        handler.DataRequests.Count.ShouldBe(
            2,
            $"the cache is good for {CensusCbpClient.CacheLifetime.TotalDays:0} days, and its age comes "
            + "from the injected TimeProvider rather than a file timestamp, so a test can step past it. "
            + Describe(handler));
    }

    [Fact]
    public async Task An_entry_inside_thirty_days_is_still_used()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        var clock = new ManualTimeProvider();
        using var client = Client(handler, directory, clock: clock);

        await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);
        clock.Advance(CensusCbpClient.CacheLifetime - TimeSpan.FromDays(1));
        await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);

        handler.DataRequests.Count.ShouldBe(1, Describe(handler));
    }

    [Fact]
    public async Task A_different_query_is_cached_separately()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        using var client = Client(handler, directory);

        await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);
        await client.GetEstablishmentsAsync(["238210"], _houstonCounties, timeout.Token);
        await client.GetEstablishmentsAsync(["4931"], ["48201"], timeout.Token);

        handler.DataRequests.Count.ShouldBe(
            3,
            "the cache key covers the year, the NAICS codes and the counties; collapsing any of them "
            + "would answer one question with another question's numbers. " + Describe(handler));
    }

    [Fact]
    public async Task The_cache_never_holds_the_api_key()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var handler = RecordedCensusHandler.Houston();
        using var client = Client(handler, directory);

        await client.GetEstablishmentsAsync(["4931"], _houstonCounties, timeout.Token);

        var files = Directory.Exists(directory.Path)
            ? Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories)
            : [];

        files.ShouldNotBeEmpty("the response is cached on disk, so there should be something to inspect.");

        foreach (var file in files)
        {
            var name = Path.GetRelativePath(directory.Path, file);
            name.ShouldNotContain(PlaceholderKey, Case.Sensitive, "a key must not end up in a file name.");
            File.ReadAllText(file).ShouldNotContain(
                PlaceholderKey, Case.Sensitive,
                $"'{name}' holds the key. Cache a response body and a cache key that is a hash, never the "
                + "request URL (CLAUDE.md: no secrets in the repo, and the cache folder is the user's disk).");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static CensusCbpClient Client(
        HttpMessageHandler handler,
        TempDirectory directory,
        string? key = PlaceholderKey,
        int? cbpYear = null,
        TimeProvider? clock = null,
        TimeSpan? requestGap = null) =>
        new(Options(directory.Path, key, cbpYear), clock ?? new ManualTimeProvider(), handler, requestGap);

    private static PsOptions Options(string data, string? key, int? cbpYear) =>
        PsOptionsFactory.Create(new Dictionary<string, string?>
        {
            [PsOptionsFactory.HomeVariable] = Path.Combine(data, "home"),
            [PsOptionsFactory.DataVariable] = data,
            [PsOptionsFactory.CensusKeyVariable] = key,
            [PsOptionsFactory.CbpYearVariable] = cbpYear?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

    private static string Describe(RecordedCensusHandler handler) =>
        $"Requests ({handler.RequestCount}): "
        + string.Join(
            Environment.NewLine,
            handler.Requests.Select(uri => "  " + Redact(Uri.UnescapeDataString(uri.PathAndQuery))));

    /// <summary>The vintage entries on disk, by name, so a test can say which ones exist.</summary>
    private static List<string> VintageFiles(TempDirectory directory)
    {
        var census = Path.Combine(directory.Path, "cache", "census");

        return Directory.Exists(census)
            ? [.. Directory.GetFiles(census, "cbp-vintage-*.json").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>Keeps the key out of a failure message, which ends up in CI logs.</summary>
    private static string Redact(string uri) => uri.Replace(PlaceholderKey, "<key>", StringComparison.Ordinal);

    /// <summary>
    /// A guard against a hung call, not a performance assertion - so it is generous. Two seconds was not:
    /// it is wall clock from here, and several of these tests spend it on three sequential calls, ten
    /// requests at the cap, or four cold clients each probing for the vintage, plus the cache files each of
    /// those writes. On an idle dev machine the heaviest took 71 ms; under CPU contention it took 433 ms,
    /// and a loaded two-core CI runner went past 2 s and failed inside a cache write
    /// (<c>A_different_query_is_cached_separately</c>, Release). Raising the default fixes the family
    /// rather than the one test that surfaced first; the slow paths keep their own explicit deadlines.
    /// </summary>
    private static CancellationTokenSource Deadline(TimeSpan? within = null) =>
        new(within ?? TimeSpan.FromSeconds(10));
}
