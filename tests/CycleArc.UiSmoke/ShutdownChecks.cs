using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.UiSmoke;

internal static class ShutdownChecks
{
    public static void Run()
    {
        foreach (var scenario in new[] { "fault", "cancel", "timeout", "cancel-callback-fault", "log-fault" })
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "CycleArc.UiSmoke.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--shutdown-child");
            start.ArgumentList.Add(scenario);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start shutdown fixture.");
            try
            {
                if (!process.WaitForExit(30000)) throw new TimeoutException($"Shutdown did not finish: {scenario}.");
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                if (process.ExitCode != 0 || !output.Contains("PASS: shutdown", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Shutdown fixture failed: {scenario}. {output} {error}");
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            }
        }
        Console.WriteLine("PASS: production WPF shutdown after refresh failure, cancellation, timeout, cancellation callback and logging failures; isolated mutex released.");
    }

    public static int RunChild(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "cyclearc-shutdown-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var app = new ShutdownApp(scenario, root) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Run();
            if (!app.CleanExit) throw new InvalidOperationException("Shutdown or mutex release was skipped.");
            Console.WriteLine("PASS: shutdown " + scenario);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Exercise App.ExitApp/OnExit while bypassing all real settings, accounts and tray startup.
    private sealed class ShutdownApp(string scenario, string root) : App
    {
        private readonly string _mutexName = "CycleArc-shutdown-test-" + Guid.NewGuid().ToString("N");
        public bool CleanExit { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            var pending = new TaskCompletionSource<CodexRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var refresh = new CodexRefreshCoordinator(_ => pending.Task);
            _ = refresh.RefreshAsync(CancellationToken.None);
            SetField("_refresh", refresh);
            var log = new AppLog(root);
            SetField("_log", log);
            if (scenario == "log-fault") Directory.CreateDirectory(log.CurrentFile);
            InstanceLease = DesktopInstanceLease.TryAcquire(_mutexName)
                ?? throw new InvalidOperationException("Could not acquire the isolated shutdown mutex.");
            var lifetime = (CancellationTokenSource)typeof(App).GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            if (scenario == "cancel-callback-fault")
                lifetime.Token.Register(() => throw new IOException("Synthetic cancellation cleanup failure."));
            // Match production's handled UI exception behavior: a missed finally would hang.
            DispatcherUnhandledException += (_, args) => args.Handled = true;
            Dispatcher.BeginInvoke(() =>
            {
                typeof(App).GetMethod("ExitApp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null);
                if (!IsExiting || !lifetime.IsCancellationRequested)
                    throw new InvalidOperationException("Exit did not cancel active work.");
                if (scenario == "cancel") pending.SetCanceled();
                else if (scenario != "timeout") pending.SetException(new IOException("Synthetic refresh failure."));
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            // Acquire from another thread before process termination, so abandonment cannot pass.
            var released = false;
            var probe = new Thread(() =>
            {
                using var mutex = new Mutex(false, _mutexName);
                try
                {
                    if (mutex.WaitOne(0)) { released = true; mutex.ReleaseMutex(); }
                }
                catch (AbandonedMutexException) { mutex.ReleaseMutex(); }
            });
            probe.Start();
            CleanExit = probe.Join(TimeSpan.FromSeconds(3)) && released && IsExiting;
        }

        private void SetField(string name, object value) =>
            typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, value);
    }
}
