using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;
using CycleArc.Updates;

namespace CycleArc.UiSmoke;

internal static class PortableUiChecks
{
    public static void Run()
    {
        var property = typeof(InstalledApp).GetProperty(nameof(InstalledApp.IsPortable))!;
        var previousPortable = InstalledApp.IsPortable;
        var previousLanguage = UiText.Language;
        try
        {
            property.SetValue(null, true);
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            {
                UiText.SetLanguage(language);
                var settings = AppSettings.CreateDefaults();
                settings.StartWithWindows = true;
                var window = new SettingsWindow(settings);
                try
                {
                    var startup = (CheckBox)window.FindName("StartupBox");
                    Check(!startup.IsEnabled && startup.IsChecked == true && startup.ToolTip is string,
                        "Portable startup control must be disabled and preserve the shared installed preference.");
                    // A stale/programmatic event must not alter the installed startup preference.
                    startup.IsChecked = false;
                    typeof(SettingsWindow).GetMethod("Commit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                    Check(settings.StartWithWindows, "Portable settings commit changed installed startup consent.");
                }
                finally { window.Close(); }
                using var client = new DisabledClient();
                var updates = new AppUpdateCoordinator(client);
                var update = new UpdateWindow(updates, () => throw new InvalidOperationException("Portable update attempted exit."));
                try
                {
                    var status = ((TextBlock)update.FindName("StatusLabel")).Text;
                    Check(status.Contains(UiText.T("manual", "수동"), StringComparison.Ordinal), "Portable updates must explain manual ZIP replacement.");
                    Check(!((Button)update.FindName("CheckButton")).IsEnabled
                        && ((Button)update.FindName("ActionButton")).Visibility == Visibility.Collapsed,
                        "Portable update actions must remain unavailable.");
                    updates.CheckAsync().GetAwaiter().GetResult();
                    Check(client.Calls == 0, "Portable update coordinator accessed the managed updater.");
                }
                finally { update.Close(); }
                using var tray = new TrayController();
                var icon = (System.Windows.Forms.NotifyIcon)typeof(TrayController).GetField("_icon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
                var startupItem = icon.ContextMenuStrip!.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == UiText.StartWithWindows);
                Check(!startupItem.Enabled, "Portable tray menu allows startup registry changes.");
                // This call must be a complete no-op before opening even a writable Run key.
                new WindowsStartupService().Apply(true);
            }
            Console.WriteLine("PASS: portable EN/KO startup controls preserve shared consent; manual ZIP update guidance and disabled managed updater.");
        }
        finally { property.SetValue(null, previousPortable); UiText.SetLanguage(previousLanguage); }
    }

    private sealed class DisabledClient : IAppUpdateClient, IDisposable
    {
        public bool IsInstalled => false;
        public string CurrentVersion => "0.10.0";
        public int Calls { get; private set; }
        public Task<AppUpdateRelease?> CheckAsync(CancellationToken token) { Calls++; throw new InvalidOperationException(); }
        public Task DownloadAsync(AppUpdateRelease release, IProgress<int> progress, CancellationToken token) { Calls++; throw new InvalidOperationException(); }
        public void ApplyOnExit(AppUpdateRelease release) { Calls++; throw new InvalidOperationException(); }
        public void Dispose() { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
