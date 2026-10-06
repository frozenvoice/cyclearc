using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CycleArc.IdleMeasure;

/// <summary>Kernel handles of this process grouped by object type name, e.g. Event, Thread, File.</summary>
internal sealed record HandleTypeCounts(int Total, IReadOnlyDictionary<string, int> ByType);

/// <summary>Cumulative CPU per native thread of this process; the UI thread is identified separately.</summary>
internal sealed record ThreadCpuSnapshot(int UiThreadId, IReadOnlyDictionary<int, double> CpuMilliseconds);

// Harness-only boundary diagnostics. They query only the current process and allocate,
// so they run at phase boundaries and transitions, never on the per-second sampler.
internal static class ProcessInventory
{
    private const int ProcessHandleInformation = 51;
    private const int ObjectTypesInformation = 3;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private static Dictionary<int, string>? _typeNames;
    // The pseudo-handle: querying through it opens no handle of its own.
    private static readonly IntPtr CurrentProcess = new(-1);

    public static HandleTypeCounts CaptureHandles()
    {
        var names = _typeNames ??= TypeNames();
        var buffer = Query((pointer, length) => NtQueryInformationProcess(CurrentProcess,
            ProcessHandleInformation, pointer, length, out var needed) is var status && status == StatusInfoLengthMismatch
                ? (status, needed) : (status, length));
        try
        {
            var count = (int)Marshal.ReadInt64(buffer);
            var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                // PROCESS_HANDLE_TABLE_ENTRY_INFO (x64): HandleValue, HandleCount, PointerCount,
                // GrantedAccess, ObjectTypeIndex, HandleAttributes, Reserved = 40 bytes.
                var entry = buffer + 16 + index * 40;
                var type = Marshal.ReadInt32(entry + 28);
                var name = names.TryGetValue(type, out var known) ? known : "type-" + type;
                byType[name] = byType.GetValueOrDefault(name) + 1;
            }
            return new(count, byType);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static ThreadCpuSnapshot CaptureThreads(int uiThreadId)
    {
        using var process = Process.GetCurrentProcess();
        var cpu = new Dictionary<int, double>();
        foreach (ProcessThread thread in process.Threads)
        {
            try { cpu[thread.Id] = thread.TotalProcessorTime.TotalMilliseconds; }
            catch (InvalidOperationException) { } // The thread exited while enumerating.
            catch (Win32Exception) { }
            finally { thread.Dispose(); }
        }
        return new(uiThreadId, cpu);
    }

    /// <summary>CPU per thread between two snapshots; the UI thread, the busiest others and the rest.</summary>
    public static IReadOnlyDictionary<string, double> CpuDelta(ThreadCpuSnapshot before, ThreadCpuSnapshot after, int top = 4)
    {
        var deltas = after.CpuMilliseconds.Select(pair => (pair.Key,
            Delta: pair.Value - before.CpuMilliseconds.GetValueOrDefault(pair.Key))).Where(pair => pair.Delta > 0).ToList();
        var result = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["ui"] = deltas.Where(pair => pair.Key == after.UiThreadId).Sum(pair => pair.Delta)
        };
        var others = deltas.Where(pair => pair.Key != after.UiThreadId).OrderByDescending(pair => pair.Delta).ToList();
        for (var index = 0; index < Math.Min(top, others.Count); index++)
            result["thread-" + others[index].Key] = others[index].Delta;
        result["rest"] = others.Skip(top).Sum(pair => pair.Delta);
        return result;
    }

    [DllImport("kernel32.dll")]
    public static extern int GetCurrentThreadId();

    /// <summary>CPU cycles charged to this process; not quantized to the 15.6 ms clock tick.</summary>
    public static ulong ProcessCycles() => QueryProcessCycleTime(CurrentProcess, out var cycles) ? cycles : 0;

    /// <summary>CPU cycles charged to the calling thread; call on the UI thread for UI attribution.</summary>
    public static ulong CurrentThreadCycles() => QueryThreadCycleTime(new IntPtr(-2), out var cycles) ? cycles : 0;

    [DllImport("kernel32.dll")]
    private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    private static Dictionary<int, string> TypeNames()
    {
        var buffer = Query((pointer, length) => NtQueryObject(IntPtr.Zero, ObjectTypesInformation, pointer, length, out var needed)
            is var status && status == StatusInfoLengthMismatch ? (status, Math.Max(needed, length * 2)) : (status, length));
        try
        {
            var names = new Dictionary<int, string>();
            var count = Marshal.ReadInt32(buffer);
            var entry = buffer + 8;
            for (var index = 0; index < count; index++)
            {
                // OBJECT_TYPE_INFORMATION (x64): UNICODE_STRING TypeName at 0, TypeIndex at 90, 104 bytes,
                // followed by the name buffer; each entry is pointer-aligned.
                var length = (ushort)Marshal.ReadInt16(entry);
                var maximum = (ushort)Marshal.ReadInt16(entry + 2);
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry + 8), length / 2);
                names[Marshal.ReadByte(entry + 90)] = name;
                entry = (IntPtr)(((long)entry + 104 + maximum + 7) & ~7L);
            }
            return names;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IntPtr Query(Func<IntPtr, int, (int Status, int Needed)> query)
    {
        var length = 64 * 1024;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(length);
            var (status, needed) = query(buffer, length);
            if (status >= 0) return buffer;
            Marshal.FreeHGlobal(buffer);
            if (status != StatusInfoLengthMismatch || length >= 64 * 1024 * 1024)
                throw new InvalidOperationException($"Native inventory query failed: 0x{status:X8}.");
            length = Math.Max(needed + 4096, length * 2);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int informationClass, IntPtr information, int length, out int returnLength);
}
