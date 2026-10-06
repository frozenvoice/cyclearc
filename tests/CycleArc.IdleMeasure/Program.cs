using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace CycleArc.IdleMeasure;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        var clock = Stopwatch.StartNew();
        Options? options = null;
        try
        {
            options = Options.Parse(args);
            var sampler = new ProcessMetricsSampler();
            var entry = sampler.Capture("entry", clock.Elapsed.TotalSeconds);
            var runtime = ProcessMetricsSampler.CaptureRuntimeIdentity();
            ValidateConfiguration(options.Configuration, runtime);
            var fixture = new SyntheticAccounts(options.FixtureRoot);
            int result = 1;
            var app = new AppHarness(fixture, async app =>
            {
                try
                {
                    await RunAsync(app, fixture, options, sampler, runtime, entry, clock);
                    result = 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    WriteJson(options.Output + ".failure.json", new { Status = "failed", Error = ex.ToString() });
                }
                finally
                {
                    try { await app.StopAsync(); await fixture.DisposeAsync(); }
                    catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                    app.Shutdown(result);
                }
            }) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // Match UiSmoke: App.xaml has an exact x:Class and cannot initialize a subclass.
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/CycleArc;component/UI/Themes.xaml", UriKind.Relative)
            });
            app.Run();
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (options is not null) WriteJson(options.Output + ".failure.json", new { Status = "failed", Error = ex.ToString() });
            return 1;
        }
    }

    private static async Task RunAsync(AppHarness app, SyntheticAccounts fixture, Options options,
        ProcessMetricsSampler sampler, RuntimeIdentity runtime, ProcessSample entry, Stopwatch clock)
    {
        using var cancellation = new CancellationTokenSource();
        var duration = options.WarmupSeconds + options.PhaseSeconds * 8;
        var measurements = new Measurements(sampler, app.Dispatcher, clock, duration);
        // Warm instrumentation and allocate buffers before the steady phases. No forced GC.
        var sampleTask = Task.Run(() => measurements.SampleAsync(cancellation.Token));
        var probeTask = Task.Run(() => measurements.ProbeAsync(cancellation.Token));
        var phases = new List<PhaseResult>(9);
        var inventories = new List<NativeVirtualMemoryInventory>(9);
        var transitions = new List<TransitionResult>(8 + ChurnCycles * 4);
        var phaseBoundary = new PhaseBoundary<HandleTypeCounts, ThreadCpuSnapshot>(new AppBoundaryDiagnostics(app));
        try
        {
            await app.InitializeAsync();
            await app.AssertDataWhenIdleAsync();
            var ready = sampler.Capture("ready", clock.Elapsed.TotalSeconds);
            var readyMilliseconds = clock.Elapsed.TotalMilliseconds;
            WriteJson(options.Output + ".ready.json", new { ProcessId = Environment.ProcessId, EntryToReadyMilliseconds = readyMilliseconds });
            await Phase("warmup", options.WarmupSeconds, false, false);
            await Phase("tray-idle", options.PhaseSeconds, false, false);
            // The widget before the popup has ever been created; the later widget phases keep
            // a created, hidden popup.
            await Transition("show-widget-before-flyout", () => app.ShowWidget(true));
            await Phase("widget-before-flyout", options.PhaseSeconds, false, true);
            await Transition("hide-widget-before-flyout", () => app.ShowWidget(false));
            await Transition("show-flyout", () => app.ShowFlyout(true));
            await Phase("flyout-visible", options.PhaseSeconds, true, false);
            await Transition("hide-flyout", () => app.ShowFlyout(false));
            await Phase("tray-after-flyout", options.PhaseSeconds, false, false);
            await Transition("show-widget", () => app.ShowWidget(true));
            await Phase("widget-visible", options.PhaseSeconds, false, true);
            measurements.Phase = "synthetic-refresh";
            var beforeRefresh = fixture.CaptureCounters();
            var refreshStart = clock.Elapsed.TotalMilliseconds;
            await app.RefreshAsync();
            await app.PublishAndReadPassiveAsync();
            var refreshMilliseconds = clock.Elapsed.TotalMilliseconds - refreshStart;
            var afterRefresh = fixture.CaptureCounters();
            Require(afterRefresh.CodexQuotaRequests >= beforeRefresh.CodexQuotaRequests + 2
                && afterRefresh.ClaudeLiveRequests >= beforeRefresh.ClaudeLiveRequests + 2
                && afterRefresh.CursorUsageRequests >= beforeRefresh.CursorUsageRequests + 1
                && afterRefresh.CursorHttpRequests >= beforeRefresh.CursorHttpRequests + 3,
                "Synthetic manual refresh must perform all providers' external requests.");
            await Phase("post-refresh-widget", options.PhaseSeconds, false, true);
            await Transition("hide-widget", () => app.ShowWidget(false));
            await Phase("tray-after-refresh", options.PhaseSeconds, false, false);
            // Repeated popup open/close and account switching with the widget shown. Each
            // cycle selects twice, so the five-account ring returns to its first selection.
            var firstSelection = app.SelectedAccountId;
            await Transition("show-widget-churn", () => app.ShowWidget(true));
            for (var cycle = 0; cycle < ChurnCycles; cycle++)
            {
                await Transition("churn-open-flyout", () => app.ShowFlyout(true));
                await Task.Delay(ChurnPauseMilliseconds);
                await Transition("churn-switch-visible", app.SelectNextAccount);
                await Task.Delay(ChurnPauseMilliseconds);
                await Transition("churn-close-flyout", () => app.ShowFlyout(false));
                await Task.Delay(ChurnPauseMilliseconds);
                await Transition("churn-switch-hidden", app.SelectNextAccount);
                await Task.Delay(ChurnPauseMilliseconds);
            }
            Require(app.SelectedAccountId == firstSelection, "Account churn did not return to its first selection.");
            await Phase("widget-after-churn", options.PhaseSeconds, false, true);
            await app.AssertDataWhenIdleAsync();
            Require(app.PassiveTicks >= duration / 3, "Two-second passive timer stopped or lost excessive ticks.");
            cancellation.Cancel();
            await Task.WhenAll(sampleTask, probeTask);
            var final = sampler.Capture("final", clock.Elapsed.TotalSeconds);
            // Stop/dispose only after all measured endpoints. Report serialization is not timed.
            await app.StopAsync();
            var production = typeof(App).Assembly;
            WriteJson(options.Output, new
            {
                SchemaVersion = 1, Status = "complete", options.Configuration, options.Trial,
                options.PhaseSeconds, options.WarmupSeconds, ProcessId = Environment.ProcessId,
                FixtureRoot = options.FixtureRoot, Runtime = runtime,
                ProductionAssembly = production.Location,
                ProductionVersion = production.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                HarnessVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                EntryToReadyMilliseconds = readyMilliseconds, Entry = entry, Ready = ready, Final = final,
                RefreshMilliseconds = refreshMilliseconds, BeforeRefresh = beforeRefresh, AfterRefresh = afterRefresh,
                Phases = phases, Transitions = transitions, VirtualMemoryInventories = inventories,
                Samples = measurements.Samples.Take(measurements.SampleCount).ToArray(),
                UiProbes = measurements.Probes.Take(measurements.ProbeCount).ToArray(),
                Counters = fixture.CaptureCounters(), app.PassiveTicks, app.AutomaticTicks, app.DisplayTicks,
                UiCreations = app.UiCounts,
                Assertions = new[] { "five isolated accounts", "latest passive values and original timestamps",
                    "all providers performed refresh requests", "production window visibility", "passive timer remains active",
                    "account churn returns to its first selection" },
                Limitations = new[] { "Synthetic current-source WPF harness; ordinary installed bootstrap, IPC, updater and discovery excluded",
                    "External provider transport is synthetic with cancellable 10ms responses",
                    "UI latency is a dispatcher/scheduling proxy, not click-to-pixel latency",
                    "GC pauses are cumulative; individual maximum pauses are not observed",
                    "ManagedAllocatedBytes is GC.GetTotalMemory(false), not retained live objects",
                    "Last-GC sizes are dated snapshots; private-minus-GC commit is not native heap attribution",
                    "Instrumentation and fixture memory are included equally in every configuration",
                    "Handle/thread inventories are outside process/UI cycle intervals; counter queries and boundary bookkeeping still have nonzero cost",
                    "Per-thread CPU snapshots span boundary enumeration/query work and are auxiliary diagnostics, not the process/UI cycle interval",
                    "UI creation counts compare displayed objects by reference after fixture calls; objects replaced within one call are not counted" }
            });
            Console.WriteLine("PASS: isolated idle measurement, provider requests, latest data, account isolation and visibility.");

            async Task Transition(string name, Action action)
            {
                measurements.Phase = name;
                var ui = app.UiCounts;
                var start = clock.Elapsed.TotalMilliseconds;
                action();
                var returned = clock.Elapsed.TotalMilliseconds;
                // WPF render/layout priorities precede ApplicationIdle. This is still
                // a dispatcher milestone, not a physical screen-present timestamp.
                var idle = await app.Dispatcher.InvokeAsync(() => clock.Elapsed.TotalMilliseconds, DispatcherPriority.ApplicationIdle);
                transitions.Add(new TransitionResult(name, start, returned - start, idle - start, app.UiCounts.Minus(ui)));
            }

            async Task Phase(string phase, int seconds, bool flyout, bool widget)
            {
                measurements.Phase = phase;
                WriteJson(options.Output + ".progress.json", new { phase, ProcessId = Environment.ProcessId, ElapsedSeconds = clock.Elapsed.TotalSeconds });
                app.AssertVisibility(flyout, widget);
                // START: handle/thread inventory, phase sample, then process/UI cycles last.
                // END: process/UI cycles first, phase sample, then handle/thread inventory.
                // Per-thread snapshots span the inventories and are only auxiliary attribution.
                Func<PhaseSample> captureSample = CapturePhaseSample;
                var before = phaseBoundary.CaptureStart(captureSample);
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                var after = phaseBoundary.CaptureEnd(captureSample);
                measurements.Phase = "boundary-inventory";
                app.AssertVisibility(flyout, widget);
                await app.AssertDataWhenIdleAsync();
                phases.Add(new PhaseResult(phase, before.Sample.Metrics, after.Sample.Metrics,
                    before.Sample.Counters, after.Sample.Counters, before.Sample.Ui, after.Sample.Ui,
                    BoundaryDiagnostics.From(before.Cpu, before.Inventory), BoundaryDiagnostics.From(after.Cpu, after.Inventory),
                    ProcessInventory.CpuDelta(before.Inventory.Threads, after.Inventory.Threads)));
                inventories.Add(sampler.CaptureVirtualMemoryInventory(phase));

                PhaseSample CapturePhaseSample() => new(sampler.Capture(phase, clock.Elapsed.TotalSeconds),
                    fixture.CaptureCounters(), app.UiCounts);
            }
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(sampleTask, probeTask);
        }
    }

    private const int ChurnCycles = 5;
    private const int ChurnPauseMilliseconds = 250;

    private static void ValidateConfiguration(string label, RuntimeIdentity runtime)
    {
        Require(!runtime.IsServerGc, "Expected the production workstation GC default.");
        Require(runtime.GcConfiguration.TryGetValue("GCConserveMem", out var conserve), "GCConserveMem unavailable.");
        Require(runtime.GcConfiguration.TryGetValue("ConcurrentGC", out var concurrent), "ConcurrentGC unavailable.");
        Require(Convert.ToInt32(conserve) == (label == "conserve5" ? 5 : label == "conserve7" ? 7 : 0), "GC conservation setting did not apply.");
        Require(Convert.ToBoolean(concurrent) == (label != "concurrent-off"), "Concurrent GC setting did not apply.");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void WriteJson(string path, object value)
    {
        File.WriteAllText(path + ".writing", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".writing", path, true);
    }

    private sealed record Options(string Output, string FixtureRoot, int PhaseSeconds, int WarmupSeconds, string Configuration, int Trial)
    {
        public static Options Parse(string[] args)
        {
            Require(args.Length == 12, "Six explicit option/value pairs are required; use scripts/Measure-Idle.ps1.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2) values.Add(args[i], args[i + 1]);
            string output = Path.GetFullPath(values["--output"]), root = Path.GetFullPath(values["--fixture-root"]);
            Require(!File.Exists(output) && Directory.Exists(Path.GetDirectoryName(output)), "Output must be a new file in an existing report directory.");
            Require(Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any(), "Fixture root must be a fresh empty directory.");
            Require((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Fixture root cannot be a reparse point.");
            int phase = int.Parse(values["--phase-seconds"]), warmup = int.Parse(values["--warmup-seconds"]), trial = int.Parse(values["--trial"]);
            string config = values["--config-label"];
            Require(phase is >= 5 and <= 600 && warmup is >= 0 and <= 600 && trial is >= 1 and <= 3, "Invalid measurement duration/trial.");
            Require(config is "default" or "conserve5" or "conserve7" or "concurrent-off", "Unknown GC configuration.");
            return new Options(output, root, phase, warmup, config, trial);
        }
    }

    private sealed class Measurements(ProcessMetricsSampler sampler, Dispatcher dispatcher, Stopwatch clock, int seconds)
    {
        public readonly ProcessSample[] Samples = new ProcessSample[seconds + 180];
        public readonly UiProbe[] Probes = new UiProbe[(seconds + 180) * 2];
        public int SampleCount, ProbeCount;
        private string _phase = "startup";
        public string Phase { get => Volatile.Read(ref _phase); set => Volatile.Write(ref _phase, value); }

        public async Task SampleAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    Require(SampleCount < Samples.Length, "Sample buffer exhausted.");
                    Samples[SampleCount++] = sampler.Capture(Phase, clock.Elapsed.TotalSeconds);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        public async Task ProbeAsync(CancellationToken token)
        {
            // Absolute intended due time includes GC/thread-pool delay before dispatch.
            double due = clock.Elapsed.TotalMilliseconds + 500;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, due - clock.Elapsed.TotalMilliseconds)), token);
                    var phase = Phase;
                    double queued = clock.Elapsed.TotalMilliseconds;
                    double executed = await dispatcher.InvokeAsync(() => clock.Elapsed.TotalMilliseconds, DispatcherPriority.Input, token);
                    Require(ProbeCount < Probes.Length, "UI probe buffer exhausted.");
                    Probes[ProbeCount++] = new UiProbe(phase, due, queued, executed,
                        Math.Max(0, executed - due), Math.Max(0, executed - queued));
                    due += 500;
                    if (due < executed) due += Math.Ceiling((executed - due) / 500) * 500;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    private readonly record struct UiProbe(string Phase, double DueMilliseconds, double QueuedMilliseconds,
        double ExecutedMilliseconds, double DueToExecutionMilliseconds, double QueueToExecutionMilliseconds);
    private sealed record PhaseResult(string Name, ProcessSample Start, ProcessSample End,
        FixtureCounters CountersBefore, FixtureCounters CountersAfter, UiCreationCounts UiBefore, UiCreationCounts UiAfter,
        BoundaryDiagnostics DiagnosticsBefore, BoundaryDiagnostics DiagnosticsAfter, IReadOnlyDictionary<string, double> ThreadCpuMilliseconds);

    private readonly record struct PhaseSample(ProcessSample Metrics, FixtureCounters Counters, UiCreationCounts Ui);

    // Preserve the schema-1 flattened diagnostics fields for old reports and summarizers.
    // Capture order is owned by PhaseBoundary, not by this serialization projection.
    private sealed record BoundaryDiagnostics(ulong ProcessCycles, ulong UiThreadCycles, long RefreshSnapshotCalls,
        HandleTypeCounts Handles)
    {
        public static BoundaryDiagnostics From(PhaseCpuBoundary cpu, BoundaryInventory<HandleTypeCounts, ThreadCpuSnapshot> inventory)
            => new(cpu.ProcessCycles, cpu.UiThreadCycles, cpu.RefreshSnapshotCalls, inventory.Handles);
    }

    private sealed class AppBoundaryDiagnostics(AppHarness app) : IPhaseBoundaryDiagnostics<HandleTypeCounts, ThreadCpuSnapshot>
    {
        public HandleTypeCounts CaptureHandles() => ProcessInventory.CaptureHandles();
        public ThreadCpuSnapshot CaptureThreads() => ProcessInventory.CaptureThreads(ProcessInventory.GetCurrentThreadId());
        public long RefreshSnapshotCalls() => app.RefreshSnapshotCalls;
        public ulong ProcessCycles() => ProcessInventory.ProcessCycles();
        public ulong UiThreadCycles() => ProcessInventory.CurrentThreadCycles();
    }
    private sealed record TransitionResult(string Name, double StartMilliseconds,
        double SynchronousActionMilliseconds, double UntilDispatcherIdleMilliseconds, UiCreationCounts UiCreated);
}
