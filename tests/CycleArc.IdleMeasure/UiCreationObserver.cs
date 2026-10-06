using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.IdleMeasure;

/// <summary>Cumulative count of displayed UI objects that were not displayed at the previous observation.</summary>
internal sealed record UiCreationCounts(long Observations, long AccountRows, long RowAvatars, long SelectedAvatars,
    long WidgetAvatars, long FlyoutDetailRows, long TrayIcons)
{
    public UiCreationCounts Minus(UiCreationCounts before) => new(Observations - before.Observations,
        AccountRows - before.AccountRows, RowAvatars - before.RowAvatars, SelectedAvatars - before.SelectedAvatars,
        WidgetAvatars - before.WidgetAvatars, FlyoutDetailRows - before.FlyoutDetailRows, TrayIcons - before.TrayIcons);
}

// Harness-only: after each fixture call into production presentation, compare the objects
// now displayed with the previous observation by reference. Only the current generation is
// held, which production already keeps alive, so the observer itself retains no old views.
internal sealed class UiCreationObserver(Func<FlyoutWindow?> flyout, Func<FloatingWidgetController?> widget,
    Func<TrayController?> tray)
{
    private static readonly FieldInfo TrayIconField = typeof(TrayController).GetField("_current",
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(nameof(TrayController), "_current");
    private static readonly FieldInfo ModuleAvatarField = typeof(WidgetAccountModuleView).GetField("_avatarHost",
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(nameof(WidgetAccountModuleView), "_avatarHost");
    private static readonly string[] DetailLists = ["CodexRows", "CodexSecondaryRows", "UsageCreditsRows", "CreditExpiryRows"];
    private readonly Generation _rows = new(), _rowAvatars = new(), _selectedAvatars = new(), _widgetAvatars = new(),
        _detailRows = new(), _trayIcons = new();
    private long _observations;

    public UiCreationCounts Counts => new(_observations, _rows.Created, _rowAvatars.Created,
        _selectedAvatars.Created, _widgetAvatars.Created, _detailRows.Created, _trayIcons.Created);

    public void Observe()
    {
        _observations++;
        var popup = flyout();
        var rows = popup?.FindName("AccountOverview") is ItemsControl overview ? overview.Items.Cast<object>().ToArray() : [];
        _rows.Replace(rows);
        _rowAvatars.Replace(rows.OfType<DependencyObject>().SelectMany(Avatars).ToArray());
        _selectedAvatars.Replace(popup?.FindName("SelectedAvatarHost") is ContentControl { Content: { } avatar } ? [avatar] : []);
        _detailRows.Replace(popup is null ? [] : DetailLists.SelectMany(name =>
            popup.FindName(name) is ItemsControl list ? list.Items.Cast<object>() : []).ToArray());
        _widgetAvatars.Replace(widget()?.CurrentWindow?.Modules
            .Select(module => ((Border)ModuleAvatarField.GetValue(module)!).Child).OfType<object>().ToArray() ?? []);
        _trayIcons.Replace(tray() is { } controller && TrayIconField.GetValue(controller) is { } icon ? [icon] : []);
    }

    private static IEnumerable<object> Avatars(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is FrameworkElement { Tag: "AccountAvatar" }) yield return child;
            foreach (var nested in Avatars(child)) yield return nested;
        }
    }

    private sealed class Generation
    {
        private HashSet<object> _current = new(ReferenceEqualityComparer.Instance);
        public long Created { get; private set; }

        public void Replace(object[] displayed)
        {
            var next = new HashSet<object>(displayed, ReferenceEqualityComparer.Instance);
            foreach (var item in next) if (!_current.Contains(item)) Created++;
            _current = next;
        }
    }
}
