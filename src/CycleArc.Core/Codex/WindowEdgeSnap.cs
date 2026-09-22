namespace CycleArc.Codex;

public enum HorizontalEdgeAnchor
{
    None,
    Left,
    Right
}

public enum VerticalEdgeAnchor
{
    None,
    Top,
    Bottom
}

public readonly record struct WindowEdgeAnchors(
    HorizontalEdgeAnchor Horizontal = HorizontalEdgeAnchor.None,
    VerticalEdgeAnchor Vertical = VerticalEdgeAnchor.None)
{
    public bool IsAttached => Horizontal != HorizontalEdgeAnchor.None
        || Vertical != VerticalEdgeAnchor.None;

    public WindowEdgeAnchors Normalize() => new(
        Horizontal is HorizontalEdgeAnchor.Left or HorizontalEdgeAnchor.Right
            ? Horizontal
            : HorizontalEdgeAnchor.None,
        Vertical is VerticalEdgeAnchor.Top or VerticalEdgeAnchor.Bottom
            ? Vertical
            : VerticalEdgeAnchor.None);
}

public static class WindowEdgeSnap
{
    public const double MarginDip = 8;
    public const double ThresholdDip = 12;

    public static WindowEdgeAnchors Detect(
        double left,
        double top,
        double width,
        double height,
        ScreenRect work,
        bool enabled = true,
        bool bypass = false)
    {
        if (!enabled
            || bypass
            || !double.IsFinite(left)
            || !double.IsFinite(top)
            || !UsableDimension(width)
            || !UsableDimension(height)
            || work.Width <= 0
            || work.Height <= 0)
        {
            return default;
        }

        var horizontal = DetectHorizontal(left, width, work);
        var vertical = DetectVertical(top, height, work);
        return new WindowEdgeAnchors(horizontal, vertical);
    }

    public static (double Left, double Top) Place(
        double left,
        double top,
        double width,
        double height,
        ScreenRect work,
        WindowEdgeAnchors anchors)
    {
        // Recover into the already-selected work area before applying anchors. This keeps
        // placement safe for stale/off-screen coordinates without allowing this helper to
        // select another monitor. The original coordinates that are valid on one axis are
        // restored below so a recovery on the other axis cannot move a free axis.
        if (work.Width <= 0 || work.Height <= 0)
        {
            return (FiniteOrZero(left), FiniteOrZero(top));
        }

        var safeWidth = SafeDimension(width, 180);
        var safeHeight = SafeDimension(height, 60);
        var recovered = WidgetPlacement.RecoverInto(left, top, safeWidth, safeHeight, work);
        var placedLeft = FiniteOrZero(recovered.Left);
        var placedTop = FiniteOrZero(recovered.Top);
        if (FitsHorizontal(left, safeWidth, work))
        {
            placedLeft = left;
        }

        if (FitsVertical(top, safeHeight, work))
        {
            placedTop = top;
        }

        // RecoverInto uses a +40 fallback for non-finite coordinates. Clamp that fallback
        // back to the fixed area when a very small work area cannot contain it.
        if (!double.IsFinite(left))
        {
            placedLeft = ClampRecoveredAxis(placedLeft, safeWidth, work.X, work.Width);
        }

        if (!double.IsFinite(top))
        {
            placedTop = ClampRecoveredAxis(placedTop, safeHeight, work.Y, work.Height);
        }

        var normalized = anchors.Normalize();
        if (IsHorizontalAttachable(safeWidth, work))
        {
            placedLeft = normalized.Horizontal switch
            {
                HorizontalEdgeAnchor.Left => work.X + MarginDip,
                HorizontalEdgeAnchor.Right => work.X + (double)work.Width - MarginDip - safeWidth,
                _ => placedLeft
            };
        }

        if (IsVerticalAttachable(safeHeight, work))
        {
            placedTop = normalized.Vertical switch
            {
                VerticalEdgeAnchor.Top => work.Y + MarginDip,
                VerticalEdgeAnchor.Bottom => work.Y + (double)work.Height - MarginDip - safeHeight,
                _ => placedTop
            };
        }

        return (FiniteOrZero(placedLeft), FiniteOrZero(placedTop));
    }

    private static HorizontalEdgeAnchor DetectHorizontal(double left, double width, ScreenRect work)
    {
        if (!IsHorizontalAttachable(width, work))
        {
            return HorizontalEdgeAnchor.None;
        }

        var leftTarget = work.X + MarginDip;
        var rightTarget = work.X + work.Width - MarginDip - width;
        var leftDistance = Math.Abs(left - leftTarget);
        var rightDistance = Math.Abs(left - rightTarget);
        var leftWithin = leftDistance <= ThresholdDip;
        var rightWithin = rightDistance <= ThresholdDip;

        if (!leftWithin && !rightWithin)
        {
            return HorizontalEdgeAnchor.None;
        }

        // A tie deliberately favors the left edge, so the result is stable and never
        // attempts to satisfy both sides by changing the window size.
        return leftWithin && (!rightWithin || leftDistance <= rightDistance)
            ? HorizontalEdgeAnchor.Left
            : HorizontalEdgeAnchor.Right;
    }

    private static VerticalEdgeAnchor DetectVertical(double top, double height, ScreenRect work)
    {
        if (!IsVerticalAttachable(height, work))
        {
            return VerticalEdgeAnchor.None;
        }

        var topTarget = work.Y + MarginDip;
        var bottomTarget = work.Y + work.Height - MarginDip - height;
        var topDistance = Math.Abs(top - topTarget);
        var bottomDistance = Math.Abs(top - bottomTarget);
        var topWithin = topDistance <= ThresholdDip;
        var bottomWithin = bottomDistance <= ThresholdDip;

        if (!topWithin && !bottomWithin)
        {
            return VerticalEdgeAnchor.None;
        }

        // A tie deliberately favors the top edge for deterministic corner handling.
        return topWithin && (!bottomWithin || topDistance <= bottomDistance)
            ? VerticalEdgeAnchor.Top
            : VerticalEdgeAnchor.Bottom;
    }

    private static bool IsHorizontalAttachable(double width, ScreenRect work) =>
        UsableDimension(width) && width <= work.Width - (MarginDip * 2);

    private static bool IsVerticalAttachable(double height, ScreenRect work) =>
        UsableDimension(height) && height <= work.Height - (MarginDip * 2);

    private static bool FitsHorizontal(double left, double width, ScreenRect work) =>
        double.IsFinite(left)
        && IsUsableDimensionForArea(width)
        && left >= work.X
        && left + width <= work.X + (double)work.Width;

    private static bool FitsVertical(double top, double height, ScreenRect work) =>
        double.IsFinite(top)
        && IsUsableDimensionForArea(height)
        && top >= work.Y
        && top + height <= work.Y + (double)work.Height;

    private static double ClampRecoveredAxis(double origin, double size, int workOrigin, int workLength)
    {
        var min = workOrigin + MarginDip;
        var max = workOrigin + (double)workLength - size - MarginDip;
        return max < min ? min : Math.Clamp(FiniteOrZero(origin), min, max);
    }

    private static bool IsUsableDimensionForArea(double value) =>
        double.IsFinite(value) && value > 0;

    private static bool UsableDimension(double value) => double.IsFinite(value) && value > 0;

    private static double SafeDimension(double value, double fallback) =>
        UsableDimension(value) ? value : fallback;

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
}
