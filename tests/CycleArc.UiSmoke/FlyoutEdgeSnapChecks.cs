using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.UI;
using CycleArc.Services;

namespace CycleArc.UiSmoke;

internal static class FlyoutEdgeSnapChecks
{
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);

        var previousLanguage = UiText.Language;
        var flyout = new FlyoutWindow { ShowActivated = false, ShowInTaskbar = false };
        FlyoutWindow? reopened = null;
        var changed = 0;
        try
        {
            UiText.SetLanguage(UiLanguage.English);
            var now = DateTimeOffset.Now;
            var snapshot = new CodexQuotaSnapshot(
                CodexQuotaStatus.Available,
                "pro",
                now,
                now,
                null,
                null,
                null,
                [
                    new("five_hour", 24, 300, now.AddHours(2), CodexWindowKind.FiveHour),
                    new("weekly", 12, 10080, now.AddDays(4), CodexWindowKind.Weekly)
                ],
                null);
            flyout.ApplyWindowSettings(new AppSettings { SnapWindowsToScreenEdges = true, FlyoutZoomPercent = 100 });
            flyout.Bind(snapshot);
            flyout.PositionChanged += (_, _) => changed++;
            flyout.Show();
            Pump();
            flyout.Activate();
            flyout.UpdateLayout();

            var work = CurrentWorkArea(flyout);
            flyout.RestorePosition(
                work.X + (work.Width - flyout.Width) / 2,
                work.Y + (work.Height - Math.Max(flyout.ActualHeight, 1)) / 2);
            Pump();

            // Deterministic synthetic-coordinate release coverage. Native pointer injection is
            // kept in the opt-in hardware smoke so the default suite remains repeatable.
            flyout.ApplyEdgeSnapSettings(new AppSettings
            {
                SnapWindowsToScreenEdges = true,
                FlyoutHorizontalAnchor = HorizontalEdgeAnchor.Left,
                FlyoutVerticalAnchor = VerticalEdgeAnchor.Top
            });
            flyout.RestorePosition(work.X + 300, work.Y + 220);
            Pump();
            changed = 0;
            var attachedBeforeClick = flyout.EdgeAnchors;
            var beforeClick = flyout.PixelPosition
                ?? throw new InvalidOperationException("Flyout has no native position before click.");
            Pump();
            RaiseSyntheticHeaderPress(flyout);
            Pump();
            Require(flyout.EdgeAnchors == attachedBeforeClick,
                "A no-op header gesture changed edge anchors.");
            var afterClick = flyout.PixelPosition
                ?? throw new InvalidOperationException("Flyout has no native position after click.");
            Require(Math.Abs(afterClick.X - beforeClick.X) <= 2 && Math.Abs(afterClick.Y - beforeClick.Y) <= 2,
                "A no-op header gesture moved the flyout.");
            Require(changed == 0, "A no-op header gesture persisted a position.");

            // Drive the production release path with synthetic coordinates. The opt-in native
            // smoke separately covers the actual pointer threshold and DragMove interaction.
            var right = work.Right - WindowEdgeSnap.MarginDip - flyout.Width;
            var bottom = work.Bottom - WindowEdgeSnap.MarginDip - Math.Max(flyout.ActualHeight, 1);
            var destination = DipToPixel(flyout, right, bottom);
            flyout.Left = right;
            flyout.Top = bottom;
            changed = 0;
            InvokePrivate(flyout, "CompleteDragSnap", false);
            InvokePrivate(flyout, "PersistPosition");
            Pump();
            Require(flyout.EdgeAnchors.Horizontal == HorizontalEdgeAnchor.Right
                && flyout.EdgeAnchors.Vertical == VerticalEdgeAnchor.Bottom,
                $"Simulated release did not attach to the right/bottom edges ({flyout.EdgeAnchors}).");
            var snapped = flyout.PixelPosition
                ?? throw new InvalidOperationException("Flyout has no native position after drag.");
            Require(Math.Abs(snapped.X - destination.X) <= 4 && Math.Abs(snapped.Y - destination.Y) <= 4,
                "Simulated release did not place the flyout at its edge target.");
            Require(changed == 1, $"Simulated drag persisted {changed} positions instead of one.");

            // Disabling and re-enabling clears persisted anchors. A later real drop can
            // end at the same coordinates, but it must persist the renewed attachment.
            var savedCorner = (flyout.Left, flyout.Top);
            var savedEvents = changed;
            flyout.ApplyEdgeSnapSettings(new AppSettings { SnapWindowsToScreenEdges = false });
            flyout.ApplyEdgeSnapSettings(new AppSettings { SnapWindowsToScreenEdges = true });
            Require(!flyout.EdgeAnchors.IsAttached, "Re-enabling inferred an attachment.");
            flyout.Left -= 40;
            flyout.Left = savedCorner.Left;
            InvokePrivate(flyout, "CompleteDragSnap", false);
            InvokePrivate(flyout, "PersistPosition");
            Require(changed == savedEvents + 1 && flyout.EdgeAnchors.IsAttached,
                "Reattaching at the old coordinates did not persist the renewed anchors.");

            // A routed interactive control remains a control after the header drag path changed.
            var refresh = (System.Windows.Controls.Button)flyout.FindName("RefreshAllButton");
            var refreshes = 0;
            flyout.SyncRequested += () => refreshes++;
            refresh.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(refreshes == 1 && flyout.EdgeAnchors.Horizontal == HorizontalEdgeAnchor.Right,
                "The flyout header button leaked into drag/snap handling.");

            // Attached axes follow the real production resize/DPI relayout path.
            var anchorsBeforeResize = flyout.EdgeAnchors;
            flyout.ApplyWindowSettings(new AppSettings
            {
                SnapWindowsToScreenEdges = true,
                FlyoutHorizontalAnchor = anchorsBeforeResize.Horizontal,
                FlyoutVerticalAnchor = anchorsBeforeResize.Vertical,
                FlyoutZoomPercent = 130
            });
            flyout.RefreshWorkArea();
            Pump();
            Require(flyout.EdgeAnchors == anchorsBeforeResize,
                "Zoom/work-area recovery lost attached axes.");
            var resizedWork = CurrentWorkArea(flyout);
            var expected = WindowEdgeSnap.Place(
                flyout.Left,
                flyout.Top,
                flyout.Width,
                Math.Max(flyout.ActualHeight, 1),
                resizedWork,
                anchorsBeforeResize);
            Require(Math.Abs(flyout.Left - expected.Left) <= 1
                && Math.Abs(flyout.Top - expected.Top) <= 1,
                "Zoom/work-area recovery did not preserve edge placement.");

            // Account rows really change measured height; SizeChanged must retain the
            // bottom anchor without a new drop or an explicit placement call.
            foreach (var count in new[] { 1, 3, 5, 1 })
            {
                var rows = Enumerable.Range(0, count).Select(index =>
                    WidgetFixture.Synthetic($"edge-account-{index}", $"Synthetic {index + 1}", snapshot)).ToArray();
                flyout.BindAccounts(rows, rows[0].Profile.Id, false);
                flyout.UpdateLayout();
                Pump();
                var area = CurrentWorkArea(flyout);
                Require(Math.Abs(flyout.Left + flyout.Width - (area.Right - 2)) <= 1
                    && Math.Abs(flyout.Top + flyout.ActualHeight - (area.Bottom - 2)) <= 1,
                    $"Account count {count} changed the attached margin.");
            }
            // Restore the original binding shape for the recreation comparison below.
            flyout.Close();
            flyout = new FlyoutWindow { ShowActivated = false, ShowInTaskbar = false };
            flyout.ApplyWindowSettings(new AppSettings
            {
                FlyoutZoomPercent = 130, FlyoutHorizontalAnchor = HorizontalEdgeAnchor.Right,
                FlyoutVerticalAnchor = VerticalEdgeAnchor.Bottom
            });
            flyout.Bind(snapshot);
            flyout.Show();
            flyout.RestorePosition(work.X + 100, work.Y + 100);
            Pump();
            var pixels = flyout.PixelPosition
                ?? throw new InvalidOperationException("Flyout has no physical coordinates to restore.");
            var saved = new AppSettings
            {
                SnapWindowsToScreenEdges = true,
                FlyoutHorizontalAnchor = flyout.EdgeAnchors.Horizontal,
                FlyoutVerticalAnchor = flyout.EdgeAnchors.Vertical,
                FlyoutPixelLeft = pixels.X,
                FlyoutPixelTop = pixels.Y,
                FlyoutLeft = flyout.Left,
                FlyoutTop = flyout.Top,
                FlyoutPositionConfigured = true,
                FlyoutZoomPercent = flyout.ZoomPercent
            };
            flyout.Close();
            reopened = new FlyoutWindow { ShowActivated = false, ShowInTaskbar = false };
            reopened.ApplyWindowSettings(saved);
            reopened.Bind(snapshot);
            reopened.Show();
            Pump();
            reopened.RestorePosition(saved.FlyoutLeft, saved.FlyoutTop);
            Pump();
            var restored = reopened.PixelPosition
                ?? throw new InvalidOperationException("Reopened flyout has no physical position.");
            Require(Math.Abs(restored.X - pixels.X) <= 3 && Math.Abs(restored.Y - pixels.Y) <= 3,
                "Saved pixel position did not restore on the native monitor.");
            Require(reopened.EdgeAnchors == flyoutAnchors(saved),
                "Reopened flyout did not restore edge anchors.");

            Console.WriteLine("PASS: synthetic-coordinate production flyout snap/release path, routed header input, zoom/account-height recovery and host-monitor pixel restore.");
        }
        finally
        {
            if (reopened is not null && reopened.IsVisible) reopened.Close();
            if (flyout.IsVisible) flyout.Close();
            UiText.SetLanguage(previousLanguage);
        }

        static WindowEdgeAnchors flyoutAnchors(AppSettings settings) =>
            new WindowEdgeAnchors(settings.FlyoutHorizontalAnchor, settings.FlyoutVerticalAnchor).Normalize();
    }

    private static ScreenRect CurrentWorkArea(FlyoutWindow window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        var source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("Flyout has no presentation source.");
        var fromDevice = source.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = fromDevice.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        return new ScreenRect(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X)),
            Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y)));
    }

    private static (int X, int Y) DipToPixel(FlyoutWindow window, double left, double top)
    {
        var source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("Flyout has no presentation source.");
        var toDevice = source.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var point = toDevice.Transform(new Point(left, top));
        return ((int)Math.Round(point.X), (int)Math.Round(point.Y));
    }

    private static void RaiseSyntheticHeaderPress(FlyoutWindow flyout)
    {
        var header = (UIElement)flyout.FindName("FlyoutHeaderGrid");
        var args = new MouseButtonEventArgs(
            Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = header
        };
        header.RaiseEvent(args);
    }

    private static void InvokePrivate(FlyoutWindow flyout, string method, params object?[] args)
    {
        var target = typeof(FlyoutWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(candidate => candidate.Name == method
                && candidate.GetParameters().Length == args.Length);
        target.Invoke(flyout, args);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
