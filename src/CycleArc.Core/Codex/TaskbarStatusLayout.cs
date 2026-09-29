namespace CycleArc.Codex;

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right
}

public enum TaskbarStripMode
{
    Full,
    Compact,
    UltraCompact,
    AboveTaskbar,
    Hidden
}

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Intersects(ScreenRect other) =>
        X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;

    public bool Contains(ScreenRect other) =>
        X <= other.X && Y <= other.Y && Right >= other.Right && Bottom >= other.Bottom;

    public ScreenRect Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + (dx * 2), Height + (dy * 2));
}

public readonly record struct TaskbarLayoutInput(
    ScreenRect Monitor,
    ScreenRect WorkArea,
    ScreenRect Taskbar,
    ScreenRect? NotifyArea,
    ScreenRect? ClockArea,
    ScreenRect? TaskList,
    TaskbarEdge Edge,
    double DpiScale,
    bool TaskbarAutoHide,
    bool TaskbarVisible,
    bool ExclusiveFullscreenOnMonitor);

public readonly record struct TaskbarLayoutResult(
    ScreenRect Bounds,
    TaskbarStripMode Mode,
    bool Visible,
    bool OverlapsNotify,
    bool OverlapsClock);

public static class TaskbarStatusPositioner
{
    public const int FullWidthDip = 148;
    public const int CompactWidthDip = 112;
    public const int UltraWidthDip = 82;
    public const int HeightDip = 26;
    public const int PaddingPx = 4;

    public static bool ShouldShow(
        bool enabled,
        bool exclusiveFullscreenOnMonitor,
        bool taskbarVisible) =>
        enabled && !exclusiveFullscreenOnMonitor && taskbarVisible;

    public static bool ShouldShow(TaskbarLayoutInput input) =>
        TaskbarVisibilityDetector.ShouldShowOverlay(
            input.ExclusiveFullscreenOnMonitor,
            input.TaskbarAutoHide,
            input.TaskbarVisible,
            input.Taskbar,
            input.Monitor,
            input.Edge);

    public static bool IsExclusiveFullscreen(
        ScreenRect foreground,
        ScreenRect monitor,
        ScreenRect workArea,
        bool foregroundIsMaximized = false,
        int tolerancePx = TaskbarVisibilityDetector.FullscreenTolerancePx) =>
        IsForegroundFullscreenOnMonitor(foreground, monitor, workArea, foregroundIsMaximized, tolerancePx);

    public static bool IsForegroundFullscreenOnMonitor(
        ScreenRect foreground,
        ScreenRect monitor,
        ScreenRect workArea,
        bool foregroundIsMaximized = false,
        int tolerancePx = TaskbarVisibilityDetector.FullscreenTolerancePx) =>
        FullscreenClassifier.Observe(
            ForegroundWindowFacts.FromMaximizedFlag(foreground, foregroundIsMaximized),
            monitor,
            workArea,
            tolerancePx).Kind == FullscreenObservationKind.Fullscreen;

    public static bool CoversMonitor(
        ScreenRect window,
        ScreenRect monitor,
        int tolerancePx = TaskbarVisibilityDetector.FullscreenTolerancePx)
    {
        if (tolerancePx < 0
            || monitor.Width <= 0
            || monitor.Height <= 0
            || window.Width <= 0
            || window.Height <= 0)
        {
            return false;
        }

        return Math.Abs(window.X - monitor.X) <= tolerancePx
            && Math.Abs(window.Y - monitor.Y) <= tolerancePx
            && Math.Abs(window.Right - monitor.Right) <= tolerancePx
            && Math.Abs(window.Bottom - monitor.Bottom) <= tolerancePx;
    }

    public static bool IsIgnoredFullscreenForeground(string? className) =>
        IsShellTaskbarForeground(className) || IsDesktopShellForeground(className);

    public static bool IsShellTaskbarForeground(string? className) =>
        className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    public static bool IsDesktopShellForeground(string? className) =>
        className is "Progman" or "WorkerW";

    /// <summary>
    /// Shell surfaces (Start, Search, Action Center, Alt-Tab, the hidden-icons flyout, ...)
    /// that can enumerate as large or oddly-bounded top-level windows but never represent
    /// fullscreen application content. Excluded from monitor fullscreen enumeration.
    /// </summary>
    public static bool IsNonApplicationShellClass(string? className) =>
        IsShellTaskbarForeground(className)
        || IsDesktopShellForeground(className)
        || className is "Windows.UI.Core.CoreWindow"
            or "TaskSwitcherWnd"
            or "MultitaskingViewFrame"
            or "NotifyIconOverflowWindow"
            or "Shell_InputSwitchTopLevelWindow"
            or "XamlExplorerHostIslandWindow";

    public static ForegroundWindowRole ClassifyForegroundRole(string? className, bool ownOverlay, bool taskbarWindow)
    {
        if (ownOverlay)
        {
            return ForegroundWindowRole.OwnOverlay;
        }

        if (taskbarWindow || IsShellTaskbarForeground(className))
        {
            return ForegroundWindowRole.ShellTaskbar;
        }

        return IsNonApplicationShellClass(className)
            ? ForegroundWindowRole.Desktop
            : ForegroundWindowRole.Application;
    }

