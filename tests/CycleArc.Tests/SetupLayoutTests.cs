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
    public void TheDesktopShortcutCheckboxSitsBelowTheLocationBox()
    {
        Assert.True(SetupLayout.DesktopShortcutCheck.Y >= SetupLayout.Location.Bottom,
            $"The desktop shortcut checkbox starts at {SetupLayout.DesktopShortcutCheck.Y}, above the location box's bottom at {SetupLayout.Location.Bottom}.");
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

    // The installer cannot be cancelled once the engine runs, so the progress page offers no
    // action at all; the confirmation and completion pages keep the choices the UI
    // verification script drives by control ID.
    [Fact]
    public void EachPageShowsOnlyItsOwnActions()
    {
        static SetupElement[] On(SetupPage page) => SetupLayout.Page(page).Select(entry => entry.Element).ToArray();

        var progress = On(SetupPage.Progress);
        foreach (var action in new[] { SetupElement.Primary, SetupElement.Secondary,
                     SetupElement.DesktopShortcut, SetupElement.Run })
            Assert.DoesNotContain(action, progress);
        Assert.Contains(SetupElement.Progress, progress);
        Assert.Contains(SetupElement.Status, progress);

        Assert.Contains(SetupElement.DesktopShortcut, On(SetupPage.Confirm));
        Assert.Contains(SetupElement.Secondary, On(SetupPage.Confirm));
        Assert.DoesNotContain(SetupElement.Run, On(SetupPage.Confirm));
        Assert.Contains(SetupElement.Run, On(SetupPage.Done));
        Assert.DoesNotContain(SetupElement.DesktopShortcut, On(SetupPage.Done));
        Assert.Contains(SetupElement.Detail, On(SetupPage.Failed));
        Assert.DoesNotContain(SetupElement.Run, On(SetupPage.Failed));
        foreach (var page in SetupLayout.Pages)
        {
            Assert.Contains(SetupElement.Minimize, On(page));
            Assert.Contains(SetupElement.Close, On(page));
        }
    }

    [Fact]
    public void TheWindowIsACompactLandscapeFrame()
    {
        Assert.InRange(SetupLayout.ClientWidth, 560, 640);
        Assert.InRange(SetupLayout.ClientHeight, 320, 380);
        Assert.True(SetupLayout.ClientWidth > SetupLayout.ClientHeight);
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
