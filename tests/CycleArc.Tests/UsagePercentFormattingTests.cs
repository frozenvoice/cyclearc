using CycleArc.Codex;

namespace CycleArc.Tests;

public sealed class UsagePercentFormattingTests
{
    [Theory]
    [InlineData(76.91, 23.09, "77%", "23%", "76.91%", "23.09%")]
    [InlineData(76.5, 23.5, "77%", "23%", "76.5%", "23.5%")]
    [InlineData(76.499, 23.501, "76%", "24%", "76.5%", "23.5%")]
    [InlineData(12.5, 87.5, "13%", "87%", "12.5%", "87.5%")]
    [InlineData(99.6, 0.4, ">99%", "<1%", "99.6%", "0.4%")]
    [InlineData(0.5, 99.5, "<1%", ">99%", "0.5%", "99.5%")]
    [InlineData(1, 99, "1%", "99%", "1%", "99%")]
    [InlineData(0, 100, "0%", "100%", "0%", "100%")]
    [InlineData(100, 0, "100%", "0%", "100%", "0%")]
    public void ComplementaryPairsUseWidgetBoundariesAndPreciseDetail(
        double used, double remaining,
        string expectedWidgetUsed, string expectedWidgetRemaining,
        string expectedDetailUsed, string expectedDetailRemaining)
    {
        Assert.Equal(expectedWidgetUsed, UsagePercentFormatting.Widget(used));
        Assert.Equal(expectedWidgetRemaining, UsagePercentFormatting.WidgetRemaining(used, remaining));
        Assert.Equal(expectedDetailUsed, UsagePercentFormatting.Detail(used));
        Assert.Equal(expectedDetailRemaining, UsagePercentFormatting.Detail(remaining));
    }

    [Theory]
    [InlineData(0.009, "<0.01%")]
    [InlineData(0.01, "0.01%")]
    [InlineData(99.99, "99.99%")]
    [InlineData(99.991, ">99.99%")]
    [InlineData(0, "0%")]
    [InlineData(100, "100%")]
    public void DetailBoundariesKeepTinyValuesVisible(double value, string expected)
    {
        Assert.Equal(expected, UsagePercentFormatting.Detail(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    public void InvalidValuesStayUnknown(double? value)
    {
        Assert.Equal("?", UsagePercentFormatting.Widget(value));
        Assert.Equal("?", UsagePercentFormatting.Detail(value));
        Assert.Equal("?", UsagePercentFormatting.WidgetRemaining(50, value));
    }

    [Fact]
    public void MissingOrIndependentValuesDoNotInventAComplement()
    {
        Assert.Equal("?", UsagePercentFormatting.WidgetRemaining(76.5, null));
        Assert.Equal("24%", UsagePercentFormatting.WidgetRemaining(76.5, 24));
        Assert.Equal("24%", UsagePercentFormatting.WidgetRemaining(null, 23.5));
        Assert.Equal("24%", UsagePercentFormatting.WidgetRemaining(76.5, 23.501));
        Assert.Equal("23%", UsagePercentFormatting.WidgetRemaining(76.5, 23.5 + 0.0000000000001));
    }

    [Fact]
    public void ComplementToleranceHandlesFloatingPointNoiseWithoutChangingIndependentValues()
    {
        Assert.Equal("23%", UsagePercentFormatting.WidgetRemaining(76.5, 23.50000000000001));
        Assert.Equal("24%", UsagePercentFormatting.WidgetRemaining(76.499, 23.50100000000001));
        Assert.Equal("24%", UsagePercentFormatting.WidgetRemaining(76.5, 23.5000001));
    }
}
