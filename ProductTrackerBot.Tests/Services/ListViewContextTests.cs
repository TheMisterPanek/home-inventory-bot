using ProductTrackerBot.Services;

namespace ProductTrackerBot.Tests.Services;

public class ListViewContextTests
{
    [Fact]
    public void TryParse_WithFullSuffix_ReadsItemAndView()
    {
        var parsed = ListViewContext.TryParse("shop:done:5:3:0,2:2", "shop:done:", out var itemId, out var context);

        Assert.True(parsed);
        Assert.Equal(5, itemId);
        Assert.Equal(3, context.PageNumber);
        Assert.Equal(new[] { 0, 2 }, context.TagIndices);
        Assert.Equal(2, context.TagPageNumber);
    }

    [Fact]
    public void TryParse_WithEmptyTagCsv_YieldsUnfilteredContext()
    {
        Assert.True(ListViewContext.TryParse("shop:sel:0:2::1", "shop:sel:", out var itemId, out var context));

        Assert.Equal(0, itemId);
        Assert.Equal(2, context.PageNumber);
        Assert.Empty(context.TagIndices);
    }

    // Buttons rendered before the view context existed are still live in older chat messages.
    [Fact]
    public void TryParse_WithoutSuffix_FallsBackToDefaultView()
    {
        Assert.True(ListViewContext.TryParse("shop:remove:12", "shop:remove:", out var itemId, out var context));

        Assert.Equal(12, itemId);
        Assert.Equal(1, context.PageNumber);
        Assert.Empty(context.TagIndices);
        Assert.Equal(1, context.TagPageNumber);
    }

    [Theory]
    [InlineData("shop:done:abc")]
    [InlineData("shop:done:")]
    [InlineData("list_next:-100:2")]
    public void TryParse_WithMalformedData_ReturnsFalse(string data)
    {
        Assert.False(ListViewContext.TryParse(data, "shop:done:", out _, out _));
    }

    [Fact]
    public void ToCallbackSuffix_RoundTrips()
    {
        var context = new ListViewContext(4, new[] { 1, 3 }, 2);

        Assert.Equal("4:1,3:2", context.ToCallbackSuffix());
        Assert.True(ListViewContext.TryParse($"shop:sel:9:{context.ToCallbackSuffix()}", "shop:sel:", out var itemId, out var parsed));
        Assert.Equal(9, itemId);
        Assert.Equal(context.PageNumber, parsed.PageNumber);
        Assert.Equal(context.TagIndices, parsed.TagIndices);
        Assert.Equal(context.TagPageNumber, parsed.TagPageNumber);
    }
}
