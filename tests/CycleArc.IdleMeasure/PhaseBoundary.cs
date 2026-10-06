namespace CycleArc.IdleMeasure;

internal readonly record struct PhaseCpuBoundary(ulong ProcessCycles, ulong UiThreadCycles, long RefreshSnapshotCalls);

internal readonly record struct BoundaryInventory<THandles, TThreads>(THandles Handles, TThreads Threads);

internal readonly record struct PhaseBoundarySnapshot<THandles, TThreads, TSample>(
    PhaseCpuBoundary Cpu, BoundaryInventory<THandles, TThreads> Inventory, TSample Sample);

internal interface IPhaseBoundaryDiagnostics<THandles, TThreads>
{
    THandles CaptureHandles();
    TThreads CaptureThreads();
    long RefreshSnapshotCalls();
    ulong ProcessCycles();
    ulong UiThreadCycles();
}

/// <summary>
/// Keeps allocating handle/thread inventories outside the process/UI cycle interval.
/// Phase samples are also captured outside that interval and inside the inventory boundaries.
/// Counter queries and the small amount of boundary bookkeeping still have nonzero cost.
/// </summary>
internal sealed class PhaseBoundary<THandles, TThreads>(IPhaseBoundaryDiagnostics<THandles, TThreads> diagnostics)
{
    public PhaseBoundarySnapshot<THandles, TThreads, TSample> CaptureStart<TSample>(Func<TSample> captureSample)
    {
        var handles = diagnostics.CaptureHandles();
        var threads = diagnostics.CaptureThreads();
        var sample = captureSample();
        var refreshes = diagnostics.RefreshSnapshotCalls();
        var processCycles = diagnostics.ProcessCycles();
        var uiCycles = diagnostics.UiThreadCycles();
        return new(new(processCycles, uiCycles, refreshes), new(handles, threads), sample);
    }

    public PhaseBoundarySnapshot<THandles, TThreads, TSample> CaptureEnd<TSample>(Func<TSample> captureSample)
    {
        var processCycles = diagnostics.ProcessCycles();
        var uiCycles = diagnostics.UiThreadCycles();
        var refreshes = diagnostics.RefreshSnapshotCalls();
        var sample = captureSample();
        var handles = diagnostics.CaptureHandles();
        var threads = diagnostics.CaptureThreads();
        return new(new(processCycles, uiCycles, refreshes), new(handles, threads), sample);
    }
}
