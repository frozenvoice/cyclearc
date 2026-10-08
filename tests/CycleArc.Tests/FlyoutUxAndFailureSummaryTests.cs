using System.Xml.Linq;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Tests;

public class FlyoutUxAndFailureSummaryTests
{
    [Fact]
    public void TaskbarStrip_DoesNotAssignHoverToolTip()
    {
        var source = File.ReadAllText(Find("src/CycleArc/UI/TaskbarStatusStripWindow.xaml.cs"));
        var xaml = File.ReadAllText(Find("src/CycleArc/UI/TaskbarStatusStripWindow.xaml"));
        Assert.DoesNotContain("ToolTip =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTipService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip", xaml, StringComparison.Ordinal);
        Assert.Contains("FlyoutRequested", source, StringComparison.Ordinal);
        Assert.Contains("RefreshRequested", source, StringComparison.Ordinal);
        Assert.Contains("ContextMenuRequested", source, StringComparison.Ordinal);
        Assert.Contains("ReassertTopmost", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FlyoutPinPolicy_PinMeansTopmostOnly_AndNeverHidesOnDeactivate()
    {
        Assert.False(AppSettings.CreateDefaults().FlyoutPinned);
        Assert.False(AppSettings.CreateDefaults().FlyoutPositionConfigured);
        Assert.False(FlyoutWindowState.HidesOnDeactivate);
        Assert.False(FlyoutWindowState.IsTopmost(false));
        Assert.True(FlyoutWindowState.IsTopmost(true));
        Assert.True(FlyoutWindowState.AllowsHeaderDrag);
        Assert.True(FlyoutWindowState.UseSavedPosition(true));
        Assert.False(FlyoutWindowState.UseSavedPosition(false));
        Assert.False(FlyoutWindowState.RepositionNearAnchorOnUnpin);
    }

    [Fact]
    public void SavedPosition_IsIndependentOfPin_AndUnpinDoesNotReposition()
    {
        Assert.True(FlyoutWindowState.UseSavedPosition(positionConfigured: true));
        Assert.False(FlyoutWindowState.UseSavedPosition(positionConfigured: false));
        Assert.False(FlyoutWindowState.RepositionNearAnchorOnUnpin);

        var app = File.ReadAllText(Find("src/CycleArc/App.xaml.cs"));
        Assert.Contains("UseSavedPosition(_settings.FlyoutPositionConfigured)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("UseSavedPosition(_settings.FlyoutPinned", app, StringComparison.Ordinal);
        var pinChanged = Slice(app, "_flyout.PinChanged", "_flyout.PositionChanged");
        Assert.Contains("FlyoutPinned", pinChanged, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceFlyout", pinChanged, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutLeft", pinChanged, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutTop", pinChanged, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutPositionConfigured", pinChanged, StringComparison.Ordinal);
        Assert.DoesNotContain("RepositionNearAnchorOnUnpin", pinChanged, StringComparison.Ordinal);
        var positionChanged = Slice(app, "_flyout.PositionChanged", "private void PlaceFlyout");
        Assert.Contains("FlyoutLeft", positionChanged, StringComparison.Ordinal);
        Assert.Contains("FlyoutTop", positionChanged, StringComparison.Ordinal);
        Assert.Contains("FlyoutPositionConfigured = true", positionChanged, StringComparison.Ordinal);
        var place = Slice(app, "private void PlaceFlyout", "private void OpenLogs");
        Assert.Contains("UseSavedPosition(_settings.FlyoutPositionConfigured)", place, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutPinned", place, StringComparison.Ordinal);
        Assert.Contains("PlaceNearTaskbar", place, StringComparison.Ordinal);
        Assert.Contains("RestorePosition", place, StringComparison.Ordinal);
    }

    [Fact]
    public void Flyout_DoesNotHideOnDeactivation_AndHeaderActionsStayIndependent()
    {
        var xaml = File.ReadAllText(Find("src/CycleArc/UI/FlyoutWindow.xaml"));
        var code = File.ReadAllText(Find("src/CycleArc/UI/FlyoutWindow.xaml.cs"));
        var app = File.ReadAllText(Find("src/CycleArc/App.xaml.cs"));
        Assert.False(FlyoutWindowState.HidesOnDeactivate);
        Assert.DoesNotContain("Deactivated=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnDeactivated", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnDeactivated", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseOnDeactivate", code, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutCloseOnDeactivate", code, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutCloseOnDeactivate", app, StringComparison.Ordinal);
        Assert.DoesNotContain("_suppressDeactivateClose", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnHeaderButtonPreviewMouseDown", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnHeaderButtonPreviewMouseDown", code, StringComparison.Ordinal);
        Assert.Contains("OnRefreshAllClick", code, StringComparison.Ordinal);
        Assert.Contains("OnPinClick", code, StringComparison.Ordinal);
        Assert.Contains("OnCloseClick", code, StringComparison.Ordinal);
        Assert.Contains("DragMove()", code, StringComparison.Ordinal);
        Assert.Contains("if (e.Key == Key.Escape)", code, StringComparison.Ordinal);
        var escape = Slice(code, "private void OnPreviewKeyDown", "private void OnRefreshAllClick");
        Assert.Contains("Hide();", escape, StringComparison.Ordinal);
        Assert.DoesNotContain("Shutdown", escape, StringComparison.Ordinal);
    }

    [Fact]
    public void OffScreenSavedPosition_IsRecoveredToWorkArea()
    {
        var work = new ScreenRect(0, 0, 1920, 1040);
        var clamped = FlyoutPlacement.ClampToWorkArea(5000, -400, 320, 400, work);
        Assert.InRange(clamped.Left, 8, 1920 - 328);
        Assert.InRange(clamped.Top, 8, 1040 - 408);
        var selected = FlyoutPlacement.SelectWorkArea(
            5000,
            5000,
            320,
            400,
            [work, new ScreenRect(1920, 0, 1280, 1024)]);
        Assert.Equal(work, selected);
        var onSecond = FlyoutPlacement.SelectWorkArea(
            2000,
            40,
            320,
            400,
            [work, new ScreenRect(1920, 0, 1280, 1024)]);
        Assert.Equal(1920, onSecond.X);
        // A sliver over the primary must not pull a window that sits mostly on the secondary.
        var straddling = FlyoutPlacement.SelectWorkArea(
            1900,
            40,
            320,
            400,
            [work, new ScreenRect(1920, 0, 1280, 1024)]);
        Assert.Equal(1920, straddling.X);
    }

    [Fact]
    public void FlyoutXaml_HasPinCloseSpinnerAndNoProgressStrip()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FlyoutWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var xaml = document.ToString();
        Assert.Contains("x:Name=\"TitleText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WindowOptionsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PinMenuItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CloseFlyoutMenuItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CloseButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AccentBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgressBar", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncProgressStrip", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("📌", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("📍", xaml, StringComparison.Ordinal);
        var spinner = document.Descendants(ns + "Viewbox")
            .Single(element => (string?)element.Attribute(x + "Name") == "RefreshSpinner");
        Assert.Equal("Collapsed", (string?)spinner.Attribute("Visibility"));
        var close = document.Descendants(ns + "MenuItem")
            .Single(element => (string?)element.Attribute(x + "Name") == "CloseFlyoutMenuItem");
        Assert.Equal("OnCloseClick", (string?)close.Attribute("Click"));
        var pin = document.Descendants(ns + "MenuItem")
            .Single(element => (string?)element.Attribute(x + "Name") == "PinMenuItem");
        Assert.Equal("OnPinClick", (string?)pin.Attribute("Click"));
        Assert.Contains("OnHeaderMouseLeftButtonDown", xaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Topmost=\"False\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Deactivated=", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FlyoutCode_CloseHidesWithoutExit_AndDragWorksPinnedOrUnpinned()
    {
        var code = File.ReadAllText(Find("src/CycleArc/UI/FlyoutWindow.xaml.cs"));
        var app = File.ReadAllText(Find("src/CycleArc/App.xaml.cs"));
        var close = Slice(code, "private void OnCloseClick", "private void OnHeaderMouseLeftButtonDown");
        Assert.Contains("Hide();", close, StringComparison.Ordinal);
        Assert.DoesNotContain("Shutdown", close, StringComparison.Ordinal);
        Assert.DoesNotContain("Close();", close, StringComparison.Ordinal);
        Assert.DoesNotContain("Pinned = false", close, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutPinned", close, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutLeft", close, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutTop", close, StringComparison.Ordinal);
        Assert.Contains("DragMove()", code, StringComparison.Ordinal);
        Assert.Contains("HeaderSourceIsInteractive", code, StringComparison.Ordinal);
        var drag = Slice(code, "private void OnHeaderMouseLeftButtonDown", "private void PersistPosition");
        Assert.Contains("FlyoutWindowState.AllowsHeaderDrag", drag, StringComparison.Ordinal);
        Assert.Contains("DragMove()", drag, StringComparison.Ordinal);
        Assert.Contains("PersistPosition()", drag, StringComparison.Ordinal);
        Assert.DoesNotContain("Pinned", drag, StringComparison.Ordinal);
        var interactive = Slice(code, "private bool HeaderSourceIsInteractive", "private IReadOnlyList<ScreenRect> EnumerateWorkAreas");
        Assert.Contains("RefreshAllButton", interactive, StringComparison.Ordinal);
        Assert.Contains("WindowOptionsButton", interactive, StringComparison.Ordinal);
        Assert.Contains("source is System.Windows.Controls.Button", interactive, StringComparison.Ordinal);
        Assert.DoesNotContain("_suppressDeactivateClose", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnHeaderButtonPreviewMouseDown", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnDeactivated", code, StringComparison.Ordinal);
        var refresh = Slice(code, "private void OnRefreshAllClick", "private void OnPinClick");
        Assert.Contains("SyncRequested", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("Topmost", refresh, StringComparison.Ordinal);
        var pin = Slice(code, "private void OnPinClick", "private void OnCloseClick");
        Assert.Contains("Topmost = FlyoutWindowState.IsTopmost(Pinned)", pin, StringComparison.Ordinal);
        Assert.Contains("PinChanged?.Invoke(Pinned)", pin, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistPosition", pin, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseOnDeactivate", pin, StringComparison.Ordinal);
        Assert.Contains("Topmost = FlyoutWindowState.IsTopmost(Pinned)", Slice(code, "public void ApplyWindowSettings", "public void RestorePosition"), StringComparison.Ordinal);
        Assert.Contains("ApplyAnimationClock", code, StringComparison.Ordinal);
        Assert.Contains("RotateTransform.AngleProperty", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TranslateTransform.XProperty", code, StringComparison.Ordinal);
        Assert.Contains("HandoffBehavior.SnapshotAndReplace", code, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureRefreshStoryboard", code, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureProgressStripStoryboard", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard.SetTarget", code, StringComparison.Ordinal);
        Assert.Contains("SetRefreshing", code, StringComparison.Ordinal);
        Assert.Contains("ContentRendered", code, StringComparison.Ordinal);
        Assert.Contains("IsVisibleChanged", code, StringComparison.Ordinal);
        Assert.Contains("RepeatBehavior.Forever", code, StringComparison.Ordinal);
        Assert.Contains("FlyoutPinned", app, StringComparison.Ordinal);
        Assert.Contains("FlyoutPositionConfigured", app, StringComparison.Ordinal);
        Assert.Contains("UseSavedPosition", app, StringComparison.Ordinal);
        var settingsUi = File.ReadAllText(Find("src/CycleArc/UI/SettingsWindow.xaml"));
        Assert.DoesNotContain("FlyoutLeft", settingsUi, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutPinned", settingsUi, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutCloseBox", settingsUi, StringComparison.Ordinal);
        Assert.DoesNotContain("Close flyout when it loses focus", settingsUi, StringComparison.Ordinal);
        var settingsCode = File.ReadAllText(Find("src/CycleArc/UI/SettingsWindow.xaml.cs"));
        Assert.DoesNotContain("FlyoutCloseOnDeactivate", settingsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutCloseBox", settingsCode, StringComparison.Ordinal);
        var settingsApp = File.ReadAllText(Find("src/CycleArc.Core/Services/SettingsApplication.cs"));
        Assert.DoesNotContain("FlyoutLeft", settingsApp, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutPinned", settingsApp, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutCloseOnDeactivate", settingsApp, StringComparison.Ordinal);
        var settingsModel = File.ReadAllText(Find("src/CycleArc.Core/Models/AppSettings.cs"));
        Assert.Contains("FlyoutCloseOnDeactivate", settingsModel, StringComparison.Ordinal);
        Assert.Contains("Deprecated", settingsModel, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageWindow_DefaultViewHidesParserDiagnostics()
    {
        var xaml = File.ReadAllText(Find("src/CycleArc/UI/CoverageWindow.xaml"));
        var code = File.ReadAllText(Find("src/CycleArc/UI/CoverageWindow.xaml.cs"));
        Assert.Contains("AdvancedExpander", xaml, StringComparison.Ordinal);
        Assert.Contains("AdvancedPanel", xaml, StringComparison.Ordinal);
        Assert.Contains("IsExpanded=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DataStatusPresentation.From", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CoverageCompactLabel", code, StringComparison.Ordinal);
        Assert.DoesNotContain("conversation_id", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnglishFailureSummary_OmitsTechnicalNoise()
    {
        var coverage = new CoverageInfo
        {
            FailedConversations = 3,
            ConversationIncomplete = true,
            NormalIndexState = CollectionState.Complete,
            ArchivedIndexState = CollectionState.Complete,
            ProjectsIndexState = CollectionState.Complete,
            FailureSummary =
            {
                FailedThisSyncCount = 3,
                BodyTimeoutCount = 1,
                SchemaMismatchCount = 2
            }
        };
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("3 conversations not read", DisplayFormatting.CoverageCompactLabel(coverage));
            var details = string.Join('\n', DisplayFormatting.CoverageFailureDetailLines(coverage));
            Assert.Contains("Read timeout    1", details, StringComparison.Ordinal);
            Assert.Contains("Response format mismatch    2", details, StringComparison.Ordinal);
            Assert.Contains("Failed this sync    3", details, StringComparison.Ordinal);
            Assert.Contains("lower bound", details, StringComparison.Ordinal);
            Assert.DoesNotContain("SchemaMismatch", details, StringComparison.Ordinal);
            Assert.DoesNotContain("BodyTimeout", details, StringComparison.Ordinal);
            Assert.DoesNotContain("generation", details, StringComparison.Ordinal);
            Assert.DoesNotContain("HTTP status=0", details, StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void PayloadTooLarge_UsesSizeLabelNotSchemaMismatch()
    {
        var coverage = new CoverageInfo
        {
            FailedConversations = 1,
            ConversationIncomplete = true,
            FailureSummary =
            {
                FailedThisSyncCount = 1,
                PayloadTooLargeCount = 1
            }
        };
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var details = string.Join('\n', DisplayFormatting.CoverageFailureDetailLines(coverage));
            Assert.Contains("응답 크기 초과    1", details, StringComparison.Ordinal);
            Assert.DoesNotContain("응답 형식 불일치", details, StringComparison.Ordinal);
            Assert.DoesNotContain("SchemaMismatch", details, StringComparison.Ordinal);
            Assert.Equal("응답 크기 초과", DisplayFormatting.FailureCategoryLabel("PayloadTooLarge"));
            Assert.Equal("응답 크기 초과", DisplayFormatting.FailureCategoryLabel("ResponseTooLarge"));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }

        Assert.Equal("Response too large", DisplayFormatting.FailureCategoryLabel(ConversationFetchBackoff.PayloadTooLarge));
        Assert.Equal(ConversationFetchBackoff.PayloadTooLarge, ConversationFetchBackoff.NormalizeCategory("ResponseTooLarge"));
    }

    [Fact]
    public void GlobalIndexFailure_DoesNotInventConversationCounts()
    {
        var coverage = new CoverageInfo
        {
            IndexIncomplete = true,
            NormalIndexState = CollectionState.Failed,
            FailedConversations = 0
        };
        Assert.False(coverage.FailureSummary.HasConversationFailures);
        Assert.Empty(DisplayFormatting.CoverageFailureDetailLines(coverage));
        Assert.Equal(UiText.Partial, DisplayFormatting.CoverageCompactLabel(coverage));
        Assert.DoesNotContain("conversations not read", DisplayFormatting.CoverageCompactLabel(coverage), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshIndicator_IdleActiveHiddenAndResume()
    {
        var controller = new RefreshIndicatorController();
        Assert.False(controller.IsAnimating);
        Assert.Equal(RefreshIndicatorTransition.Started, controller.Apply(true));
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(true));
        Assert.Equal(RefreshIndicatorTransition.Stopped, controller.Reset());
        Assert.Equal(RefreshIndicatorTransition.Started, controller.Apply(true));
        Assert.Equal(RefreshIndicatorTransition.Stopped, controller.Apply(false));
        Assert.False(controller.IsAnimating);
        Assert.Equal(0, controller.Angle);
        var active = CombinedRefreshCoordinator.Present(true, true, combinedManual: true);
        Assert.True(active.Active);
        Assert.Equal(UiText.RefreshAllProgress, active.ProgressText);
        var idle = CombinedRefreshCoordinator.Present(false, false);
        Assert.False(idle.Active);
        Assert.True(idle.ShowNormalStatus);
        var hiddenRefreshing = FlyoutRefreshVisualState.Create(true, false);
        Assert.False(hiddenRefreshing.RunAnimation);
        Assert.False(hiddenRefreshing.SpinnerVisible);
        var shownRefreshing = FlyoutRefreshVisualState.Create(true, true);
        Assert.True(shownRefreshing.RunAnimation);
        Assert.True(shownRefreshing.SpinnerVisible);
        Assert.False(shownRefreshing.IdleIconVisible);
    }

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, end);
        return source[from..to];
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
