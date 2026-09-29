namespace CycleArc.Codex;

public static class WidgetPlacement
{
    // Work areas are ordered with the primary monitor first.
    public static (double Left, double Top) Recover(double left, double top, double width, double height,
        IReadOnlyList<ScreenRect> workAreas, bool reset = false)
    {
        if (workAreas.Count == 0) return (40, 40);
        width = double.IsFinite(width) && width > 0 ? width : 180;
        height = double.IsFinite(height) && height > 0 ? height : 60;
        var area = workAreas[0];
        if (reset || !double.IsFinite(left) || !double.IsFinite(top))
        {
            left = area.X + 40;
            top = area.Y + 40;
        }
        else
        {
            var overlapping = Overlapping(left, top, width, height, workAreas);
            if (overlapping is null) { left = area.X + 40; top = area.Y + 40; }
            else area = overlapping.Value;
        }
        return RecoverInto(left, top, width, height, area);
    }

    /// <summary>
    /// Clamps into a work area that was already chosen for this pass. Relayout uses this after
    /// wrapping so a previous wide rectangle cannot re-select a different monitor by overlap.
    /// </summary>
    public static (double Left, double Top) RecoverInto(double left, double top, double width, double height,
        ScreenRect area)
    {
        width = double.IsFinite(width) && width > 0 ? width : 180;
        height = double.IsFinite(height) && height > 0 ? height : 60;
        if (!double.IsFinite(left) || !double.IsFinite(top))
            return (area.X + 40, area.Y + 40);
        // A fully visible widget is already valid, even within the flyout's 8px edge margin.
        // Do not move a user-positioned widget just because the application restarted.
        if (left >= area.X && top >= area.Y && left + width <= area.Right && top + height <= area.Bottom)
            return (left, top);
        return FlyoutPlacement.ClampToWorkArea(left, top, width, height, area);
    }

    /// <summary>
    /// The monitor that contains this origin. Used when relaying out after a move so a wide
    /// widget dropped onto a narrow display is measured against that display, not against the
    /// overlap of its previous large rectangle with the virtual desktop.
    /// An origin outside every work area (a widget pushed partly past the outer edge of a
    /// side monitor, or above the top) belongs to the nearest monitor, never to the primary
    /// by default: falling back to the primary made such a drop reappear on another display.
    /// </summary>
    public static ScreenRect AreaContaining(double left, double top, IReadOnlyList<ScreenRect> workAreas)
    {
        if (workAreas.Count == 0) return new ScreenRect(0, 0, 1920, 1040);
        if (!double.IsFinite(left) || !double.IsFinite(top)) return workAreas[0];
        var best = workAreas[0];
        var bestDistance = double.PositiveInfinity;
        foreach (var area in workAreas)
        {
            if (left >= area.X && left < area.Right && top >= area.Y && top < area.Bottom)
                return area;
            var dx = left < area.X ? area.X - left : left >= area.Right ? left - area.Right : 0;
            var dy = top < area.Y ? area.Y - top : top >= area.Bottom ? top - area.Bottom : 0;
            var distance = (dx * dx) + (dy * dy);
            // Strictly closer only, so a tie keeps the earlier (primary-first) monitor.
            if (distance < bestDistance) { bestDistance = distance; best = area; }
        }
        return best;
    }

    /// <summary>
    /// The work area the widget actually sits on, so wrapping is measured against that monitor
    /// rather than the whole virtual desktop. Falls back to the primary monitor when the saved
    /// position overlaps none of them. Coordinates are DIP, like every other caller here.
    /// </summary>
    public static ScreenRect AreaFor(double left, double top, double width, double height,
        IReadOnlyList<ScreenRect> workAreas)
    {
        if (workAreas.Count == 0) return new ScreenRect(0, 0, 1920, 1040);
        return Overlapping(left, top, width, height, workAreas) ?? workAreas[0];
    }

    private static ScreenRect? Overlapping(double left, double top, double width, double height,
        IReadOnlyList<ScreenRect> workAreas)
    {
        var largestOverlap = 0d;
        ScreenRect? best = null;
        foreach (var candidate in workAreas)
        {
            var overlap = Math.Max(0, Math.Min(left + width, candidate.Right) - Math.Max(left, candidate.X))
                * Math.Max(0, Math.Min(top + height, candidate.Bottom) - Math.Max(top, candidate.Y));
            if (overlap > largestOverlap) { largestOverlap = overlap; best = candidate; }
        }
        return best;
    }
}
