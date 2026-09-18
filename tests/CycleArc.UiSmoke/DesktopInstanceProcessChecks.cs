using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class DesktopInstanceProcessChecks
{
    private const string ChildArgument = "--desktop-instance-child";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    public static void Run()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "cyclearc-desktop-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var children = new List<Child>();

        try
        {
            RunFirstLaunchRace(temporaryRoot, children);

            var key = Guid.NewGuid().ToString("N");
            var first = StartChild(key, Path.Combine(temporaryRoot, "first.jsonl"));
            children.Add(first);
            var ready = WaitForReport(first, report => report.State == "ready");

            Check(ready.ProcessId == first.Process.Id, "Desktop instance child reported the wrong process ID.");
            Check(!string.IsNullOrWhiteSpace(ready.ExecutablePath), "Desktop instance child did not report its executable path.");
            Check(!string.IsNullOrWhiteSpace(ready.VersionText), "Desktop instance child did not report its version.");

            var pipeName = DesktopInstancePipe.ForTests(key);
            Check(ready.PipeName == pipeName, "Desktop instance child and parent derived different test pipe names.");
            var status = Task.WhenAll(
                DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Status, RequestTimeout),
                DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Status, RequestTimeout))
                .GetAwaiter().GetResult();
            foreach (var response in status)
            {
                Check(response.Succeeded, $"Desktop instance status request failed: {response.Error}");
                Check(response.ProcessId == ready.ProcessId, "Desktop instance status returned a different process ID.");
                Check(response.ExecutablePath == ready.ExecutablePath, "Desktop instance status returned a different executable path.");
                Check(response.Version == ready.VersionText, "Desktop instance status returned a different version.");
                Check(!string.IsNullOrWhiteSpace(response.InstanceId), "Desktop instance status did not return its server nonce.");
            }
            var expectedInstance = status[0];

            var activated = DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Activate, RequestTimeout)
                .GetAwaiter().GetResult();
            Check(activated.Succeeded, $"Desktop instance activation failed: {activated.Error}");
            WaitForReport(first, report => report.State == "activate" && report.ActivationCount == 1);

            var second = StartChild(key, Path.Combine(temporaryRoot, "second.jsonl"));
            children.Add(second);
            WaitForReport(second, report => report.State == "busy");
            Check(second.Process.WaitForExit((int)ProcessTimeout.TotalMilliseconds), "Second desktop instance child did not exit after losing the lease.");
            Check(!first.Process.HasExited, "The first desktop instance child exited when a second instance started.");

            var shutdown = DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Shutdown, RequestTimeout,
                expectedInstance: expectedInstance)
                .GetAwaiter().GetResult();
            Check(shutdown.Succeeded, $"Desktop instance shutdown failed: {shutdown.Error}");
            WaitForReport(first, report => report.State == "shutdown");
            Check(first.Process.WaitForExit((int)ProcessTimeout.TotalMilliseconds), "Desktop instance child did not exit after shutdown.");

            var replacement = StartChild(key, Path.Combine(temporaryRoot, "replacement.jsonl"));
            children.Add(replacement);
            var replacementReady = WaitForReport(replacement, report => report.State == "ready");
            Check(replacementReady.ProcessId == replacement.Process.Id, "The desktop instance lease was not released after process exit.");
            var replacementStatus = DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Status, RequestTimeout)
                .GetAwaiter().GetResult();
            Check(replacementStatus.Succeeded && replacementStatus.ProcessId == replacementReady.ProcessId,
                "A replacement desktop instance could not become the new owner.");
            var replacementShutdown = DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Shutdown, RequestTimeout,
                expectedInstance: replacementStatus)
                .GetAwaiter().GetResult();
            Check(replacementShutdown.Succeeded, $"Replacement desktop instance shutdown failed: {replacementShutdown.Error}");
            Check(replacement.Process.WaitForExit((int)ProcessTimeout.TotalMilliseconds), "Replacement desktop instance child did not exit.");

            RunLostShutdownReportCheck(temporaryRoot, children);

            Console.WriteLine("PASS: desktop instance lease, status, activation, shutdown, replacement and lost-report process checks.");
        }
        finally
        {
            foreach (var child in children)
                StopChild(child);

            // This directory was generated above and is the only path this check may remove.
            try
            {
                if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void RunUiChecks()
    {
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("App.ApplyTheme");
        var screenshotDirectory = Path.Combine(Directory.GetCurrentDirectory(), ".tmp", "desktop-version-ui");
        Directory.CreateDirectory(screenshotDirectory);
        var previousLanguage = UiText.Language;

        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                string? selectedPath = null;
                var about = new AboutWindow("9.9.9", "synthetic", path => selectedPath = path);
                try
                {
                    var installButton = (Button)about.FindName("InstallVersionButton")!;
                    var expectedLabel = UiText.T("Install another version…", "다른 버전 설치…");
                    Check(installButton.Visibility == Visibility.Visible && installButton.IsEnabled,
                        "About install-version action is not available when a callback is supplied.");
                    Check(string.Equals(installButton.Content as string, expectedLabel, StringComparison.Ordinal),
                        $"About install-version label is incorrect for {language}/{theme}.");
                    Check(selectedPath is null, "About install-version callback ran during smoke setup.");

                    var content = (FrameworkElement)about.Content;
                    about.Content = null;
                    var frame = new Border
                    {
                        Width = about.Width,
                        Background = about.Background ?? (Brush)about.FindResource("BgBrush"),
                        Child = content,
                    };
                    TextElement.SetForeground(frame, about.Foreground ?? (Brush)about.FindResource("TextBrush"));
                    frame.Measure(new Size(about.Width, double.PositiveInfinity));
                    frame.Arrange(new Rect(0, 0, about.Width, frame.DesiredSize.Height));
                    frame.UpdateLayout();
                    Check(frame.ActualWidth > 0 && frame.ActualHeight > 0,
                        $"About layout is empty for {language}/{theme}.");
                    var buttonBounds = installButton.TransformToAncestor(frame).TransformBounds(new Rect(installButton.RenderSize));
                    Check(buttonBounds.Left >= -0.1 && buttonBounds.Top >= -0.1
                        && buttonBounds.Right <= frame.ActualWidth + 0.1 && buttonBounds.Bottom <= frame.ActualHeight + 0.1,
                        $"About install-version button is clipped for {language}/{theme}.");
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth),
                        (int)Math.Ceiling(frame.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(frame);
                    var suffix = language == UiLanguage.English ? "en" : "ko";
                    var outputPath = Path.Combine(screenshotDirectory, $"about-{suffix}-{theme.ToString().ToLowerInvariant()}.png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(outputPath);
                    encoder.Save(stream);
                }
                finally { about.Close(); }
            }

            UiText.SetLanguage(UiLanguage.English);
            applyTheme.Invoke(null, [AppTheme.Light]);
            var flyout = new FlyoutWindow();
            try
            {
                var versionLabel = (TextBlock)flyout.FindName("VersionText")!;
                var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
                Check(versionLabel.Text == UiText.VersionPrefix + assemblyVersion,
                    "Flyout does not expose the current assembly version label.");
            }
            finally { flyout.Close(); }

            var withoutCallback = new AboutWindow("9.9.9", "synthetic");
            try
            {
                var hiddenButton = (Button)withoutCallback.FindName("InstallVersionButton")!;
                Check(hiddenButton.Visibility == Visibility.Collapsed && !hiddenButton.IsEnabled,
                    "About install-version action is exposed without an installer callback.");
            }
            finally { withoutCallback.Close(); }
            Console.WriteLine($"PASS: version/about UI checks and four screenshots in {screenshotDirectory}.");
        }
        finally { UiText.SetLanguage(previousLanguage); }
    }

    /// <summary>
    /// A child whose shutdown report cannot be written must fail loudly. The IPC server
    /// acknowledges a shutdown before it runs the callback and then swallows whatever the
    /// callback throws, so without the child's own guard a failed report would leave it
    /// waiting for a stop signal that never arrives and the parent waiting out its whole
    /// budget on a shutdown that already returned success.
    /// </summary>
    private static void RunLostShutdownReportCheck(string temporaryRoot, List<Child> children)
    {
        var key = Guid.NewGuid().ToString("N");
        var reportPath = Path.Combine(temporaryRoot, "lost-report.jsonl");
        var child = StartChild(key, reportPath);
        children.Add(child);
        var ready = WaitForReport(child, report => report.State == "ready");

        // Make only the shutdown report unwritable, after the child is already serving.
        // A directory at the report path fails every append without touching the child.
        File.Delete(reportPath);
        Directory.CreateDirectory(reportPath);

        var pipeName = DesktopInstancePipe.ForTests(key);
        var status = DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Status, RequestTimeout)
            .GetAwaiter().GetResult();
        Check(status.Succeeded && status.ProcessId == ready.ProcessId,
            $"The lost-report child did not answer status: {status.Error}");
        var shutdown = DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Shutdown, RequestTimeout,
            expectedInstance: status)
            .GetAwaiter().GetResult();
        // The acknowledgement still succeeds; that is exactly why it cannot stand in for the report.
        Check(shutdown.Succeeded, $"The lost-report child refused a valid shutdown: {shutdown.Error}");

        Check(child.Process.WaitForExit((int)ProcessTimeout.TotalMilliseconds),
            "A child that could not write its shutdown report kept waiting for a stop signal instead of exiting.");
        child.Output.CollectRemaining(ChildProcessOutput.DrainBudget);
        Check(child.Process.ExitCode == 6,
            $"A lost shutdown report must exit 6, not {child.Process.ExitCode}.");
        var stderr = child.Output.StandardError;
        Check(stderr.Contains("failed to write the shutdown report", StringComparison.Ordinal),
            $"A lost shutdown report left no diagnostic on stderr: {(stderr.Length == 0 ? "(empty)" : stderr)}");

        Directory.Delete(reportPath, recursive: true);
    }

    private static void RunFirstLaunchRace(string temporaryRoot, List<Child> children)
    {
        var key = Guid.NewGuid().ToString("N");
        var left = StartChild(key, Path.Combine(temporaryRoot, "race-left.jsonl"));
        var right = StartChild(key, Path.Combine(temporaryRoot, "race-right.jsonl"));
        children.Add(left);
        children.Add(right);

        var deadline = Stopwatch.StartNew();
        Child? winner = null;
        Child? loser = null;
        Report? ready = null;

        // Reads both reports and applies the two win conditions to that one snapshot.
        bool TryResolveRace()
        {
            var leftReports = ReadReports(left.ReportPath);
            var rightReports = ReadReports(right.ReportPath);
            if (leftReports.LastOrDefault(report => report.State == "ready") is { } leftReady
                && rightReports.Any(report => report.State == "busy"))
            {
                winner = left;
                loser = right;
                ready = leftReady;
                return true;
            }
            if (rightReports.LastOrDefault(report => report.State == "ready") is { } rightReady
                && leftReports.Any(report => report.State == "busy"))
            {
                winner = right;
                loser = left;
                ready = rightReady;
                return true;
            }

            return false;
        }

        while (deadline.Elapsed < ProcessTimeout)
        {
            if (TryResolveRace()) break;
            var leftExited = left.Process.HasExited;
            var rightExited = right.Process.HasExited;
            if (leftExited || rightExited)
            {
                // A child writes its record and exits immediately afterwards, so the snapshot
                // above can predate a record that is on disk by the time the exit is observed.
                // Re-read before calling an exit unexplained; otherwise the loser's ordinary
                // "busy" record is reported as a child that exited without one.
                if (TryResolveRace()) break;
                if ((leftExited && ReadReports(left.ReportPath).Length == 0)
                    || (rightExited && ReadReports(right.ReportPath).Length == 0))
                {
                    throw new InvalidOperationException(
                        ChildProcessReportWait.Describe(left.Process, left.Output, left.ReportPath,
                            "A first-launch desktop instance race child exited without a report")
                        + Environment.NewLine
                        + ChildProcessReportWait.Describe(right.Process, right.Output, right.ReportPath, "other race child"));
                }
            }

            Thread.Sleep(25);
        }

        if (winner is null || loser is null || ready is null)
        {
            throw new TimeoutException(
                ChildProcessReportWait.Describe(left.Process, left.Output, left.ReportPath, "first-launch race left")
                + Environment.NewLine
                + ChildProcessReportWait.Describe(right.Process, right.Output, right.ReportPath, "first-launch race right")
                + Environment.NewLine
                + "A first-launch desktop instance race did not produce exactly one owner.");
        }
        var raceWinner = winner!;
        var raceLoser = loser!;
        var raceReady = ready!;
        Check(raceReady.ProcessId == raceWinner.Process.Id, "The first-launch race winner reported the wrong process ID.");

        var pipeName = DesktopInstancePipe.ForTests(key);
        Check(raceReady.PipeName == pipeName, "First-launch race child and parent derived different test pipe names.");
        var status = DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Status, RequestTimeout)
            .GetAwaiter().GetResult();
        Check(status.Succeeded && status.ProcessId == raceReady.ProcessId && status.Version == raceReady.VersionText,
            $"The first-launch race winner did not answer status with its own identity (ok={status.Succeeded}, error={status.Error}, pid={status.ProcessId}/{raceReady.ProcessId}, version={status.Version}/{raceReady.VersionText}).");
        var shutdown = DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Shutdown, RequestTimeout,
            expectedInstance: status)
            .GetAwaiter().GetResult();
        Check(shutdown.Succeeded, $"First-launch race winner shutdown failed: {shutdown.Error}");
        WaitForReport(raceWinner, report => report.State == "shutdown");
        Check(raceWinner.Process.WaitForExit((int)ProcessTimeout.TotalMilliseconds), "First-launch race winner did not exit.");
        Check(raceLoser.Process.WaitForExit((int)ProcessTimeout.TotalMilliseconds), "First-launch race loser did not exit.");
    }

    public static int RunChild(string key, string reportPath)
    {
        if (!Guid.TryParseExact(key, "N", out _) || !Path.IsPathFullyQualified(reportPath)) return 2;

        reportPath = Path.GetFullPath(reportPath);
        var leaseName = @"Local\CycleArc-test-" + key;
        DesktopInstanceLease? lease;
        try
        {
            lease = DesktopInstanceLease.TryAcquire(leaseName);
        }
        catch (Exception)
        {
            TryAppendReport(reportPath, new { state = "error" });
            return 3;
        }

        if (lease is null)
        {
            TryAppendReport(reportPath, new { state = "busy", pid = Environment.ProcessId });
            return 4;
        }

        try
        {
            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reportGate = new object();
            var activationTotal = 0;
            var reportFailed = 0;
            void Report(object value)
            {
                lock (reportGate) AppendReport(reportPath, value);
            }

            // The IPC server answers before it runs a callback and then swallows whatever the
            // callback throws, so a report that cannot be written would otherwise disappear:
            // the parent sees a successful shutdown response and then waits out its whole
            // budget for a record this child never managed to append. Record the real
            // exception on stderr and let the child leave with a distinct exit code, so the
            // parent fails on a diagnosable exit instead of an unexplained timeout.
            bool TryReport(object value, string description)
            {
                try
                {
                    Report(value);
                    return true;
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref reportFailed, 1);
                    Console.Error.WriteLine($"desktop-instance child failed to write the {description} report to '{reportPath}': {ex}");
                    return false;
                }
            }

            var instanceInfo = DesktopInstanceInfo.Current();
            var server = new DesktopInstanceServer(new DesktopInstanceServerOptions
            {
                PipeName = DesktopInstancePipe.ForTests(key),
                InstanceInfo = instanceInfo,
            });
            try
            {
                _ = server.StartAsync(
                    onActivate: () =>
                    {
                        var count = Interlocked.Increment(ref activationTotal);
                        // A lost activation report can only end in a parent timeout, so stop here too.
                        if (!TryReport(new { state = "activate", activateCount = count }, "activate")) stop.TrySetResult();
                        return Task.CompletedTask;
                    },
                    onShutdown: () =>
                    {
                        // The report is this child's shutdown evidence. Releasing the stop signal
                        // in either case keeps a write failure from turning into a hang, and the
                        // recorded failure keeps it from being mistaken for a clean shutdown.
                        TryReport(new { state = "shutdown", activateCount = Volatile.Read(ref activationTotal) }, "shutdown");
                        stop.TrySetResult();
                        return Task.CompletedTask;
                    });
                Report(new
                {
                    state = "ready",
                    pid = instanceInfo.ProcessId,
                    exePath = instanceInfo.ExecutablePath,
                    versionText = instanceInfo.Version,
                    pipeName = DesktopInstancePipe.ForTests(key),
                });
                stop.Task.GetAwaiter().GetResult();
                // A child that could not record its reports never reports success.
                return Volatile.Read(ref reportFailed) == 0 ? 0 : 6;
            }
            catch (Exception)
            {
                TryAppendReport(reportPath, new { state = "error" });
                return 5;
            }
            finally
            {
                try { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception) { }
            }
        }
        finally
        {
            // DesktopInstanceLease requires release on the acquiring thread. This child intentionally
            // blocks that thread while the named-pipe server runs on its asynchronous worker.
            lease.Dispose();
        }
    }

    public static int RunChildGuarded(string key, string reportPath)
    {
        SetErrorMode(SemNoGpFaultErrorBox);
        try { return RunChild(key, reportPath); }
        catch (Exception ex)
        {
            TryAppendReport(reportPath, new { state = "error", exception = ex.ToString() });
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Child StartChild(string key, string reportPath)
    {
        var hostPath = Environment.ProcessPath ?? throw new InvalidOperationException("The UiSmoke process has no executable path.");
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(hostPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entryAssemblyPath)
            && string.Equals(Path.GetExtension(entryAssemblyPath), ".dll", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(entryAssemblyPath);
        startInfo.ArgumentList.Add(ChildArgument);
        startInfo.ArgumentList.Add(key);
        startInfo.ArgumentList.Add(Path.GetFullPath(reportPath));

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the desktop instance child process.");
        return new Child(process, reportPath, new ChildProcessOutput(process));
    }

    private static Report WaitForReport(Child child, Func<Report, bool> predicate)
    {
        return ChildProcessReportWait.WaitFor(
            () => ReadReports(child.ReportPath).LastOrDefault(predicate),
            child.Process,
            child.Output,
            child.ReportPath,
            ProcessTimeout,
            $"desktop instance report '{child.ReportPath}'");
    }

    private static void StopChild(Child child)
    {
        try
        {
            if (!child.Process.HasExited)
            {
                child.Process.Kill(entireProcessTree: true);
                child.Process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally
        {
            child.Output.CollectRemaining(ChildProcessOutput.DrainBudget);
            child.Output.Dispose();
            child.Process.Dispose();
        }
    }

    private static Report[] ReadReports(string reportPath)
    {
        // A report the child has not created yet is an ordinary "not there yet", not a failure.
        if (!File.Exists(reportPath)) return Array.Empty<Report>();
        try
        {
            var reports = new List<Report>();
            foreach (var line in File.ReadLines(reportPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("state", out var stateElement)
                        || stateElement.ValueKind != JsonValueKind.String)
                        continue;
                    var state = stateElement.GetString();
                    if (string.IsNullOrWhiteSpace(state)) continue;
                    var processId = root.TryGetProperty("pid", out var pidElement) && pidElement.TryGetInt32(out var pid) ? pid : 0;
                    var executablePath = root.TryGetProperty("exePath", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
                        ? pathElement.GetString() ?? string.Empty : string.Empty;
                    var version = root.TryGetProperty("versionText", out var versionElement) && versionElement.ValueKind == JsonValueKind.String
                        ? versionElement.GetString() ?? string.Empty : string.Empty;
                    var pipeName = root.TryGetProperty("pipeName", out var pipeElement) && pipeElement.ValueKind == JsonValueKind.String
                        ? pipeElement.GetString() ?? string.Empty : string.Empty;
                    var activateCount = root.TryGetProperty("activateCount", out var countElement) && countElement.TryGetInt32(out var count)
                        ? count : 0;
                    reports.Add(new Report(state, processId, executablePath, version, activateCount, pipeName));
                }
                catch (JsonException) { }
            }
            return reports.ToArray();
        }
        catch (IOException) { return Array.Empty<Report>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<Report>(); }
    }

    private static void AppendReport(string reportPath, object value)
    {
        ChildReportFile.Append(reportPath, JsonSerializer.Serialize(value));
    }

    private static void TryAppendReport(string reportPath, object value)
    {
        try { AppendReport(reportPath, value); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Child(Process Process, string ReportPath, ChildProcessOutput Output);

    private sealed record Report(string State, int ProcessId, string ExecutablePath, string VersionText, int ActivationCount, string PipeName);

    private const uint SemNoGpFaultErrorBox = 0x0002;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
