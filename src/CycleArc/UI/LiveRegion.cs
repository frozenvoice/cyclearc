using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace CycleArc.UI;

/// <summary>
/// Lets a screen reader hear status that changes without moving focus, such as a finished
/// refresh or an account operation's result. Only a changed name is announced, and only while
/// the element is on screen, so unchanged rebinds and hidden windows stay quiet.
/// </summary>
internal static class LiveRegion
{
    public static void Mark(FrameworkElement element) =>
        AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);

    /// <summary>
    /// Announces each new non-empty text of a status line until the returned action stops it.
    /// The property descriptor holds its handlers strongly, so the owner stops it on close.
    /// </summary>
    public static Action Follow(System.Windows.Controls.TextBlock status)
    {
        Mark(status);
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(System.Windows.Controls.TextBlock.TextProperty, typeof(System.Windows.Controls.TextBlock));
        EventHandler changed = (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(status.Text)) Raise(status);
        };
        descriptor.AddValueChanged(status, changed);
        return () => descriptor.RemoveValueChanged(status, changed);
    }

    private static void Raise(FrameworkElement element)
    {
        if (!element.IsVisible || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        (UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public static void Announce(FrameworkElement element, string? previousName)
    {
        if (string.Equals(previousName, AutomationProperties.GetName(element), StringComparison.Ordinal)) return;
        Raise(element);
    }
}
