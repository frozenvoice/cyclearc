namespace CycleArc.Codex;

public enum FlyoutToggleAction
{
    Show,
    Hide,
    BringToFront
}

public static class FlyoutWindowState
{
    public static bool HidesOnDeactivate => false;

    public static bool IsTopmost(bool pinned) => pinned;

    public static bool AllowsHeaderDrag => true;

    public static bool UseSavedPosition(bool positionConfigured) => positionConfigured;

    public static bool RepositionNearAnchorOnUnpin => false;

    /// <summary>
    /// Pressing the tray icon takes the foreground from the popup before the click arrives, so a
    /// popup that lost activation this recently was the window the person was using.
    /// </summary>
    public static readonly TimeSpan RecentActivation = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// A tray click hides the popup the person is looking at, but brings an open popup that
    /// another window covers to the front instead of hiding it unseen.
    /// </summary>
    public static FlyoutToggleAction Toggle(bool visible, bool active, DateTimeOffset? lastDeactivated, DateTimeOffset now)
    {
        if (!visible) return FlyoutToggleAction.Show;
        if (active) return FlyoutToggleAction.Hide;
        return lastDeactivated is { } at && now - at >= TimeSpan.Zero && now - at <= RecentActivation
            ? FlyoutToggleAction.Hide
            : FlyoutToggleAction.BringToFront;
    }
}
