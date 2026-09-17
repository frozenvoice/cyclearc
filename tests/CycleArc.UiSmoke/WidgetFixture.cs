using System.Windows;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>
/// Binds the production widget from synthetic accounts. A fixed work area keeps wrapping
/// deterministic whatever monitor the CI runner reports.
/// </summary>
internal static class WidgetFixture
{
    public static readonly IReadOnlyList<ScreenRect> Desktop = [new ScreenRect(0, 0, 1920, 1040)];

    public static void BindOne(FloatingWidget widget, CodexAccountView? account,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto) =>
        widget.BindAccounts(account is null ? [] : [account], account?.Profile.Id ?? "", preference, Desktop);

    public static void BindSnapshot(FloatingWidget widget, CodexQuotaSnapshot snapshot,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto) =>
        BindOne(widget, Synthetic("synthetic-widget", "Widget fixture", snapshot), preference);

    public static CodexAccountView Synthetic(string id, string label, CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile(id, "", label) { Provider = snapshot.Provider }, snapshot);

    public static WidgetAccountModuleView Module(FloatingWidget widget, int index = 0) => widget.Modules[index];

    public static string Status(FloatingWidget widget, int index = 0) => Module(widget, index).StatusText.Text;

    public static string RingValue(FloatingWidget widget, int index = 0) => Module(widget, index).RingValueText.Text;

    public static string Tooltip(FloatingWidget widget, int index = 0) => Module(widget, index).ToolTip as string ?? "";

    /// Renders the widget as currently arranged. A shown window is captured at its live
    /// RenderSize so a LastLayout-sized remesaure cannot hide HWND clipping.
    public static void RenderWidget(FloatingWidget widget, string? path)
    {
        var content = (FrameworkElement)widget.Content;
        if (widget.IsLoaded && content.RenderSize.Width > 0 && content.RenderSize.Height > 0)
        {
            AccountUiChecks.RenderCurrent(content, path);
            return;
        }
        AccountUiChecks.Render(widget, widget.LastLayout?.Width
            ?? content.DesiredSize.Width, null, path);
    }
}
