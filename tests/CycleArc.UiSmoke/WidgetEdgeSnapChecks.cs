using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class WidgetEdgeSnapChecks
{
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var language = UiText.Language;
        var widget = new FloatingWidget { ShowActivated = false };
        var moved = 0;
        try
        {
            UiText.SetLanguage(UiLanguage.English);
            widget.Moved += (_, _) => moved++;
            widget.Show();
            Pump();
            var work = CurrentWorkArea(widget);
            var accounts = Accounts(DateTimeOffset.Now);
            Bind(widget, accounts, 1, work);
            widget.Apply(new AppSettings
            {
                FloatingWidgetEnabled = true, SnapWindowsToScreenEdges = true,
                WidgetLeft = work.X + 220, WidgetTop = work.Y + 180,
                WidgetOpacity = .92, WidgetAlwaysOnTop = true
            });
            Pump();
            Check(widget.EdgeAnchors == default, "Initial widget unexpectedly attached.");
            Check(FloatingWidget.ShouldBeginWindowDrag(widget.Modules[0]), "Module lost drag routing.");
            Check(!FloatingWidget.ShouldBeginWindowDrag(
                (DependencyObject)widget.FindName("WidgetZoomInButton")), "Zoom button drags window.");
            moved = 0;
            var size = Size(widget);
            var corner = WindowEdgeSnap.Place(
                work.Right - WindowEdgeSnap.MarginDip - size.Width,
                work.Bottom - WindowEdgeSnap.MarginDip - size.Height,
                size.Width, size.Height, work,
                new WindowEdgeAnchors(HorizontalEdgeAnchor.Right, VerticalEdgeAnchor.Bottom));
            SimulatedDrag(widget, corner.Left, corner.Top);
            Pump();
            Check(widget.EdgeAnchors == new WindowEdgeAnchors(
                HorizontalEdgeAnchor.Right, VerticalEdgeAnchor.Bottom), "Corner drag did not attach.");
            CheckAt(widget, work, "corner");
            Check(moved == 1, "Corner drag emitted more than one persistence event.");

            var beforeClick = (widget.Left, widget.Top);
            var beforeMoved = moved;
            SimulatedClick(widget);
            Check(widget.EdgeAnchors.IsAttached && (widget.Left, widget.Top) == beforeClick,
                "Click detached or moved an attached widget.");
            Check(moved == beforeMoved, "Click emitted a move.");

            var free = (work.X + (work.Width - Size(widget).Width) / 2,
                work.Y + (work.Height - Size(widget).Height) / 2);
            SimulatedDrag(widget, free.Item1, free.Item2, bypass: true);
            Pump();
            Check(widget.EdgeAnchors == default, "Shift/bypass drag kept anchors.");
            Check(Math.Abs(widget.Left - free.Item1) <= 3 && Math.Abs(widget.Top - free.Item2) <= 3,
                "Bypass drag did not preserve its free drop.");

            var leftTop = work.Y + work.Height / 2 - Size(widget).Height / 2;
            SimulatedDrag(widget, work.X + WindowEdgeSnap.MarginDip, leftTop);
            Pump();
            Check(widget.EdgeAnchors == new WindowEdgeAnchors(
                HorizontalEdgeAnchor.Left, VerticalEdgeAnchor.None), "One-axis drag attached incorrectly.");
            var freeTop = widget.Top;
            foreach (var zoom in new[] { 100, 150, 80 })
            {
                widget.SetZoom(zoom, notify: false);
                Pump();
                CheckAt(widget, work, $"zoom {zoom}");
                if (directory is not null)
                {
                            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle);
                    NativeEdgeSnapChecks.Capture(widget, screen.WorkingArea,
                        Path.Combine(directory, $"widget-edge-snap-{zoom}.png"));
                }
            }
            Check(Math.Abs(widget.Top - freeTop) <= 3, "Free axis moved during zoom.");

            foreach (var count in new[] { 1, 3, 5 })
            {
                Bind(widget, accounts, count, work);
                Pump();
                CheckAt(widget, work, $"accounts {count}");
            }
            var stale = accounts.Select(a => a with
            {
                Snapshot = a.Snapshot with { Status = CodexQuotaStatus.Stale }
            }).ToArray();
            Bind(widget, stale, 5, work);
            Pump();
            CheckAt(widget, work, "status resize");

            var beforeDisable = (widget.Left, widget.Top);
            widget.ApplyEdgeSnapSettings(new AppSettings { SnapWindowsToScreenEdges = false });
            Check(widget.EdgeAnchors == default, "Disable did not clear anchors immediately.");
            Check((widget.Left, widget.Top) == beforeDisable, "Disable moved widget.");
            widget.SetZoom(100, notify: false);
            Pump();
            Check(widget.EdgeAnchors == default, "Disabled relayout recreated anchors.");

            widget.ApplyEdgeSnapSettings(new AppSettings
            {
                SnapWindowsToScreenEdges = true,
                WidgetHorizontalAnchor = HorizontalEdgeAnchor.Left
            });
            Check(widget.EdgeAnchors == new WindowEdgeAnchors(
                HorizontalEdgeAnchor.Left, VerticalEdgeAnchor.None), "Saved anchor did not load.");
            widget.Relayout();
            Pump();
            CheckAt(widget, work, "saved anchor");
            Console.WriteLine("PASS: widget simulated drag/click/bypass, axis anchors, zoom/content resize, settings and screenshots.");
        }
        finally
        {
            if (widget.IsVisible) widget.Close();
            UiText.SetLanguage(language);
        }
    }

    private static void Bind(FloatingWidget widget, IReadOnlyList<CodexAccountView> accounts,
        int count, ScreenRect work) =>
        widget.BindAccounts(accounts.Take(count).ToArray(), accounts[0].Profile.Id,
            UsagePeriodPreference.Auto, [work], DateTimeOffset.Now);

    private static CodexAccountView[] Accounts(DateTimeOffset now)
    {
        static CodexQuotaSnapshot S(DateTimeOffset n, double a, double b) =>
            new(CodexQuotaStatus.Available, "pro", n, n, null, null, null,
            [new("five", a, CodexWindowClassifier.FiveHourMinutes, n.AddHours(2), CodexWindowKind.FiveHour),
             new("week", b, CodexWindowClassifier.WeeklyMinutes, n.AddDays(4), CodexWindowKind.Weekly)], null);
        return
        [
            WidgetFixture.Synthetic("edge-a", "Main", S(now, 21, 32)),
            WidgetFixture.Synthetic("edge-b", "Work", S(now, 42, 18)),
            WidgetFixture.Synthetic("edge-c", "Personal", S(now, 64, 25)),
            WidgetFixture.Synthetic("edge-d", "Kakao", S(now, 83, 41)),
            WidgetFixture.Synthetic("edge-e", "Lab", S(now, 35, 57))
        ];
    }

    private static void SimulatedDrag(FloatingWidget widget, double left, double top, bool bypass = false)
    {
        var session = new WidgetDragSession(widget.Left, widget.Top, widget.Left + 24, widget.Top + 24);
        typeof(FloatingWidget).GetField("_drag", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(widget, session);
        typeof(FloatingWidget).GetMethod("UpdateDragPosition",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(widget,
            [new System.Windows.Point(left + 24, top + 24)]);
        typeof(FloatingWidget).GetMethod("FinishDragCore",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(widget, [true, bypass]);
    }

    private static void SimulatedClick(FloatingWidget widget)
    {
        var session = new WidgetDragSession(widget.Left, widget.Top, widget.Left + 24, widget.Top + 24);
        typeof(FloatingWidget).GetField("_drag", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(widget, session);
        typeof(FloatingWidget).GetMethod("FinishDrag",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(widget, [true]);
    }

    private static (double Width, double Height) Size(FloatingWidget w) =>
        (Math.Max(w.ActualWidth, 1), Math.Max(w.ActualHeight, 1));

    private static void CheckAt(FloatingWidget w, ScreenRect work, string name)
    {
        var s = Size(w);
        var expected = WindowEdgeSnap.Place(w.Left, w.Top, s.Width, s.Height, work, w.EdgeAnchors);
        Check(Math.Abs(w.Left - expected.Left) <= 2 && Math.Abs(w.Top - expected.Top) <= 2,
            $"{name} placement drifted ({w.Left},{w.Top} vs {expected}).");
    }

    private static ScreenRect CurrentWorkArea(FloatingWidget w)
    {
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(w).Handle);
        var fromDevice = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var a = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var b = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        return new ScreenRect((int)Math.Round(a.X), (int)Math.Round(a.Y),
            Math.Max(1, (int)Math.Round(b.X - a.X)), Math.Max(1, (int)Math.Round(b.Y - a.Y)));
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            var f = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => f.Continue = false));
            Dispatcher.PushFrame(f);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
