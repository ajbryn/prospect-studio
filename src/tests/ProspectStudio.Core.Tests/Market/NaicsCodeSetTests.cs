using ProspectStudio.Core.Market;
using Shouldly;

namespace ProspectStudio.Core.Tests.Market;

/// <summary>
/// The CBP NAICS table is hierarchical, so a parent row already contains its children. Verified in
/// Harris County: <c>4931</c> is 360 establishments and <c>49311</c> = <c>493110</c> is 256 of those, so
/// asking for both and adding them reports 616 for a market of 360.
/// </summary>
public class NaicsCodeSetTests
{
    [Theory]
    [InlineData("a parent and its child", new[] { "4931", "49311" }, new[] { "4931" })]
    [InlineData("the child listed first", new[] { "49311", "4931" }, new[] { "4931" })]
    [InlineData("a parent and a grandchild", new[] { "4931", "493110" }, new[] { "4931" })]
    [InlineData("a whole chain", new[] { "4931", "49311", "493110" }, new[] { "4931" })]
    [InlineData("the 5- and 6-digit forms of one industry", new[] { "23821", "238210" }, new[] { "23821" })]
    [InlineData("different branches", new[] { "4931", "238210" }, new[] { "4931", "238210" })]
    [InlineData("the same code twice", new[] { "4931", "4931" }, new[] { "4931" })]
    [InlineData("siblings that only share a prefix", new[] { "23821", "23822" }, new[] { "23821", "23822" })]
    [InlineData("a subsector and an industry inside it", new[] { "493", "4931" }, new[] { "493" })]
    public void Only_the_outermost_codes_are_kept(string because, string[] requested, string[] expected)
    {
        NaicsCodeSet.DropDescendants(requested).ShouldBe(expected, ignoreOrder: true, because);
    }

    [Fact]
    public void A_single_code_is_left_alone()
    {
        NaicsCodeSet.DropDescendants(["238210"]).ShouldBe(["238210"]);
    }

    [Fact]
    public void An_empty_list_stays_empty()
    {
        NaicsCodeSet.DropDescendants([]).ShouldBeEmpty();
    }
}
