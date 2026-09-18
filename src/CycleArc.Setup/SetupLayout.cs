namespace CycleArc.Setup;

internal readonly record struct LayoutBox(string Name, int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// The window's layout in 96-DPI units, kept as data so it can be checked rather than only
/// looked at. Every box is scaled by the window's DPI, and the frame is sized from
/// <see cref="ClientWidth"/>/<see cref="ClientHeight"/> at that same scale - when the frame
/// was left unscaled, everything below the first rows fell outside the window at 150% and above.
/// </summary>
internal static class SetupLayout
{
    public const int ClientWidth = 560;
    public const int ClientHeight = 340;
    public const int Margin = 24;

    public static readonly LayoutBox Heading = new("heading", Margin, 22, 490, 30);
    public static readonly LayoutBox Body = new("body", Margin, 58, 490, 44);
    public static readonly LayoutBox LocationLabel = new("locationLabel", Margin, 112, 490, 18);
    public static readonly LayoutBox Location = new("location", Margin, 132, 490, 24);
    public static readonly LayoutBox Progress = new("progress", Margin, 132, 490, 18);
    public static readonly LayoutBox Status = new("status", Margin, 158, 490, 20);
    public static readonly LayoutBox Detail = new("detail", Margin, 112, 490, 100);
    public static readonly LayoutBox RunCheck = new("runCheck", Margin, 168, 300, 24);
    public static readonly LayoutBox Primary = new("primary", 318, 244, 96, 32);
    public static readonly LayoutBox Secondary = new("secondary", 420, 244, 96, 32);

    public static LayoutBox[] All => new[]
    {
        Heading, Body, LocationLabel, Location, Progress, Status, Detail, RunCheck, Primary, Secondary,
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

        // The completion page shows the location box and the Run checkbox together, so those
        // two must not sit on top of each other - they did while Run stayed at the location's y.
        if (Overlap(Location, RunCheck))
            problems.Add($"{dpi} dpi: the Run checkbox overlaps the install location box.");
        // Every page keeps its controls clear of the button row.
        foreach (var box in new[] { Location, Progress, Status, Detail, RunCheck })
            if (Overlap(box, Primary) || Overlap(box, Secondary))
                problems.Add($"{dpi} dpi: '{box.Name}' overlaps the button row.");
        if (Overlap(Primary, Secondary))
            problems.Add($"{dpi} dpi: the two buttons overlap.");

        return problems;
    }

    private static bool Overlap(LayoutBox left, LayoutBox right) =>
        left.X < right.Right && right.X < left.Right && left.Y < right.Bottom && right.Y < left.Bottom;
}