    public static TaskbarLayoutResult Place(
        TaskbarLayoutInput input,
        int? fullWidthDip = null,
        int? compactWidthDip = null,
        int? ultraWidthDip = null,
        int? heightDip = null)
    {
        if (!ShouldShow(input))
        {
            return new TaskbarLayoutResult(default, TaskbarStripMode.Hidden, false, false, false);
        }

        var scale = input.DpiScale <= 0 ? 1 : input.DpiScale;
        var full = Dip(fullWidthDip ?? FullWidthDip, scale);
        var compact = Dip(compactWidthDip ?? CompactWidthDip, scale);
        var ultra = Dip(ultraWidthDip ?? UltraWidthDip, scale);
        var height = Dip(heightDip ?? HeightDip, scale);
        var reserved = ReservedNotify(input);

        foreach (var (width, mode) in new[]
                 {
                     (full, TaskbarStripMode.Full),
                     (compact, TaskbarStripMode.Compact),
                     (ultra, TaskbarStripMode.UltraCompact)
                 })
        {
            if (TryPlaceInside(input, reserved, width, height, out var inside)
                && !OverlapsProtected(inside, reserved))
            {
                return Result(inside, mode, reserved);
            }
        }

        var above = PlaceAdjacent(input, reserved, ultra, height);
        return Result(above, TaskbarStripMode.AboveTaskbar, reserved);
    }

    public static TaskbarStripMode ChooseMode(int availablePx, double dpiScale)
    {
        var scale = dpiScale <= 0 ? 1 : dpiScale;
        if (availablePx >= Dip(FullWidthDip, scale) + PaddingPx)
        {
            return TaskbarStripMode.Full;
        }

        if (availablePx >= Dip(CompactWidthDip, scale) + PaddingPx)
        {
            return TaskbarStripMode.Compact;
        }

        if (availablePx >= Dip(UltraWidthDip, scale) + PaddingPx)
        {
            return TaskbarStripMode.UltraCompact;
        }

        return TaskbarStripMode.AboveTaskbar;
    }

    private static bool TryPlaceInside(
        TaskbarLayoutInput input,
        ScreenRect reserved,
        int width,
        int height,
        out ScreenRect bounds)
    {
        bounds = default;
        var bar = input.Taskbar;
        switch (input.Edge)
        {
            case TaskbarEdge.Bottom:
            case TaskbarEdge.Top:
                var right = reserved.X - PaddingPx;
                var left = right - width;
                var minLeft = input.TaskList?.Right + PaddingPx ?? bar.X + PaddingPx;
                if (left < minLeft)
                {
                    return false;
                }

                var y = bar.Y + Math.Max(0, (bar.Height - height) / 2);
                bounds = new ScreenRect(left, y, width, height);
                return bar.Contains(bounds) || IntersectsInterior(bar, bounds);
            case TaskbarEdge.Left:
            case TaskbarEdge.Right:
                var bottom = reserved.Y - PaddingPx;
                var top = bottom - height;
                if (top < bar.Y + PaddingPx || width + (PaddingPx * 2) > bar.Width)
                {
                    return false;
                }

                var x = bar.X + Math.Max(0, (bar.Width - width) / 2);
                bounds = new ScreenRect(x, top, width, height);
                return true;
            default:
                return false;
        }
    }

    private static ScreenRect PlaceAdjacent(
        TaskbarLayoutInput input,
        ScreenRect reserved,
        int width,
        int height)
    {
        var work = input.WorkArea;
        return input.Edge switch
        {
            TaskbarEdge.Bottom => Clamp(
                new ScreenRect(reserved.X - width - PaddingPx, input.Taskbar.Y - height - PaddingPx, width, height),
                work),
            TaskbarEdge.Top => Clamp(
                new ScreenRect(reserved.X - width - PaddingPx, input.Taskbar.Bottom + PaddingPx, width, height),
                work),
            TaskbarEdge.Left => Clamp(
                new ScreenRect(input.Taskbar.Right + PaddingPx, reserved.Y - height - PaddingPx, width, height),
                work),
            _ => Clamp(
                new ScreenRect(input.Taskbar.X - width - PaddingPx, reserved.Y - height - PaddingPx, width, height),
                work)
        };
    }

    private static ScreenRect ReservedNotify(TaskbarLayoutInput input)
    {
        if (input.NotifyArea is { } notify && notify.Width > 0 && notify.Height > 0)
        {
            if (input.ClockArea is { } clock && clock.Width > 0)
            {
                return Union(notify, clock);
            }

            return notify;
        }

        if (input.ClockArea is { } onlyClock && onlyClock.Width > 0)
        {
            return onlyClock;
        }

        var bar = input.Taskbar;
        var reserve = Math.Max(Dip(140, input.DpiScale), bar.Width / 5);
        return input.Edge switch
        {
            TaskbarEdge.Left or TaskbarEdge.Right =>
                new ScreenRect(bar.X, bar.Bottom - reserve, bar.Width, reserve),
            _ => new ScreenRect(bar.Right - reserve, bar.Y, reserve, bar.Height)
        };
    }

