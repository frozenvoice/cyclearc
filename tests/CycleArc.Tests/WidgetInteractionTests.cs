using System.Xml.Linq;
using CycleArc.Services;

namespace CycleArc.Tests;

public class WidgetInteractionTests
{
    [Fact]
    public void DragThreshold_SeparatesClickFromDrag()
    {
        Assert.True(WidgetInteraction.IsClick(0, 0, 2, 2));
        Assert.False(WidgetInteraction.IsDrag(0, 0, 2, 2));
        Assert.True(WidgetInteraction.IsDrag(0, 0, 10, 0));
        Assert.False(WidgetInteraction.IsClick(0, 0, 10, 1));
    }

    [Fact]
    public void EventSubscription_HappensOnlyOnce()
    {
        var binder = new OnceEventSubscription();
        var count = 0;
        Assert.True(binder.TrySubscribe(() => count++));
        Assert.False(binder.TrySubscribe(() => count++));
        Assert.Equal(1, count);
        Assert.Equal(1, binder.Count);
    }

    [Fact]
    public void FloatingWidgetXaml_UsesThemeResourcesAndHasNoExactQuotaPrototype()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FloatingWidget.xaml"));
        var xaml = document.ToString();
        Assert.Contains("CardBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("TextBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("MutedBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ProStateValue", xaml, StringComparison.Ordinal);
        Assert.Contains("OnPreviewLeftDown", xaml, StringComparison.Ordinal);
        Assert.Contains("OnMouseDown", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("31/50", xaml, StringComparison.Ordinal);
        var widgetCode = File.ReadAllText(Find("src/CycleArc/UI/FloatingWidget.xaml.cs"));
        Assert.Contains("FlyoutRequested", widgetCode, StringComparison.Ordinal);
        Assert.Contains("RefreshRequested", widgetCode, StringComparison.Ordinal);
        Assert.Contains("ContextMenuRequested", widgetCode, StringComparison.Ordinal);
        Assert.Contains("WidgetDragSession", widgetCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DragMove()", widgetCode, StringComparison.Ordinal);
        Assert.Contains("IsScrollChrome", widgetCode, StringComparison.Ordinal);
        Assert.Contains("ShouldBeginWindowDrag", widgetCode, StringComparison.Ordinal);
        Assert.Contains("MouseButton.Middle", widgetCode, StringComparison.Ordinal);
        var appCode = File.ReadAllText(Find("src/CycleArc/App.xaml.cs"));
        Assert.Contains("RefreshCodexAsync", appCode, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetConversation", appCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FloatingWidget_ShowsEveryAccountFromTheSharedOverview()
    {
        var widgetCode = File.ReadAllText(Find("src/CycleArc/UI/FloatingWidget.xaml.cs"));
        Assert.Contains("public void BindAccounts(", widgetCode, StringComparison.Ordinal);
        Assert.Contains("public void Relayout(", widgetCode, StringComparison.Ordinal);
        Assert.Contains("RecoverInto(", widgetCode, StringComparison.Ordinal);
        Assert.Contains("ApplyArrangedLayout()", widgetCode, StringComparison.Ordinal);
        Assert.Contains("WidgetAccountModel.All(", widgetCode, StringComparison.Ordinal);
        Assert.Contains("WidgetGridLayout.For(", widgetCode, StringComparison.Ordinal);
        // The widget must not build a usage source of its own.
        Assert.DoesNotContain("HttpClient", widgetCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", widgetCode, StringComparison.Ordinal);

        var controller = File.ReadAllText(Find("src/CycleArc/Services/FloatingWidgetController.cs"));
        Assert.Contains("BindAccounts(overview.Accounts, overview.SelectedId, overview.Preference)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("overview.Selected,", controller, StringComparison.Ordinal);

        var appCode = File.ReadAllText(Find("src/CycleArc/App.xaml.cs"));
        Assert.Contains("widget.AccountSelected += id => _codex.Select(id)", appCode, StringComparison.Ordinal);
        Assert.Contains("widget.FlyoutRequested += ShowMain", appCode, StringComparison.Ordinal);
        Assert.Contains("widget.SettingsRequested += ShowSettings", appCode, StringComparison.Ordinal);
        Assert.Contains("widget.CloseRequested += CloseWidget", appCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FloatingWidgetXaml_HasOneHeaderAndAWrappingModuleHost()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FloatingWidget.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string?[] Named(string element) => document.Descendants(ns + element)
            .Select(e => (string?)e.Attribute(x + "Name")).ToArray();

        Assert.Single(Named("Grid"), name => name == "WidgetHeader");
        Assert.Single(Named("Grid"), name => name == "ModuleHost");
        var scroller = document.Descendants(ns + "ScrollViewer")
            .Single(e => (string?)e.Attribute(x + "Name") == "ModuleScroller");
        Assert.Equal("Auto", (string?)scroller.Attribute("VerticalScrollBarVisibility"));
        // Horizontal scrolling would hide accounts instead of wrapping them.
        Assert.Equal("Disabled", (string?)scroller.Attribute("HorizontalScrollBarVisibility"));
        Assert.Contains("WidgetSettingsButton", document.ToString(), StringComparison.Ordinal);
        Assert.Contains("WidgetCloseButton", document.ToString(), StringComparison.Ordinal);
        // No sample account is baked into the markup; modules come from the bound accounts.
        Assert.DoesNotContain("ImageBrush", document.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("WebView", document.ToString(), StringComparison.Ordinal);
        foreach (var name in new[] { "메인", "카카오", "frozenvoice" })
            Assert.DoesNotContain(name, document.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsXaml_RemovesChatControlsAndKeepsSurfaceToggles()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "SettingsWindow.xaml"));
        var xaml = document.ToString();
        Assert.DoesNotContain("RECONSTRUCTION WINDOW", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskbarStatusBox", xaml, StringComparison.Ordinal);
        Assert.Contains("TrayHint", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetOpacityBox", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetClickThroughBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RESET ANCHOR", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FlyoutXaml_HasCodexRowsOnly()
    {
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FlyoutWindow.xaml")).ToString();
        Assert.Contains("CodexRows", xaml, StringComparison.Ordinal);
        Assert.Contains("CodexStatusText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ProStateText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmedRequestsText", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetCloseMenu_HidesAndPersistsWithoutExitingApp()
    {
        var tray = File.ReadAllText(Find("src/CycleArc/UI/TrayController.cs"));
        var app = File.ReadAllText(Find("src/CycleArc/App.xaml.cs"));
        Assert.Contains("CloseWidgetRequested?.Invoke()", tray);
        Assert.Contains("위젯 닫기", tray);
        Assert.Contains("widget.ContextMenuRequested += () => _tray.ShowWidgetContextMenu()", app);
        Assert.Contains("_tray.CloseWidgetRequested += CloseWidget", app);
        var start = app.IndexOf("private void CloseWidget()", StringComparison.Ordinal);
        var close = app[start..app.IndexOf("private void ApplyWidget()", start, StringComparison.Ordinal)];
        Assert.Contains("_settings.FloatingWidgetEnabled = false", close);
        Assert.Contains("_settingsStore.Save(_settings)", close);
        Assert.Contains("ApplyWidget()", close);
        Assert.DoesNotContain("ExitApp", close);
        Assert.DoesNotContain("Shutdown", close);
    }

    [Fact]
    public void CreditHelp_TogglesAndReusesItsPopupAcrossRefresh()
    {
        var code = File.ReadAllText(Find("src/CycleArc/UI/FlyoutWindow.xaml.cs"));
        Assert.Contains("tip.IsOpen = !tip.IsOpen", code);
        Assert.Contains("_creditHelpTip ??= MakeTooltip(helpText)", code);
        Assert.Contains("ToolTipService.SetIsEnabled(CreditHelpButton, false)", code);
        Assert.Contains("if (!IsVisible && _creditHelpTip is not null) _creditHelpTip.IsOpen = false", code);
        Assert.DoesNotContain("CreditHelpButton.ToolTip = MakeTooltip(", code);
    }

    [Fact]
    public void CreditCard_UsesCountBadgeAndCollapsibleBoundedList()
    {
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FlyoutWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.DoesNotContain("CodexLegendPanel", doc.ToString());
        var badge = doc.Descendants(ns + "Button").Single(e => (string?)e.Attribute(x + "Name") == "CreditHelpButton");
        Assert.Contains("ResetCreditsCount", badge.ToString());
        var expand = doc.Descendants(ns + "Button").Single(e => (string?)e.Attribute(x + "Name") == "CreditExpandButton");
        Assert.Equal("OnCreditExpandClick", (string?)expand.Attribute("Click"));
        var list = doc.Descendants(ns + "ScrollViewer").Single(e => (string?)e.Attribute(x + "Name") == "CreditExpiryScroll");
        Assert.Equal("108", (string?)list.Attribute("Height"));
        Assert.Equal("Auto", (string?)list.Attribute("VerticalScrollBarVisibility"));
    }

    [Fact]
    public void HeaderLogo_ScalesItsFullDrawingWithoutClippingTheStroke()
    {
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FlyoutWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var logo = doc.Descendants(ns + "Path").Single(e => (string?)e.Attribute(x + "Name") == "HeaderLogoPath");
        var canvas = logo.Parent!;
        var viewbox = canvas.Parent!;
        Assert.Equal(ns + "Canvas", canvas.Name);
        Assert.Equal(ns + "Viewbox", viewbox.Name);
        Assert.Equal("Uniform", (string?)viewbox.Attribute("Stretch"));
        // This arc with its 5 DIP round stroke reaches y=22.468, beyond the old 20 DIP grid.
        Assert.True((double)canvas.Attribute("Width")! >= 24);
        Assert.True((double)canvas.Attribute("Height")! >= 24);
        Assert.Equal("20", (string?)viewbox.Attribute("Width"));
        Assert.Equal("20", (string?)viewbox.Attribute("Height"));
    }
    private static string Find(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
