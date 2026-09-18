using CycleArc.Setup;

namespace CycleArc.Tests;

/// <summary>
/// The installer window scales its controls by the window's DPI. The frame has to scale with
/// them: while it stayed at its 96-DPI size, everything below the first rows fell outside the
/// window at 150% and above. The completion page also shows the location box and the Run
/// checkbox together, so those two must not sit on top of each other.
/// </summary>
public sealed class SetupLayoutTests
{
    public static TheoryData<uint> Scales => new() { 96, 120, 144, 168, 192, 240, 288 };

    [Theory]
    [MemberData(nameof(Scales))]
    public void NothingIsClippedOrOverlappingAtAnyScale(uint dpi)
    {
        var problems = SetupLayout.Problems(dpi);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheRunCheckboxSitsBelowTheLocationBox()
    {
        Assert.True(SetupLayout.RunCheck.Y >= SetupLayout.Location.Bottom,
            $"The Run checkbox starts at {SetupLayout.RunCheck.Y}, above the location box's bottom at {SetupLayout.Location.Bottom}.");
    }

    [Fact]
    public void EveryControlFitsTheClientAreaAtOneToOne()
    {
        foreach (var box in SetupLayout.All)
        {
            Assert.True(box.Right <= SetupLayout.ClientWidth, $"'{box.Name}' is wider than the client area.");
            Assert.True(box.Bottom <= SetupLayout.ClientHeight, $"'{box.Name}' is taller than the client area.");
        }
    }

    // A frame that is not scaled is the defect this guards: at 200% the client area doubles,
    // so the scaled boxes only fit if the frame was sized from the scaled client size.
    [Fact]
    public void ScaledBoxesNeedAScaledClientArea()
    {
        const uint dpi = 192;
        var unscaledWidth = SetupLayout.ClientWidth;
        var widest = SetupLayout.All.Max(box => SetupLayout.Scale(box.Right, dpi));
        Assert.True(widest > unscaledWidth,
            "At 200% the controls must exceed an unscaled client area, or this guard proves nothing.");
        Assert.True(widest <= SetupLayout.Scale(SetupLayout.ClientWidth, dpi));
    }
}
