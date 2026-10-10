using System.Diagnostics;
using CycleArc.Codex;

namespace CycleArc.Tests;

/// <summary>Observes only synthetic children; production code still owns all pipe I/O and deadlines.</summary>
internal sealed class CodexProcessFixture : ICodexProcessFactory, IDisposable
{
    private readonly object _gate = new();
    private Process? _process;
    private ICodexProcess? _preparedProcess;
    private CodexLaunchCommand? _preparedCommand;
    private bool _bootstrapAttempted;
    private bool _preparedReady;
    private bool _disposed;
    private string _start = "not-started";
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    public string Root { get; } = Directory.CreateTempSubdirectory("cyclearc-codex-process-").FullName;
    public string Arguments => $"--fixture-directory \"{Root}\"";
    public string StagePath(string stage) => Path.Combine(Root, stage);

    public ICodexProcess Start(CodexLaunchCommand command)
    {
        lock (_gate)
        {
            if (_bootstrapAttempted)
            {
                if (!_preparedReady || _preparedCommand != command || _preparedProcess is null)
                    throw new InvalidOperationException("The prepared fixture must be ready and claimed exactly once with its original command.");
                var prepared = _preparedProcess;
                _preparedProcess = null;
                return prepared;
            }
        }
        return StartReal(command);
    }

    public async Task BootstrapAsync(CodexLaunchCommand command)
    {
        lock (_gate)
        {
            if (_bootstrapAttempted || _process is not null || _disposed)
                throw new InvalidOperationException("Fixture bootstrap must be attempted exactly once before protocol startup.");
            _bootstrapAttempted = true;
        }
        var budget = Stopwatch.StartNew();
        var startup = Task.Run(() =>
        {
            var process = StartReal(command);
            lock (_gate)
            {
                if (_disposed)
                {
                    process.KillTree();
                    process.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    throw new ObjectDisposedException(nameof(CodexProcessFixture));
                }
                _preparedProcess = process;
                _preparedCommand = command;
            }
            return process;
        });
        ICodexProcess prepared;
        try { prepared = await startup.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException)
        {
            Assert.Fail("fixture-bootstrap process start exceeded its fixed 10s budget; " + Describe());
            throw;
        }
        while (budget.Elapsed < TimeSpan.FromSeconds(10) && !prepared.HasExited)
        {
            if (File.Exists(StagePath("ready")) && File.Exists(StagePath("child-ready")))
            {
                lock (_gate) _preparedReady = true;
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail($"fixture-bootstrap parent/descendant readiness failed within 10s; wait={budget.Elapsed}; {Describe()}");
    }

    private ICodexProcess StartReal(CodexLaunchCommand command)
    {
        lock (_gate) _start = "creating-process";
        ICodexProcess process;
        try { process = new CodexProcessFactory().Start(command); }
        catch (Exception ex)
        {
            lock (_gate) _start = $"start-failed:{ex.GetType().Name}:{ex.Message}";
            throw;
        }
        lock (_gate)
        {
            _start = $"started pid={process.ProcessId} after={_elapsed.Elapsed.TotalMilliseconds:n0}ms";
            if (_disposed)
            {
                // A delayed start must not escape fixture teardown.
                process.KillTree();
                process.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw new ObjectDisposedException(nameof(CodexProcessFixture));
            }
            try
            {
                _process = Process.GetProcessById(process.ProcessId!.Value);
                _ = _process.Handle; // Retain identity and exit code after production disposal.
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                _process?.Dispose();
                _process = null;
                _start += $" observation-failed:{ex.GetType().Name}";
                // Observation failure must not prevent the product from owning/reaping it.
            }
        }
        return process;
    }

    public async Task WaitForStageAsync(string stage, Task operation)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (File.Exists(StagePath(stage))) return;
            if (operation.IsCompleted) break;
            await Task.Delay(25);
        }
        Assert.True(File.Exists(StagePath(stage)),
            $"fixture-ready stage={stage} operation={operation.Status} wait={watch.Elapsed}; {Describe()}");
    }

    public string Describe()
    {
        lock (_gate)
        {
            var exit = _process is null ? "unobserved"
                : _process.HasExited ? _process.ExitCode.ToString() : "running";
            var stages = new[] { "bootstrap-gated", "script-entered", "before-spawn", "spawn-gated",
                "after-spawn", "child-script-entered", "child-start-gated", "ready", "child-ready", "initialize-received",
                "initialize-response", "account-read", "rate-limits-read", "exit-code" };
            return $"launch={_start} exit={exit} elapsed={_elapsed.Elapsed.TotalSeconds:n1}s "
                + string.Join(" ", stages.Select(stage => $"{stage}={ReadStage(stage)}"));
        }
    }

    private string ReadStage(string stage)
    {
        try
        {
            using var stream = new FileStream(StagePath(stage), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var buffer = new char[128];
            return new string(buffer, 0, reader.Read(buffer, 0, buffer.Length));
        }
        catch (IOException) { return "(missing)"; }
    }

    public void AssertExited()
    {
        lock (_gate)
            Assert.True(_process is { HasExited: true }, $"process-exit: {Describe()}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Fallback cleanup is separate from assertions above and owns only this fixture.
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException) { }
            finally
            {
                _process?.Dispose();
                _preparedProcess?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _preparedProcess = null;
            }
        }
        try { Directory.Delete(Root, true); }
        catch (IOException) { } // Never obscure the failed lifecycle assertion.
    }
}
