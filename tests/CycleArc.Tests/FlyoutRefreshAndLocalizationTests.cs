using System.Xml.Linq;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;

namespace CycleArc.Tests;

public class FlyoutRefreshAndLocalizationTests
{
    [Fact]
    public void RefreshButton_ExistsWithLocalizedNameAndThemeBrushes()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FlyoutWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = document.Descendants(ns + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "RefreshAllButton");
        Assert.Equal("OnRefreshAllClick", (string?)button.Attribute("Click"));
        Assert.Contains("Refresh all", (string?)button.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"))
            ?? (string?)button.Attribute("ToolTip"), StringComparison.OrdinalIgnoreCase);
        var style = document.Descendants(ns + "Style")
            .Single(element => (string?)element.Attribute(x + "Key") == "FlyoutHeaderIconButton");
        var xaml = style.ToString() + button.ToString();
        Assert.Contains("TextBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("GhostBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("AccentBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("DisabledBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name", document.ToString(), StringComparison.Ordinal);
        Assert.Null(button.Attribute("Visibility"));
        Assert.Contains("ResetCreditsCard", document.ToString(), StringComparison.Ordinal);
        Assert.Contains("CodexRows", document.ToString(), StringComparison.Ordinal);
        Assert.Contains("RefreshProgressText", document.ToString(), StringComparison.Ordinal);
        var icon = document.Descendants(ns + "Path")
            .Single(element => (string?)element.Attribute(x + "Name") == "RefreshAllIcon");
        Assert.NotNull(icon.Attribute("Data"));
        Assert.DoesNotContain("RotateTransform", icon.ToString(), StringComparison.Ordinal);
        var spinner = document.Descendants(ns + "Viewbox")
            .Single(element => (string?)element.Attribute(x + "Name") == "RefreshSpinner");
        Assert.Equal("Collapsed", (string?)spinner.Attribute("Visibility"));
        Assert.Equal("0.5,0.5", (string?)spinner.Attribute("RenderTransformOrigin"));
        Assert.Contains("RefreshSpinnerRotate", spinner.ToString(), StringComparison.Ordinal);
        Assert.Contains("ArcSegment", spinner.ToString(), StringComparison.Ordinal);
        Assert.Contains("IsLargeArc=\"True\"", spinner.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("IsMouseOver", icon.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard", button.ToString(), StringComparison.Ordinal);
        var flyoutCode = File.ReadAllText(Find("src/CycleArc/UI/FlyoutWindow.xaml.cs"));
        Assert.Contains("RefreshIndicatorController", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("RepeatBehavior.Forever", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("RefreshSpinnerRotate", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("BeginAnimation", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("HandoffBehavior.SnapshotAndReplace", flyoutCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard.SetTarget", flyoutCode, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureRefreshStoryboard", flyoutCode, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshAllRotate", flyoutCode, StringComparison.Ordinal);
        Assert.DoesNotContain("EasingFunction", flyoutCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_suppressDeactivateClose", flyoutCode, StringComparison.Ordinal);
        Assert.DoesNotContain("OnDeactivated", flyoutCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseOnDeactivate", flyoutCode, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshPresentation_DisablesDuringCombinedManual_AndKeepsProviderText()
    {
        var idle = CombinedRefreshCoordinator.Present(false, false);
        Assert.True(idle.Enabled);
        Assert.False(idle.Active);
        Assert.Equal("", idle.ProgressText);
        Assert.True(idle.ShowNormalStatus);
        Assert.False(idle.ShowRefreshProgress);

        var autoCodex = CombinedRefreshCoordinator.Present(false, true);
        Assert.True(autoCodex.Enabled);
        Assert.True(autoCodex.Active);
        Assert.False(autoCodex.ShowNormalStatus);
        Assert.True(autoCodex.ShowRefreshProgress);

        var bothBackground = CombinedRefreshCoordinator.Present(true, true);
        Assert.False(bothBackground.Enabled);
        Assert.True(bothBackground.Active);
        Assert.False(bothBackground.ShowNormalStatus);
        Assert.True(bothBackground.ShowRefreshProgress);

        var manual = CombinedRefreshCoordinator.Present(true, true, combinedManual: true);
        Assert.False(manual.Enabled);
        Assert.True(manual.Active);
        Assert.Equal(UiText.RefreshAllProgress, manual.ProgressText);
        Assert.False(manual.ShowNormalStatus);
        Assert.True(manual.ShowRefreshProgress);

        var restored = CombinedRefreshCoordinator.Present(false, false);
        Assert.True(restored.Enabled);
        Assert.False(restored.Active);
        Assert.True(restored.ShowNormalStatus);
        Assert.False(restored.ShowRefreshProgress);
    }

    [Fact]
    public void FlyoutHeader_ShowsExactlyOneActiveRefreshLabel()
    {
        var idle = CombinedRefreshCoordinator.Present(false, false);
        Assert.True(idle.ShowNormalStatus);
        Assert.False(idle.ShowRefreshProgress);

        var chatgpt = CombinedRefreshCoordinator.Present(true, false);
        Assert.False(chatgpt.ShowNormalStatus);
        Assert.True(chatgpt.ShowRefreshProgress);

        var combined = CombinedRefreshCoordinator.Present(true, true, combinedManual: true);
        Assert.False(combined.ShowNormalStatus);
        Assert.True(combined.ShowRefreshProgress);

        var done = CombinedRefreshCoordinator.Present(false, false);
        Assert.True(done.ShowNormalStatus);
        Assert.False(done.ShowRefreshProgress);

        var flyoutCode = File.ReadAllText(Find("src/CycleArc/UI/FlyoutWindow.xaml.cs"));
        Assert.Contains("StatusText.Visibility = presentation.ShowNormalStatus", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("RefreshProgressText.Visibility = presentation.ShowRefreshProgress", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("SetRefreshing(presentation.Active)", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("RefreshAllIcon.Visibility", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("RefreshSpinner.Visibility", flyoutCode, StringComparison.Ordinal);
        var xaml = File.ReadAllText(Find("src/CycleArc/UI/FlyoutWindow.xaml"));
        Assert.DoesNotContain("Margin=\"0,0,-", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FlyoutHeaderGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TitleText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"NoWrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"*\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshAllRotate", xaml, StringComparison.Ordinal);
        Assert.Contains("RefreshSpinnerRotate", xaml, StringComparison.Ordinal);
        Assert.Contains("IsLargeArc=\"True\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FlyoutHeader_ProtectsTitleAndTrimsLongStatus()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FlyoutWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var header = document.Descendants(ns + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "FlyoutHeaderGrid");
        var columns = header.Element(ns + "Grid.ColumnDefinitions")!
            .Elements(ns + "ColumnDefinition")
            .Select(column => (string?)column.Attribute("Width"))
            .ToArray();
        Assert.Equal("Auto", columns[0]);
        Assert.Equal("*", columns[1]);
        Assert.Equal("Auto", columns[2]);
        Assert.Equal("Auto", columns[3]);
        Assert.Equal("Auto", columns[4]);
        Assert.Equal("Auto", columns[5]);
        Assert.Equal(6, columns.Length);
        var title = header.Descendants(ns + "TextBlock")
            .Single(element => (string?)element.Attribute(x + "Name") == "TitleText");
        var titleHost = title.Ancestors().Single(element => element.Parent == header);
        Assert.Equal("0", (string?)titleHost.Attribute("Grid.Column"));
        Assert.Equal("{x:Static text:UiText.ProductName}", (string?)title.Attribute("Text") ?? title.Value.Trim());
        var statusHost = header.Elements(ns + "Grid").Single();
        Assert.Equal("1", (string?)statusHost.Attribute("Grid.Column"));
        var status = statusHost.Descendants(ns + "TextBlock")
            .Single(element => (string?)element.Attribute(x + "Name") == "StatusText");
        var progress = statusHost.Descendants(ns + "TextBlock")
            .Single(element => (string?)element.Attribute(x + "Name") == "RefreshProgressText");
        Assert.Equal("CharacterEllipsis", (string?)status.Attribute("TextTrimming"));
        Assert.Equal("CharacterEllipsis", (string?)progress.Attribute("TextTrimming"));
        Assert.Equal("NoWrap", (string?)status.Attribute("TextWrapping"));
        var button = header.Descendants(ns + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "RefreshAllButton");
        Assert.Equal("2", (string?)button.Attribute("Grid.Column"));
        var pin = header.Descendants(ns + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "PinButton");
        var close = header.Descendants(ns + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "CloseFlyoutButton");
        Assert.Equal("4", (string?)pin.Attribute("Grid.Column"));
        Assert.Equal("5", (string?)close.Attribute("Grid.Column"));
        var settings = header.Descendants(ns + "Button").Single(element => (string?)element.Attribute(x + "Name") == "SettingsButton");
        Assert.Equal("3", (string?)settings.Attribute("Grid.Column"));
        Assert.Equal("OnSettingsClick", (string?)settings.Attribute("Click"));
        Assert.NotEqual((string?)title.Attribute("Grid.Column"), (string?)statusHost.Attribute("Grid.Column"));
        Assert.NotEqual((string?)button.Attribute("Grid.Column"), (string?)statusHost.Attribute("Grid.Column"));
    }

    [Fact]
    public async Task CombinedRefresh_KeepsIndependentResults_AndAlwaysReleasesBusy()
    {
        var coordinator = new CombinedRefreshCoordinator(
            (_, _) => Task.FromResult(new SyncOutcome(AppSyncStatus.UpToDate, null, 3)),
            _ => Task.FromResult(new CodexRefreshResult(
                CodexQuotaSnapshot.Empty(CodexQuotaStatus.CodexNotFound, "missing"),
                false,
                "codex-not-found")));
        CombinedRefreshResult? observed = null;
        try
        {
            observed = await coordinator.RefreshAllAsync(true, CancellationToken.None);
            Assert.Equal(AppSyncStatus.UpToDate, observed.ChatGpt?.Status);
            Assert.Equal(CodexQuotaStatus.CodexNotFound, observed.Codex.Snapshot.Status);
            Assert.True(observed.PartialFailure);
            Assert.False(observed.TotalFailure);
        }
        finally
        {
            Assert.False(coordinator.ChatGptRefreshing);
            Assert.False(coordinator.CodexRefreshing);
            Assert.True(coordinator.RefreshButtonEnabled);
        }

        var failing = new CombinedRefreshCoordinator(
            (_, _) => throw new InvalidOperationException("boom"),
            _ => Task.FromResult(new CodexRefreshResult(
                new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, DateTimeOffset.Now, DateTimeOffset.Now, null, null, null, [new CodexQuotaWindow(null, 10, 300, null, CodexWindowKind.FiveHour)], null),
                false,
                null)));
        var afterException = await failing.RefreshAllAsync(true, CancellationToken.None);
        Assert.Equal(AppSyncStatus.Error, afterException.ChatGpt?.Status);
        Assert.Equal(CodexQuotaStatus.Available, afterException.Codex.Snapshot.Status);
        Assert.False(failing.BothRefreshing);
        Assert.True(failing.RefreshButtonEnabled);
    }

    [Fact]
    public void LocalizationKeys_ExistForCodexAndTaskbar()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("Always show taskbar status", UiText.TaskbarStatusEnabled);
            Assert.Equal("Refresh all", UiText.RefreshAll);
            Assert.Equal("Refreshing...", UiText.RefreshAllProgress);
            Assert.Equal("Pin", UiText.Pin);
            Assert.Equal("Unpin", UiText.Unpin);
            Assert.Equal("Close", UiText.Close);
            Assert.Equal("Syncing", UiText.Syncing);
            Assert.Equal("Codex usage", UiText.CodexUsage);
            Assert.Equal("5-hour used", UiText.FiveHourUsed);
            Assert.Equal("5-hour remaining", UiText.FiveHourRemaining);
            Assert.Equal("Weekly used", UiText.WeeklyUsed);
            Assert.Equal("Weekly remaining", UiText.WeeklyRemaining);
            Assert.Equal("Last checked", UiText.LastChecked);
            Assert.Equal("Reset credits", UiText.ResetCredits);
            Assert.Equal("Codex not found", UiText.CodexNotFound);
            Assert.Equal("Sign in to Codex", UiText.CodexSignIn);
            Assert.Equal("Last data shown · stale", UiText.CodexDataStale);
            Assert.Equal("Recent refresh error", UiText.CodexRecentRefreshError);
            Assert.Equal("Protocol changed", UiText.CodexProtocolChanged);
            Assert.Equal("Request timed out", UiText.CodexTimedOut);
            Assert.Equal("Refreshing...", UiText.CodexRefreshing);
            Assert.Equal("Codex executable path (optional)", UiText.CodexExePath);
            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal("작업표시줄 상시 표시", UiText.TaskbarStatusEnabled);
            Assert.Equal("모두 새로고침", UiText.RefreshAll);
            Assert.Equal("동기화 중...", UiText.RefreshAllProgress);
            Assert.Equal("고정", UiText.Pin);
            Assert.Equal("고정 해제", UiText.Unpin);
            Assert.Equal("닫기", UiText.Close);
            Assert.Equal("동기화 중", UiText.Syncing);
            Assert.Equal("5시간 사용량", UiText.FiveHourUsed);
            Assert.Equal("주간 남음", UiText.WeeklyRemaining);
            Assert.Equal("리셋권", UiText.ResetCredits);
            Assert.Equal("Codex를 찾을 수 없음", UiText.CodexNotFound);
            Assert.Equal("Codex에 로그인하세요", UiText.CodexSignIn);
            Assert.Equal("마지막 데이터 표시 · 오래됨", UiText.CodexDataStale);
            Assert.Equal("최근 새로고침 오류", UiText.CodexRecentRefreshError);
            Assert.Equal("프로토콜이 변경됨", UiText.CodexProtocolChanged);
            Assert.Equal("요청 시간이 초과됨", UiText.CodexTimedOut);
            Assert.Equal("새로고침 중...", UiText.CodexRefreshing);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void DurationLabels_DoNotInventFiveHourOrWeekly()
    {
        UiText.SetLanguage(UiLanguage.English);
        Assert.Equal("24-hour", CodexDisplayFormatting.DurationLabel(1440));
        Assert.Equal("3-day", CodexDisplayFormatting.DurationLabel(4320));
        Assert.Equal("17 min", CodexDisplayFormatting.DurationLabel(17));
        Assert.NotEqual("5-hour", CodexDisplayFormatting.DurationLabel(240));
        Assert.NotEqual("Weekly", CodexDisplayFormatting.DurationLabel(240));
    }

    [Fact]
    public void StripWindow_UsesSafeOverlayFlags()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TaskbarStatusStripWindow.xaml"));
        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowActivated=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Topmost=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CardBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("TextBrush", xaml, StringComparison.Ordinal);
        var source = File.ReadAllText(Find("src/CycleArc/UI/TaskbarStatusStripWindow.xaml.cs"));
        Assert.Contains("FlyoutRequested", source, StringComparison.Ordinal);
        Assert.Contains("RefreshRequested", source, StringComparison.Ordinal);
        Assert.Contains("ContextMenuRequested", source, StringComparison.Ordinal);
        var win32 = File.ReadAllText(Find("src/CycleArc/UI/TaskbarWin32.cs"));
        Assert.DoesNotContain("SetParent", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowsHook", win32, StringComparison.Ordinal);
        Assert.Contains("WsExNoActivate", win32, StringComparison.Ordinal);
        Assert.Contains("~WsExTransparent", win32, StringComparison.Ordinal);
        Assert.Contains("ReassertTopmostNoActivate", win32, StringComparison.Ordinal);
        Assert.Contains("TaskbarTopmostPlacement.HwndTopmost", win32, StringComparison.Ordinal);
        Assert.Contains("TaskbarTopmostPlacement.Flags", win32, StringComparison.Ordinal);
        Assert.Contains("SetWindowPos", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("SetFocus", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip", xaml, StringComparison.Ordinal);
        Assert.Contains("UiCallbackMarshal.TryPost", source, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke", source, StringComparison.Ordinal);
        Assert.Contains("HasShutdownStarted", source, StringComparison.Ordinal);
        Assert.Contains("HasShutdownFinished", source, StringComparison.Ordinal);
        var systemHandler = source[source.IndexOf("private void OnSystemLayout", StringComparison.Ordinal)..];
        Assert.DoesNotContain("RequestReposition();", systemHandler.Split("Dispatcher.BeginInvoke")[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CombinedManualRefresh_DisablesButtonUntilFinished()
    {
        var gate = new TaskCompletionSource();
        var coordinator = new CombinedRefreshCoordinator(
            async (_, _) =>
            {
                await gate.Task;
                return new SyncOutcome(AppSyncStatus.UpToDate, null, 1);
            },
            async _ =>
            {
                await gate.Task;
                return new CodexRefreshResult(
                    CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available),
                    false,
                    null);
            });

        var running = coordinator.RefreshAllAsync(true, CancellationToken.None);
        Assert.True(coordinator.ManualRefreshInProgress);
        Assert.False(coordinator.RefreshButtonEnabled);
        var busy = CombinedRefreshCoordinator.Present(
            coordinator.ChatGptRefreshing,
            coordinator.CodexRefreshing,
            coordinator.ManualRefreshInProgress);
        Assert.False(busy.Enabled);
        Assert.True(busy.Active);
        Assert.Equal(UiText.RefreshAllProgress, busy.ProgressText);
        gate.SetResult();
        await running;
        Assert.False(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.RefreshButtonEnabled);
        var restored = CombinedRefreshCoordinator.Present(false, false, coordinator.ManualRefreshInProgress);
        Assert.True(restored.Enabled);
        Assert.False(restored.Active);
    }

    [Fact]
    public async Task CombinedManualRefresh_CancellationReenablesButton()
    {
        using var cts = new CancellationTokenSource();
        var coordinator = new CombinedRefreshCoordinator(
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new SyncOutcome(AppSyncStatus.UpToDate, null, 0);
            },
            async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new CodexRefreshResult(
                    CodexQuotaSnapshot.Empty(CodexQuotaStatus.Cancelled),
                    false,
                    "cancelled");
            });
        var running = coordinator.RefreshAllAsync(true, cts.Token);
        Assert.False(coordinator.RefreshButtonEnabled);
        cts.Cancel();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.False(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.RefreshButtonEnabled);
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
