using CycleArc.IdleMeasure;

namespace CycleArc.Tests;

public sealed class PhaseBoundaryTests
{
    [Fact]
    public void PhaseBoundaries_KeepInventoriesOutsideCycleInterval()
    {
        var calls = new List<string>();
        var diagnostics = new FakeDiagnostics(calls);
        var boundary = new PhaseBoundary<string, string>(diagnostics);

        var before = boundary.CaptureStart(() => { calls.Add("StartSample"); return "before"; });
        calls.Add("START_PHASE");
        calls.Add("END_PHASE");
        var after = boundary.CaptureEnd(() => { calls.Add("EndSample"); return "after"; });

        Assert.Equal(new[]
        {
            "HandleInventory", "ThreadInventory", "StartSample", "RefreshSnapshotCalls", "ProcessCycles", "UiCycles",
            "START_PHASE", "END_PHASE",
            "ProcessCycles", "UiCycles", "RefreshSnapshotCalls", "EndSample", "HandleInventory", "ThreadInventory"
        }, calls);
        Assert.Equal(new PhaseCpuBoundary(100, 10, 1), before.Cpu);
        Assert.Equal(new PhaseCpuBoundary(200, 20, 2), after.Cpu);
        Assert.Equal(new BoundaryInventory<string, string>("handles-1", "threads-1"), before.Inventory);
        Assert.Equal(new BoundaryInventory<string, string>("handles-2", "threads-2"), after.Inventory);
        Assert.Equal("before", before.Sample);
        Assert.Equal("after", after.Sample);
    }

    [Fact]
    public void StartInventoryFailure_DoesNotBeginCpuInterval()
    {
        var calls = new List<string>();
        var boundary = new PhaseBoundary<string, string>(new FakeDiagnostics(calls) { FailThreads = true });

        Assert.Throws<InvalidOperationException>(() => boundary.CaptureStart(() => "before"));

        Assert.Equal(new[] { "HandleInventory", "ThreadInventory" }, calls);
    }

    private sealed class FakeDiagnostics(List<string> calls) : IPhaseBoundaryDiagnostics<string, string>
    {
        private int _handles, _threads, _refreshes, _process, _ui;
        public bool FailThreads { get; init; }
        public string CaptureHandles() { calls.Add("HandleInventory"); return "handles-" + ++_handles; }
        public string CaptureThreads()
        {
            calls.Add("ThreadInventory");
            if (FailThreads) throw new InvalidOperationException("Fake inventory failure.");
            return "threads-" + ++_threads;
        }
        public long RefreshSnapshotCalls() { calls.Add("RefreshSnapshotCalls"); return ++_refreshes; }
        public ulong ProcessCycles() { calls.Add("ProcessCycles"); return (ulong)++_process * 100; }
        public ulong UiThreadCycles() { calls.Add("UiCycles"); return (ulong)++_ui * 10; }
    }
}
