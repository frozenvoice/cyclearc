using System.ComponentModel;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace CycleArc.IdleMeasure;

// Test-process instrumentation only. No collections, working-set trims, heap walks,
// process dumps, or EventPipe sessions are induced by this sampler.
internal sealed class ProcessMetricsSampler
{
    private readonly nint _process = Native.GetCurrentProcess();
    private int? _threadCount;
    private DateTime? _threadCountSnapshotUtc;
    private string? _threadCountFailure;

    public ProcessMetricsSampler()
    {
        RefreshThreadCount();
    }

    public ProcessSample Capture(string phase, double elapsedSeconds)
    {
        var timestamp = DateTime.UtcNow;
        var gc = GC.GetGCMemoryInfo();
        bool hasGc = gc.Index != 0;
        long managedAllocated = GC.GetTotalMemory(false);
        long allocatedApprox = GC.GetTotalAllocatedBytes(precise: false);
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        double pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;

        var memory = ReadMemory();
        double cpuMs = ReadCpuMilliseconds();
        int? handles = Native.GetProcessHandleCount(_process, out uint count)
            ? checked((int)count) : null;
        int? gdi = ReadGuiResourceCount(0);
        int? user = ReadGuiResourceCount(1);
        string? failure = handles is null || gdi is null || user is null
            ? "One or more handle/resource counters were unavailable."
            : _threadCountFailure;

        return new ProcessSample(
            timestamp, phase, elapsedSeconds,
            checked((long)memory.WorkingSetSize), memory.PrivateWorkingSetSize,
            checked((long)memory.PrivateUsage), managedAllocated, allocatedApprox,
            gen0, gen1, gen2, pauseMs, gc.Index, hasGc,
            hasGc ? gc.HeapSizeBytes : null,
            hasGc ? gc.FragmentedBytes : null,
            hasGc ? gc.TotalCommittedBytes : null,
            hasGc ? checked((long)memory.PrivateUsage) - gc.TotalCommittedBytes : null,
            cpuMs, handles, gdi, user, _threadCount, _threadCountSnapshotUtc,
            memory.PrivateWorkingSetUnavailableReason, failure);
    }

    // Opt in at phase boundaries only. VirtualQuery reports allocation types and
    // committed/reserved address ranges, not native heap ownership or residency.
    // A GC can race this scan; the last-GC commit snapshot remains explicitly dated
    // by its GC index. Image/mapped pages can also incur private copy-on-write commit.
    public NativeVirtualMemoryInventory CaptureVirtualMemoryInventory(string phase)
    {
        RefreshThreadCount();
        var timestamp = DateTime.UtcNow;
        Native.GetSystemInfo(out var system);
        nuint address = (nuint)system.MinimumApplicationAddress;
        nuint maximum = (nuint)system.MaximumApplicationAddress;
        nuint structureSize = checked((nuint)Marshal.SizeOf<MemoryBasicInformation>());
        long privateCommitted = 0, privateReserved = 0;
        long mappedCommitted = 0, mappedReserved = 0;
        long imageCommitted = 0, imageReserved = 0;
        long otherCommitted = 0, otherReserved = 0;
        int regions = 0;
        bool complete = true;
        string? reason = null;

        while (address <= maximum)
        {
            nuint returned = Native.VirtualQuery((nint)address, out var region, structureSize);
            if (returned == 0)
            {
                complete = false;
                reason = $"VirtualQuery failed at 0x{address:X}: Win32 {Marshal.GetLastWin32Error()}.";
                break;
            }

            ++regions;
            long size = checked((long)region.RegionSize);
            if (region.State == Native.MemCommit)
            {
                switch (region.Type)
                {
                    case Native.MemPrivate: privateCommitted += size; break;
                    case Native.MemMapped: mappedCommitted += size; break;
                    case Native.MemImage: imageCommitted += size; break;
                    default: otherCommitted += size; break;
                }
            }
            else if (region.State == Native.MemReserve)
            {
                switch (region.Type)
                {
                    case Native.MemPrivate: privateReserved += size; break;
                    case Native.MemMapped: mappedReserved += size; break;
                    case Native.MemImage: imageReserved += size; break;
                    default: otherReserved += size; break;
                }
            }

            nuint next = (nuint)region.BaseAddress + region.RegionSize;
            if (region.RegionSize == 0 || next <= address)
            {
                complete = false;
                reason = "VirtualQuery returned a zero-sized or non-progressing address range.";
                break;
            }
            address = next;
        }

        var gc = GC.GetGCMemoryInfo();
        var memory = ReadMemory();
        long privateBytes = checked((long)memory.PrivateUsage);
        return new NativeVirtualMemoryInventory(
            timestamp, phase, complete, reason, regions,
            privateCommitted, privateReserved, mappedCommitted, mappedReserved,
            imageCommitted, imageReserved, otherCommitted, otherReserved,
            privateBytes, gc.Index,
            gc.Index != 0 ? gc.TotalCommittedBytes : null,
            gc.Index != 0 ? privateBytes - gc.TotalCommittedBytes : null,
            _threadCount, _threadCountSnapshotUtc);
    }

