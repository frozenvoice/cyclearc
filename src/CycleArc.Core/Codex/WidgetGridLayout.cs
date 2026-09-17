namespace CycleArc.Codex;

/// <summary>
/// How many account modules the widget puts on one row, and how the rest wrap, for the work
/// area the widget currently sits on. Pure DIP arithmetic with no WPF type, so the wrapping
/// rule can be tested without a desktop. Physical pixels never reach this: callers convert
/// monitor rectangles to DIP first, exactly as <see cref="WidgetPlacement"/> expects them.
/// </summary>
public sealed record WidgetGridLayout(int Columns, int Rows, double Width, double Height, bool Scrolls)
{
    /// One account module. Wide enough for a display name, a ring and two period lines.
    public const double ModuleWidth = 232;

    /// The hairline between neighbouring modules, horizontally and between wrapped rows.
    public const double SeparatorThickness = 1;

    /// The rounded panel's own border and padding, added around the module grid.
    public const double ChromeWidth = 22;
    public const double ChromeHeight = 20;

    /// The widget keeps this much clear of the work-area edge before it wraps or scrolls.
    public const double EdgeMargin = 8;

    /// Height available to the module grid once the panel chrome and header are taken out.
    public double ModuleViewportHeight { get; init; }

    public static WidgetGridLayout For(int accountCount, double headerHeight, double moduleHeight, ScreenRect workArea)
    {
        var count = Math.Max(accountCount, 1);
        moduleHeight = double.IsFinite(moduleHeight) && moduleHeight > 0 ? moduleHeight : 1;
        headerHeight = double.IsFinite(headerHeight) && headerHeight > 0 ? headerHeight : 0;

        // One module always fits, even on a work area narrower than the module: the placement
        // clamp then anchors the panel inside the monitor instead of shrinking the text.
        var usable = Math.Max(workArea.Width - (2 * EdgeMargin) - ChromeWidth, ModuleWidth);
        var fits = (int)Math.Floor((usable + SeparatorThickness) / (ModuleWidth + SeparatorThickness));
        var columns = Math.Clamp(fits, 1, count);
        var rows = (int)Math.Ceiling(count / (double)columns);

        var width = ChromeWidth + (columns * ModuleWidth) + ((columns - 1) * SeparatorThickness);
        var grid = (rows * moduleHeight) + ((rows - 1) * SeparatorThickness);
        var height = ChromeHeight + headerHeight + grid;

        // Above the work area the module grid scrolls; the header and the panel stay put.
        var maximum = Math.Max(workArea.Height - (2 * EdgeMargin), ChromeHeight + headerHeight + moduleHeight);
        var scrolls = height > maximum;
        var outer = scrolls ? maximum : height;
        return new WidgetGridLayout(columns, rows, width, outer, scrolls)
        {
            ModuleViewportHeight = Math.Max(outer - ChromeHeight - headerHeight, moduleHeight)
        };
    }
}
