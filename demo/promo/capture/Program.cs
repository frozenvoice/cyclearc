using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CycleArc;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.PromoCapture;

/// <summary>
/// Records the production popup and widget, bound to synthetic accounts, as timed PNG frame
/// sequences for the promo composition. Like the documentation previews, it never reads
/// accounts, settings, caches or credentials, registers a tray icon or starts a refresh:
/// every quota value below is a fixed sample. Clicks go through the real buttons' automation
/// peers, and the handlers rebind the windows the way the app controller does.
/// </summary>
internal static class Program
{
    private sealed class OfflineApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    // Frame density per sequence: the widget is framed closer than the popup in the edit.
    private static readonly Dictionary<string, double> Scales = new() { ["flyout"] = 2, ["widget"] = 3 };
    private const int FrameMs = 33;          // ~30 fps capture
    private static string _out = "";
    private static readonly Stopwatch Clock = new();
    private static readonly List<Task> Writes = [];
    private static readonly ConcurrentDictionary<string, List<object>> Frames = new();
    private static readonly Dictionary<string, object> Marks = [];
    private static readonly Dictionary<string, object> Regions = [];

    [STAThread]
    private static int Main(string[] args)
    {
        _out = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "session"));
        if (Directory.Exists(_out)) Directory.Delete(_out, true);
        Directory.CreateDirectory(_out);
        var app = new OfflineApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CycleArc;component/UI/Themes.xaml", UriKind.Relative)
        });
        UiText.SetLanguage(UiLanguage.English);
        typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [AppTheme.Dark]);
        var code = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            finally { app.Shutdown(); }
        });
        app.Run();
        return code;
    }

    private static async Task RunAsync()
    {
        var now = DateTimeOffset.Now;
        var accounts = Accounts(now);
        var selected = accounts[0].Profile.Id;
        var refreshing = false;
        ExportTrayIcons(accounts);

        // ---- popup ------------------------------------------------------------------------
        var flyout = new FlyoutWindow { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
        flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = 100 });
        void BindFlyout() => flyout.BindAccounts(accounts, selected, refreshing);
        flyout.AccountSelected += id => { selected = id; BindFlyout(); };
        flyout.SyncRequested += () => { refreshing = true; BindFlyout(); };
        BindFlyout();
        ShowOffscreen(flyout);
        Clock.Start();
        var content = (FrameworkElement)flyout.Content;
        using (Record("flyout", content))
        {
            Mark("flyout-open", "flyout");
            await Wait(1.5);
            await Click(flyout, "RefreshAllButton", "refresh", "flyout");
            await Wait(1.2);
            now = DateTimeOffset.Now;
            accounts = Refreshed(accounts, now);
            refreshing = false;
            BindFlyout();
            Mark("refreshed", "flyout");
            await Wait(1.2);
            foreach (var (id, mark) in new[] { (accounts[1].Profile.Id, "select-work"), (accounts[2].Profile.Id, "select-claude"), (accounts[3].Profile.Id, "select-cursor") })
            {
                await ClickRow(flyout, id, mark);
                await Wait(1.7);
            }
            Region(flyout, content, "AccountOverview", "accounts", "flyout");
            Region(flyout, content, "CodexCard", "detail", "flyout");
            await Wait(.3);
        }
        flyout.Close();

        // ---- widget -----------------------------------------------------------------------
        var widget = new FloatingWidget { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
        var widgetRefreshing = false;
        IReadOnlyList<ScreenRect> desktop = [new ScreenRect(0, 0, 2560, 1400)];
        void BindWidget() => widget.BindAccounts(accounts, selected, UsagePeriodPreference.Auto, desktop);
        widget.RefreshRequested += () => { widgetRefreshing = true; widget.SetRefreshing(true); };
        BindWidget();
        ShowOffscreen(widget);
        var widgetContent = (FrameworkElement)widget.Content;
        using (Record("widget", widgetContent))
        {
            Mark("widget-open", "widget");
            await Wait(1.5);
            await Click(widget, "WidgetRefreshButton", "widget-refresh", "widget");
            await Wait(1.2);
            now = DateTimeOffset.Now;
            accounts = Climbed(accounts, now);
            widgetRefreshing = false;
            widget.SetRefreshing(widgetRefreshing);
            BindWidget();
            Mark("widget-updated", "widget");
            for (var i = 0; i < widget.Modules.Count; i++)
                Region(widgetContent, widget.Modules[i], $"module-{i}", "widget");
            await Wait(2.4);
        }
        widget.Close();

        await Task.WhenAll(Writes);
        WriteClip();
        Console.WriteLine($"Captured {Frames.Sum(f => f.Value.Count)} frames to {_out}; synthetic data only.");
    }

    // Fixed sample accounts. Quotas, resets and names are illustrative, never read from a machine.
    private static CodexAccountView[] Accounts(DateTimeOffset now)
    {
        CodexAccountView Codex(string id, string label, double five, double week, TimeSpan fiveReset, TimeSpan weekReset) =>
            new(new CodexAccountProfile(id, @"C:\CycleArc-Samples\" + id, label, id != "personal"),
                new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now.AddMinutes(-4), now.AddMinutes(-4), null, null, 2,
                    [new("primary", five, CodexWindowClassifier.FiveHourMinutes, now + fiveReset, CodexWindowKind.FiveHour),
                     new("secondary", week, CodexWindowClassifier.WeeklyMinutes, now + weekReset, CodexWindowKind.Weekly)], null,
                    [now.AddDays(26), now.AddDays(51)]), $"{id}@example.invalid") { IsConnected = true };
        var claude = new CodexAccountView(new CodexAccountProfile("research", "", "Research") { Provider = UsageProviderId.Claude },
            new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, now.AddMinutes(-4), now.AddMinutes(-4), null, null, null,
                [new("five_hour", 46, 300, now.AddHours(2).AddMinutes(40), CodexWindowKind.FiveHour),
                 new("seven_day", 18, 10080, now.AddDays(3).AddHours(5), CodexWindowKind.Weekly)],
                ClaudeUsagePresentation.LiveDetail) { Provider = UsageProviderId.Claude },
            "research@example.invalid") { IsConnected = true };
        var cursor = new CodexAccountView(new CodexAccountProfile("editor", "", "Editor") { Provider = UsageProviderId.Cursor },
            new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now.AddMinutes(-4), now.AddMinutes(-4), null, null, null,
                [new("cursor-auto", 38.5, null, now.AddDays(12), CodexWindowKind.Other),
                 new("cursor-api", 12, null, now.AddDays(12), CodexWindowKind.Other),
                 new("cursor-on-demand", null, null, null, CodexWindowKind.Other) { IsEnabled = false }],
                CursorUsagePresentation.LiveDetail) { Provider = UsageProviderId.Cursor },
            "editor@example.invalid") { IsConnected = true };
        return [Codex("personal", "Personal", 22, 31, TimeSpan.FromHours(3.2), TimeSpan.FromDays(4.3)),
                Codex("work", "Work", 64, 47, TimeSpan.FromMinutes(78), TimeSpan.FromDays(2.6)), claude, cursor];
    }

    // A completed check: same values, current check time.
    private static CodexAccountView[] Refreshed(CodexAccountView[] accounts, DateTimeOffset now) =>
        [.. accounts.Select(a => a with { Snapshot = a.Snapshot with { LastSuccessfulRefresh = now, LastAttemptedRefresh = now } })];

    // A later check where Work's 5-hour use passed 70% and Research used a little more.
    private static CodexAccountView[] Climbed(CodexAccountView[] accounts, DateTimeOffset now) =>
        [.. accounts.Select(a => a with
        {
            Snapshot = a.Snapshot with
            {
                LastSuccessfulRefresh = now, LastAttemptedRefresh = now,
                Windows = [.. a.Snapshot.Windows.Select(w => (a.Profile.Id, w.Kind) switch
                {
                    ("work", CodexWindowKind.FiveHour) => w with { UsedPercent = 73 },
                    ("research", CodexWindowKind.FiveHour) => w with { UsedPercent = 52 },
                    _ => w
                })]
            }
        })];

    private static void ExportTrayIcons(CodexAccountView[] accounts)
    {
        var tray = Path.Combine(_out, "tray");
        Directory.CreateDirectory(tray);
        foreach (var account in accounts.Concat(Climbed(accounts, DateTimeOffset.Now).Select(a => a with { Profile = a.Profile with { Id = a.Profile.Id + "-climbed" } })))
        {
            using var icon = TrayIconRenderer.Render(account.Snapshot, TrayIconStyle.RemainingNumber, 128);
            using var bitmap = icon.ToBitmap();
            bitmap.Save(Path.Combine(tray, account.Profile.Id + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static void ShowOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = SystemParameters.VirtualScreenLeft - 4000;
        window.Top = SystemParameters.VirtualScreenTop - 4000;
        window.Show();
        window.Left = SystemParameters.VirtualScreenLeft - 4000;
        window.Top = SystemParameters.VirtualScreenTop - 4000;
    }

    private static double Now => Clock.Elapsed.TotalSeconds;

    private static async Task Wait(double seconds) => await Task.Delay(TimeSpan.FromSeconds(seconds));

    private static void Mark(string name, string seq, Rect? at = null) =>
        Marks[name] = at is { } r
            ? new { t = Math.Round(Now, 4), seq, x = Math.Round(r.X + r.Width / 2, 1), y = Math.Round(r.Y + r.Height / 2, 1) }
            : new { t = Math.Round(Now, 4), seq };

    private static Rect Bounds(FrameworkElement root, FrameworkElement element) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    private static void Region(Window window, FrameworkElement root, string name, string key, string seq) =>
        Region(root, (FrameworkElement)window.FindName(name), key, seq);

    private static void Region(FrameworkElement root, FrameworkElement element, string key, string seq)
    {
        root.UpdateLayout();
        var r = Bounds(root, element);
        Regions[key] = new { seq, x = Math.Round(r.X, 1), y = Math.Round(r.Y, 1), width = Math.Round(r.Width, 1), height = Math.Round(r.Height, 1) };
    }

    // The harness clicks this long after marking, so the composition can glide the pointer there first.
    private const double ClickDelay = .65;

    private static async Task Click(Window window, string name, string mark, string seq)
    {
        var root = (FrameworkElement)window.Content;
        var button = (Button)window.FindName(name);
        Mark(mark, seq, Bounds(root, button));
        await Wait(ClickDelay);
        Invoke(button);
    }

    private static async Task ClickRow(FlyoutWindow flyout, string id, string mark)
    {
        var root = (FrameworkElement)flyout.Content;
        var list = (ItemsControl)flyout.FindName("AccountOverview");
        var row = list.Items.OfType<Button>().First(b => Equals(b.Tag, id));
        Mark(mark, "flyout", Bounds(root, row));
        await Wait(ClickDelay);
        Invoke(row);
    }

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    private sealed class Recording(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }

    private static Recording Record(string seq, FrameworkElement content)
    {
        var dir = Path.Combine(_out, seq);
        Directory.CreateDirectory(dir);
        var list = Frames.GetOrAdd(seq, _ => []);
        var scale = Scales[seq];
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(FrameMs) };
        void Capture()
        {
            content.UpdateLayout();
            var size = content.RenderSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            var t = Now;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(content);
            bitmap.Freeze();
            var index = list.Count;
            var path = Path.Combine(dir, index.ToString("D5", CultureInfo.InvariantCulture) + ".png");
            list.Add(new { t = Math.Round(t, 4), w = Math.Round(size.Width, 1), h = Math.Round(size.Height, 1) });
            Writes.Add(Task.Run(() =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(path);
                encoder.Save(stream);
            }));
        }
        timer.Tick += (_, _) => Capture();
        Capture();
        timer.Start();
        return new Recording(timer);
    }

    private static void WriteClip()
    {
        var clip = new { scales = Scales, frames = Frames, marks = Marks, regions = Regions, synthetic = true, version = typeof(App).Assembly.GetName().Version?.ToString(3) };
        var json = JsonSerializer.Serialize(clip, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_out, "clip.js"), "window.CLIP = " + json + ";\n", Encoding.UTF8);
    }
}
