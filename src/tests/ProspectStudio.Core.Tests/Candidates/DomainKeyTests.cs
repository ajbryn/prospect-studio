using ProspectStudio.Core.Candidates;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Candidates;

/// <summary>
/// The first dedupe key of technical-design §7.2: the registrable domain of a website, with the
/// generic hosts ignored. The website forms here are the ones the real Texas extract actually holds -
/// bare hosts, trailing slashes, plain <c>http://</c> and a great many rows with nothing at all.
/// </summary>
public class DomainKeyTests
{
    [Theory]
    [InlineData("https://www.bayoufulfillment.example", "bayoufulfillment.example")]
    [InlineData("https://bayoufulfillment.example", "bayoufulfillment.example")]
    [InlineData("http://bayoufulfillment.example/about-us", "bayoufulfillment.example")]
    [InlineData("https://reladyne.example/", "reladyne.example")]
    // Real Overture rows carry bare hosts with no scheme at all ("chartersupply.com").
    [InlineData("chartersupply.example", "chartersupply.example")]
    [InlineData("www.chartersupply.example", "chartersupply.example")]
    // Case and whitespace cannot change an identity.
    [InlineData("HTTPS://WWW.BayouFulfillment.Example", "bayoufulfillment.example")]
    [InlineData("  https://www.bayoufulfillment.example  ", "bayoufulfillment.example")]
    // A port and a query string are not part of the identity.
    [InlineData("https://bayoufulfillment.example:8443/x?y=1", "bayoufulfillment.example")]
    public void For_ReturnsTheRegistrableDomain(string website, string expected) =>
        DomainKey.For(website).ShouldBe(expected);

    [Theory]
    [InlineData("https://www.facebook.com/addicksbarkerdist")]
    [InlineData("https://facebook.com/clodinemachineworks")]
    [InlineData("https://www.instagram.com/someshop")]
    [InlineData("https://www.linkedin.com/company/someshop")]
    [InlineData("https://www.yelp.com/biz/someshop")]
    [InlineData("https://sites.google.com/view/someshop")]
    [InlineData("https://someshop.business.site")]
    [InlineData("https://someshop.wixsite.com/home")]
    [InlineData("https://someshop.godaddysites.com")]
    [InlineData("https://someshop.squarespace.com")]
    public void For_IgnoresGenericHosts(string website) =>
        DomainKey.For(website).ShouldBeNull(
            "two unrelated companies that both list a Facebook page or a Wix site are not the same "
            + "company (technical-design §7.2 rule 1), so a generic host must never form a key.");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    [InlineData("mailto:sales@bayoufulfillment.example")]
    public void For_NoUsableWebsite_IsNull(string? website) =>
        DomainKey.For(website).ShouldBeNull(
            "a great many real rows have no website; that must mean 'no key', not an exception.");

    [Fact]
    public void Generic_hosts_are_exactly_the_ten_in_section_72() =>
        DomainKey.GenericHosts.Order(StringComparer.Ordinal).ToList().ShouldBe(
            [
                "business.site", "facebook.com", "godaddysites.com", "google.com", "instagram.com",
                "linkedin.com", "sites.google.com", "squarespace.com", "wixsite.com", "yelp.com",
            ],
            "technical-design §7.2 lists these ten; adding one silently stops merging real duplicates, "
            + "and removing one starts merging unrelated companies.");

    [Fact]
    public void The_fixtures_only_share_one_real_domain_and_one_generic_host()
    {
        var keyed = SamplePlaces.All
            .Select(place => (place.Id, Key: DomainKey.For(place.Websites.FirstOrDefault())))
            .Where(row => row.Key is not null)
            .GroupBy(row => row.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();

        keyed.Count.ShouldBe(
            1,
            "exactly one domain-key duplicate pair exists in the fixture (fx_0001 and fx_0013 on "
            + "bayoufulfillment.example). The facebook.com pair fx_0104/fx_0105 must not appear here. "
            + $"Got: {string.Join("; ", keyed.Select(group => $"{group.Key} -> {string.Join(",", group.Select(row => row.Id))}"))}");

        keyed[0].Select(row => row.Id).Order(StringComparer.Ordinal).ToList().ShouldBe(["fx_0001", "fx_0013"]);
    }
}