    private static bool OverlapsProtected(ScreenRect bounds, ScreenRect reserved) =>
        bounds.Intersects(reserved.Inflate(-1, -1));

    private static bool IntersectsInterior(ScreenRect bar, ScreenRect bounds) =>
        bounds.X >= bar.X && bounds.Right <= bar.Right && bounds.Y >= bar.Y && bounds.Bottom <= bar.Bottom;

    private static TaskbarLayoutResult Result(ScreenRect bounds, TaskbarStripMode mode, ScreenRect reserved)
    {
        var overlaps = OverlapsProtected(bounds, reserved);
        return new TaskbarLayoutResult(bounds, mode, true, overlaps, overlaps);
    }

    private static ScreenRect Union(ScreenRect a, ScreenRect b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new ScreenRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    private static ScreenRect Clamp(ScreenRect rect, ScreenRect work)
    {
        var x = Math.Clamp(rect.X, work.X, Math.Max(work.X, work.Right - rect.Width));
        var y = Math.Clamp(rect.Y, work.Y, Math.Max(work.Y, work.Bottom - rect.Height));
        return new ScreenRect(x, y, rect.Width, rect.Height);
    }

    private static int Dip(int value, double scale) => (int)Math.Round(value * scale);

    public static (double Left, double Top, double Width, double Height) ToDip(ScreenRect physical, double scale)
    {
        scale = scale <= 0 ? 1 : scale;
        return (physical.X / scale, physical.Y / scale, physical.Width / scale, physical.Height / scale);
    }
}

public static class TaskbarStripInteraction
{
    public const string Left = "left";
    public const string Middle = "middle";
    public const string Right = "right";

    public static TaskbarStripAction FromButton(string button) => button switch
    {
        Middle => TaskbarStripAction.CombinedRefresh,
        Right => TaskbarStripAction.ContextMenu,
        _ => TaskbarStripAction.ToggleFlyout
    };
}

public enum TaskbarStripAction
{
    ToggleFlyout,
    CombinedRefresh,
    ContextMenu
}

public enum FlyoutOpenSource
{
    Tray,
    TaskbarStrip
}

public static class FlyoutPlacement
{
    public static (double Left, double Top) PlaceNear(
        ScreenRect anchor,
        TaskbarEdge edge,
        double flyoutWidth,
        double flyoutHeight,
        ScreenRect workArea)
    {
        double left;
        double top;
        switch (edge)
        {
            case TaskbarEdge.Top:
                left = anchor.Right - flyoutWidth;
                top = anchor.Bottom + 8;
                break;
            case TaskbarEdge.Left:
                left = anchor.Right + 8;
                top = anchor.Bottom - flyoutHeight;
                break;
            case TaskbarEdge.Right:
                left = anchor.X - flyoutWidth - 8;
                top = anchor.Bottom - flyoutHeight;
                break;
            default:
                left = anchor.Right - flyoutWidth;
                top = anchor.Y - flyoutHeight - 8;
                break;
        }

        left = Math.Clamp(left, workArea.X + 8, workArea.Right - flyoutWidth - 8);
        top = Math.Clamp(top, workArea.Y + 8, workArea.Bottom - flyoutHeight - 8);
        return (left, top);
    }

    public static ScreenRect SelectWorkArea(
        double left,
        double top,
        double width,
        double height,
        IReadOnlyList<ScreenRect> workAreas)
    {
        if (workAreas.Count == 0)
        {
            return new ScreenRect(0, 0, 1920, 1080);
        }

        var placed = new ScreenRect(
            (int)Math.Round(left),
            (int)Math.Round(top),
            Math.Max(1, (int)Math.Round(width)),
            Math.Max(1, (int)Math.Round(height)));
        // The monitor holding most of the window, like Windows' own MonitorFromRect. Taking
        // the first intersecting area let a sliver over the primary pull the window across.
        ScreenRect? best = null;
        long bestOverlap = 0;
        foreach (var area in workAreas)
        {
            if (!area.Intersects(placed))
            {
                continue;
            }

            var overlap = (long)(Math.Min(area.Right, placed.Right) - Math.Max(area.X, placed.X))
                * (Math.Min(area.Bottom, placed.Bottom) - Math.Max(area.Y, placed.Y));
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = area;
            }
        }

        return best ?? workAreas[0];
    }

    public static (double Left, double Top) ClampToWorkArea(
        double left,
        double top,
        double width,
        double height,
        ScreenRect workArea)
    {
        var minLeft = workArea.X + 8.0;
        var minTop = workArea.Y + 8.0;
        var maxLeft = workArea.Right - width - 8;
        var maxTop = workArea.Bottom - height - 8;
        left = maxLeft < minLeft ? minLeft : Math.Clamp(left, minLeft, maxLeft);
        top = maxTop < minTop ? minTop : Math.Clamp(top, minTop, maxTop);
        return (left, top);
    }
}

public sealed class LayoutSignalDebouncer
{
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    public bool ShouldHandle(DateTimeOffset now, TimeSpan window)
    {
        if (now - _last < window)
        {
            return false;
        }

        _last = now;
        return true;
    }
}
