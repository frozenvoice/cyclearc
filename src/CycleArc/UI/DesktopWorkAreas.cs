using CycleArc.Codex;

namespace CycleArc.UI;

public static class DesktopWorkAreas
{
    public static IReadOnlyList<ScreenRect> For(Window window)
    {
        var transform = FromDevice(window);
        return System.Windows.Forms.Screen.AllScreens.OrderByDescending(screen => screen.Primary)
            .Select(screen => ToDip(screen.WorkingArea, transform)).ToArray();
    }

    /// <summary>
    /// The work area of the monitor under the mouse pointer (or the nearest one when the pointer
    /// sits on a taskbar or between monitors), converted exactly like <see cref="For"/> so it
    /// compares equal to an entry of that list.
    /// </summary>
    public static ScreenRect AtCursor(Window window) =>
        ToDip(System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition).WorkingArea,
            FromDevice(window));

    private static Matrix FromDevice(Window window) =>
        PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

    private static ScreenRect ToDip(System.Drawing.Rectangle area, Matrix transform)
    {
        var topLeft = transform.Transform(new System.Windows.Point(area.Left, area.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(area.Right, area.Bottom));
        return new ScreenRect((int)topLeft.X, (int)topLeft.Y,
            (int)Math.Max(1, bottomRight.X - topLeft.X), (int)Math.Max(1, bottomRight.Y - topLeft.Y));
    }
}
