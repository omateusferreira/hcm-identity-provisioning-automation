using FluentAssertions;
using HcmIdentityProvisioning.Domain.Common;
using Xunit;

namespace HcmIdentityProvisioning.Domain.Tests.Common;

public class PagedResultTests
{
    [Fact]
    public void Constructor_ShouldSetPropertiesCorrectly()
    {
        var items = new List<string> { "item1", "item2" };
        var page = new PagedResult<string>(
            Items: items,
            PageNumber: 1,
            PageSize: 10,
            TotalCount: 2,
            HasNextPage: false
        );

        page.Items.Should().BeEquivalentTo(items);
        page.PageNumber.Should().Be(1);
        page.PageSize.Should().Be(10);
        page.TotalCount.Should().Be(2);
        page.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public void RecordEquality_ShouldWorkAsExpected()
    {
        var items = new[] { 1, 2, 3 };
        var page1 = new PagedResult<int>(items, 1, 10, 30, true);
        var page2 = new PagedResult<int>(items, 1, 10, 30, true);
        var page3 = new PagedResult<int>(items, 2, 10, 30, false);

        page1.Should().Be(page2);
        page1.Should().NotBe(page3);
    }

    [Fact]
    public void EmptyResult_ShouldBeValid()
    {
        var page = new PagedResult<string>(
            Items: Array.Empty<string>(),
            PageNumber: 1,
            PageSize: 50,
            TotalCount: 0,
            HasNextPage: false
        );

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
        page.HasNextPage.Should().BeFalse();
    }
}