    // Process.Threads allocates managed wrappers and refreshes OS process metadata.
    // Keep this out of the once-per-second path; the sample carries its timestamp.
    public void RefreshThreadCount()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var threads = process.Threads;
            _threadCount = threads.Count;
            _threadCountSnapshotUtc = DateTime.UtcNow;
            _threadCountFailure = null;
            foreach (ProcessThread thread in threads)
                thread.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            _threadCount = null;
            _threadCountSnapshotUtc = DateTime.UtcNow;
            _threadCountFailure = $"Thread count unavailable: {ex.GetType().Name}.";
        }
    }

    public static RuntimeIdentity CaptureRuntimeIdentity() => new(
        RuntimeInformation.FrameworkDescription,
        Environment.Version.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.OSDescription,
        Environment.ProcessorCount,
        GCSettings.IsServerGC,
        GCSettings.LatencyMode.ToString(),
        GC.GetConfigurationVariables());

    private ProcessMemory ReadMemory()
    {
        var extended = new ProcessMemoryCountersEx2();
        extended.Cb = checked((uint)Marshal.SizeOf<ProcessMemoryCountersEx2>());
        if (Native.GetProcessMemoryInfoEx2(_process, out extended, extended.Cb))
            return new ProcessMemory(extended.WorkingSetSize, extended.PrivateUsage,
                checked((long)extended.PrivateWorkingSetSize), null);

        int extendedError = Marshal.GetLastWin32Error();
        var original = new ProcessMemoryCountersEx();
        original.Cb = checked((uint)Marshal.SizeOf<ProcessMemoryCountersEx>());
        if (!Native.GetProcessMemoryInfoEx(_process, out original, original.Cb))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read this measurement process's memory counters.");

        return new ProcessMemory(original.WorkingSetSize, original.PrivateUsage, null,
            $"PROCESS_MEMORY_COUNTERS_EX2 unavailable (Win32 {extendedError}); EX fallback has no private working-set field.");
    }

    private double ReadCpuMilliseconds()
    {
        if (!Native.GetProcessTimes(_process, out _, out _, out var kernel, out var user))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read this measurement process's CPU times.");
        return (kernel.Ticks + user.Ticks) / 10_000.0;
    }

    private int? ReadGuiResourceCount(uint kind)
    {
        Native.SetLastError(0);
        uint count = Native.GetGuiResources(_process, kind);
        if (count == 0 && Marshal.GetLastWin32Error() != 0)
            return null;
        return checked((int)count);
    }

    private readonly record struct ProcessMemory(nuint WorkingSetSize, nuint PrivateUsage,
        long? PrivateWorkingSetSize, string? PrivateWorkingSetUnavailableReason);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage;
        public nuint PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage;
        public nuint PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low, High;
        public readonly ulong Ticks => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        // Native alignment includes the x64 PartitionId/reserved WORDs here.
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemInfo
    {
        public ushort ProcessorArchitecture, Reserved;
        public uint PageSize;
        public nint MinimumApplicationAddress, MaximumApplicationAddress;
        public nuint ActiveProcessorMask;
        public uint NumberOfProcessors, ProcessorType, AllocationGranularity;
        public ushort ProcessorLevel, ProcessorRevision;
    }

    private static class Native
    {
        internal const uint MemCommit = 0x1000, MemReserve = 0x2000;
        internal const uint MemPrivate = 0x20000, MemMapped = 0x40000, MemImage = 0x1000000;

        [DllImport("kernel32.dll")]
        internal static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessMemoryInfoEx2(nint process, out ProcessMemoryCountersEx2 counters, uint size);

        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessMemoryInfoEx(nint process, out ProcessMemoryCountersEx counters, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(nint process, out NativeFileTime creation,
            out NativeFileTime exit, out NativeFileTime kernel, out NativeFileTime user);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessHandleCount(nint process, out uint count);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetGuiResources(nint process, uint flags);

        [DllImport("kernel32.dll")]
        internal static extern void SetLastError(uint error);

        [DllImport("kernel32.dll")]
        internal static extern void GetSystemInfo(out SystemInfo systemInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nuint VirtualQuery(nint address, out MemoryBasicInformation information, nuint length);
    }
}

internal readonly record struct ProcessSample(
    DateTime TimestampUtc, string Phase, double ElapsedSeconds,
    long WorkingSetBytes, long? PrivateWorkingSetBytes, long PrivateBytes,
    long ManagedAllocatedBytes, long TotalAllocatedBytesApprox,
    int Gen0Collections, int Gen1Collections, int Gen2Collections,
    double TotalGcPauseMilliseconds, long LastGcIndex, bool HasGcSnapshot,
    long? LastGcHeapBytes, long? LastGcFragmentedBytes, long? LastGcCommittedBytes,
    long? NonGcPrivateCommitResidualEstimateBytes, double CpuTotalMilliseconds,
    int? HandleCount, int? GdiObjectCount, int? UserObjectCount, int? ThreadCount,
    DateTime? ThreadCountSnapshotUtc, string? PrivateWorkingSetUnavailableReason,
    string? NativeMetricsFailureReason);

internal readonly record struct NativeVirtualMemoryInventory(
    DateTime TimestampUtc, string Phase, bool Complete, string? FailureReason, int RegionCount,
    long PrivateCommittedBytes, long PrivateReservedBytes,
    long MappedCommittedBytes, long MappedReservedBytes,
    long ImageCommittedBytes, long ImageReservedBytes,
    long OtherCommittedBytes, long OtherReservedBytes,
    long ProcessPrivateBytes, long LastGcIndex, long? LastGcCommittedBytes,
    long? NonGcPrivateCommitResidualEstimateBytes,
    int? ThreadCount, DateTime? ThreadCountSnapshotUtc);

internal sealed record RuntimeIdentity(
    string FrameworkDescription, string RuntimeVersion, string ProcessArchitecture,
    string OSDescription, int ProcessorCount, bool IsServerGc, string GcLatencyMode,
    IReadOnlyDictionary<string, object> GcConfiguration);
