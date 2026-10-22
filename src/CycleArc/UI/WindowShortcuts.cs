using System.Windows.Input;
using CycleArc.Codex;

namespace CycleArc.UI;

/// <summary>Keyboard and wheel shortcuts shared by the detail popup and the widget.</summary>
internal static class WindowShortcuts
{
    /// <summary>F5 or Ctrl+R refreshes, like a browser; other modifiers are left alone.</summary>
    public static bool IsRefresh(Key key, ModifierKeys modifiers) =>
        key == Key.F5 && modifiers == ModifierKeys.None
        || key == Key.R && modifiers == ModifierKeys.Control;

    /// <summary>Ctrl + wheel steps the zoom one notch per wheel click; null leaves the wheel to scrolling.</summary>
    public static int? WheelZoom(int delta, ModifierKeys modifiers, int current)
    {
        if (delta == 0 || modifiers != ModifierKeys.Control) return null;
        return FlyoutZoom.Adjust(current, increase: delta > 0);
    }
}
