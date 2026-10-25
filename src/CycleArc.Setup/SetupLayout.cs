namespace CycleArc.Setup;

internal readonly record struct LayoutBox(string Name, int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>The four states of the one installer window.</summary>
internal enum SetupPage { Confirm, Progress, Done, Failed }

/// <summary>
/// Everything the window shows. Some are real child controls (buttons, checkboxes, text the
/// accessibility tree reads); the rest are painted by the window itself.
/// </summary>
internal enum SetupElement
{
    Brand, Minimize, Close, Icon, Heading, Body, Location, Status, Progress, Hint, Detail,
    DesktopShortcut, Run, Secondary, Primary,
}

/// <summary>
/// The window's layout in 96-DPI units, kept as data so it can be checked rather than only
/// looked at. Every box is scaled by the window's DPI, and the frame is sized from
/// <see cref="ClientWidth"/>/<see cref="ClientHeight"/> at that same scale - when the frame
/// was left unscaled, everything below the first rows fell outside the window at 150% and above.
///
/// The window has no system frame: the client area is the whole window, and the brand,
/// minimise and close controls sit inside it.
/// </summary>
internal static class SetupLayout
{
    public const int ClientWidth = 600;
    public const int ClientHeight = 340;
    public const int Margin = 40;

    // Window chrome, on every page.
    public static readonly LayoutBox Brand = new("brand", 24, 12, 200, 32);
    public static readonly LayoutBox Minimize = new("minimize", 518, 8, 36, 30);
    public static readonly LayoutBox Close = new("close", 556, 8, 36, 30);

    // Confirmation and completion share one column: icon, heading, body, location.
    public static readonly LayoutBox Icon = new("icon", 278, 34, 44, 44);
    public static readonly LayoutBox Heading = new("heading", Margin, 88, 520, 40);
    public static readonly LayoutBox Body = new("body", Margin, 130, 520, 40);
    public static readonly LayoutBox Location = new("location", 80, 184, 440, 38);

    // Progress: one large message, the real stage, and a thin bar.
    public static readonly LayoutBox ProgressHeading = new("progressHeading", Margin, 104, 520, 40);
    public static readonly LayoutBox Status = new("status", 60, 148, 480, 22);
    public static readonly LayoutBox Progress = new("progress", 80, 212, 440, 4);
    public static readonly LayoutBox Hint = new("hint", 60, 230, 480, 20);

    // Failure: the cause and the log path, in a scrollable panel.
    public static readonly LayoutBox FailedIcon = new("failedIcon", 280, 34, 40, 40);
    public static readonly LayoutBox FailedHeading = new("failedHeading", Margin, 80, 520, 40);
    public static readonly LayoutBox FailedBody = new("failedBody", 60, 120, 480, 22);
    public static readonly LayoutBox Detail = new("detail", Margin, 150, 520, 104);

    // The action row.
    public static readonly LayoutBox DesktopShortcutCheck = new("desktopShortcutCheck", Margin, 274, 280, 28);
    public static readonly LayoutBox RunCheck = new("runCheck", Margin, 274, 280, 28);
    public static readonly LayoutBox Secondary = new("secondary", 352, 270, 100, 36);
    public static readonly LayoutBox Primary = new("primary", 460, 270, 100, 36);

    /// <summary>The inset of the failure text inside its panel.</summary>
    public const int DetailPadding = 12;

    /// <summary>What each page shows, and where.</summary>
    public static IReadOnlyList<(SetupElement Element, LayoutBox Box)> Page(SetupPage page)
    {
        var chrome = new List<(SetupElement, LayoutBox)>
        {
            (SetupElement.Brand, Brand), (SetupElement.Minimize, Minimize), (SetupElement.Close, Close),
        };
        chrome.AddRange(page switch
        {
            SetupPage.Confirm => new[]
            {
                (SetupElement.Icon, Icon), (SetupElement.Heading, Heading), (SetupElement.Body, Body),
                (SetupElement.Location, Location), (SetupElement.DesktopShortcut, DesktopShortcutCheck),
                (SetupElement.Secondary, Secondary), (SetupElement.Primary, Primary),
            },
            SetupPage.Progress => new[]
            {
                (SetupElement.Heading, ProgressHeading), (SetupElement.Status, Status),
                (SetupElement.Progress, Progress), (SetupElement.Hint, Hint),
            },
            SetupPage.Done => new[]
            {
                (SetupElement.Icon, Icon), (SetupElement.Heading, Heading), (SetupElement.Body, Body),
                (SetupElement.Location, Location), (SetupElement.Run, RunCheck), (SetupElement.Primary, Primary),
            },
            _ => new[]
            {
                (SetupElement.Icon, FailedIcon), (SetupElement.Heading, FailedHeading),
                (SetupElement.Body, FailedBody), (SetupElement.Detail, Detail),
                (SetupElement.Primary, Primary),
            },
        });
        return chrome;
    }

    /// <summary>Where <paramref name="element"/> sits on <paramref name="page"/>, or null when it is not shown.</summary>
    public static LayoutBox? Box(SetupPage page, SetupElement element)
    {
        foreach (var (candidate, box) in Page(page))
            if (candidate == element) return box;
        return null;
    }

    public static SetupPage[] Pages => new[] { SetupPage.Confirm, SetupPage.Progress, SetupPage.Done, SetupPage.Failed };

    public static LayoutBox[] All => new[]
    {
        Brand, Minimize, Close, Icon, Heading, Body, Location, ProgressHeading, Status, Progress, Hint,
        FailedIcon, FailedHeading, FailedBody, Detail, DesktopShortcutCheck, RunCheck, Secondary, Primary,
    };

    public static int Scale(int value, uint dpi) => (int)Math.Round(value * dpi / 96.0);

    /// <summary>
    /// Checks the invariants that a scaled window must keep. Returns the problems found, so a
    /// self-test can report them rather than a person having to spot a clipped control.
    /// </summary>
    public static IReadOnlyList<string> Problems(uint dpi)
    {
        var problems = new List<string>();
        var width = Scale(ClientWidth, dpi);
        var height = Scale(ClientHeight, dpi);

        foreach (var box in All)
        {
            var x = Scale(box.X, dpi);
            var y = Scale(box.Y, dpi);
            var right = Scale(box.Right, dpi);
            var bottom = Scale(box.Bottom, dpi);
            if (x < 0 || y < 0 || right > width || bottom > height)
                problems.Add($"{dpi} dpi: '{box.Name}' at ({x},{y})-({right},{bottom}) falls outside the {width}x{height} client area.");
        }

        // Nothing shown on the same page may sit on top of anything else there, after scaling.
        // This covers the cases that went wrong before: the Run checkbox on the location box,
        // and the desktop shortcut checkbox or the status line running into the button row.
        foreach (var page in Pages)
        {
            var boxes = Page(page);
            for (var i = 0; i < boxes.Count; i++)
                for (var j = i + 1; j < boxes.Count; j++)
                    if (Overlap(Scaled(boxes[i].Box, dpi), Scaled(boxes[j].Box, dpi)))
                        problems.Add($"{dpi} dpi: on the {page} page '{boxes[i].Box.Name}' overlaps '{boxes[j].Box.Name}'.");
        }

        return problems;
    }

    private static LayoutBox Scaled(LayoutBox box, uint dpi)
    {
        var x = Scale(box.X, dpi);
        var y = Scale(box.Y, dpi);
        return box with { X = x, Y = y, Width = Scale(box.Right, dpi) - x, Height = Scale(box.Bottom, dpi) - y };
    }

    private static bool Overlap(LayoutBox left, LayoutBox right) =>
        left.X < right.Right && right.X < left.Right && left.Y < right.Bottom && right.Y < left.Bottom;
}
