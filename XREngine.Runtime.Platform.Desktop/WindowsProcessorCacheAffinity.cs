using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using XREngine.Execution;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>
/// Windows capability for <see cref="ProcessorCacheAffinity"/>: reads the L3 cache topology
/// with <c>GetLogicalProcessorInformationEx</c> and restricts the current process to the
/// selected logical processors.
/// </summary>
internal static partial class WindowsProcessorCacheAffinity
{
    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int RelationAll = 0xFFFF;
    private const int CacheTypeInstruction = 1;

    /// <summary>Applies the cache-core selection to the current process; see <see cref="ProcessorCacheAffinity.ApplyToCurrentProcess"/>.</summary>
    internal static int Apply(bool enabled, Action<string> log)
    {
        int processorCount = Environment.ProcessorCount;
        if (!OperatingSystem.IsWindows())
        {
            log("[CPU affinity] Cache-core selection is available only on Windows; affinity unchanged.");
            return processorCount;
        }

        return ApplyOnWindows(enabled, log, processorCount);
    }

    [SupportedOSPlatform("windows")]
    private static int ApplyOnWindows(bool enabled, Action<string> log, int processorCount)
    {
        List<ProcessorCacheGroup> caches = [];
        if (!TryReadTopology(caches, out int efficiencyClassCount, out string readFailure))
        {
            log($"[CPU affinity] Could not read the cache topology ({readFailure}); affinity unchanged.");
            return processorCount;
        }

        ProcessorCacheAffinitySelection selection = ProcessorCacheAffinity.Select(caches.ToArray());
        string hybridNote = efficiencyClassCount > 1
            ? $" The CPU has {efficiencyClassCount} core efficiency classes; they do not change affinity."
            : string.Empty;
        if (!selection.Applies)
        {
            log($"[CPU affinity] Not restricted: {selection.Reason}.{hybridNote}");
            return processorCount;
        }

        if (!enabled)
        {
            log($"[CPU affinity] Not restricted: disabled by PreferLargestCacheCores. Detected: {selection.Reason}.{hybridNote}");
            return processorCount;
        }

        if (selection.Group != 0 || processorCount > 64)
        {
            log($"[CPU affinity] Not restricted: the selection is outside processor group 0 or the system has more than 64 logical processors. Detected: {selection.Reason}.");
            return processorCount;
        }

        using Process process = Process.GetCurrentProcess();
        ulong current = unchecked((ulong)(long)process.ProcessorAffinity);
        ulong systemMask = processorCount >= 64 ? ulong.MaxValue : (1UL << processorCount) - 1UL;
        if ((current & systemMask) != systemMask)
        {
            log($"[CPU affinity] Not restricted: the process affinity was already set to 0x{current:X}. Detected: {selection.Reason}.");
            return BitOperations.PopCount(current & systemMask);
        }

        process.ProcessorAffinity = unchecked((nint)(long)selection.Mask);
        log($"[CPU affinity] Restricted the process: {selection.Reason}.{hybridNote}");
        return selection.LogicalProcessorCount;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe bool TryReadTopology(
        List<ProcessorCacheGroup> l3Caches,
        out int efficiencyClassCount,
        out string failure)
    {
        efficiencyClassCount = 0;
        failure = string.Empty;
        uint length = 0;
        _ = GetLogicalProcessorInformationEx(RelationAll, null, ref length);
        if (length == 0)
        {
            failure = $"error {Marshal.GetLastPInvokeError()}";
            return false;
        }

        byte[] buffer = new byte[length];
        fixed (byte* start = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, start, ref length))
            {
                failure = $"error {Marshal.GetLastPInvokeError()}";
                return false;
            }

            Span<bool> efficiencyClasses = stackalloc bool[256];
            uint offset = 0;
            while (offset + 8 <= length)
            {
                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship at 0, Size at 4, union at 8.
                byte* entry = start + offset;
                int relationship = *(int*)entry;
                uint size = *(uint*)(entry + 4);
                if (size == 0 || offset + size > length)
                    break;

                // CACHE_RELATIONSHIP: Level at 0, CacheSize at 4, Type at 8, GroupCount at 30,
                // GroupMasks (GROUP_AFFINITY: Mask at 0, Group at 8, 16 bytes each) from 32.
                if (relationship == RelationCache && *(entry + 8) == 3 && *(int*)(entry + 16) != CacheTypeInstruction)
                {
                    uint cacheSize = *(uint*)(entry + 12);
                    int groupCount = Math.Max(1, (int)*(ushort*)(entry + 38));
                    for (int index = 0; index < groupCount && 40 + (index + 1) * 16 <= size; index++)
                    {
                        byte* groupAffinity = entry + 40 + index * 16;
                        l3Caches.Add(new ProcessorCacheGroup(*(ushort*)(groupAffinity + 8), *(ulong*)groupAffinity, cacheSize));
                    }
                }
                // PROCESSOR_RELATIONSHIP: Flags at 0, EfficiencyClass at 1.
                else if (relationship == RelationProcessorCore)
                {
                    efficiencyClasses[*(entry + 9)] = true;
                }

                offset += size;
            }

            for (int index = 0; index < efficiencyClasses.Length; index++)
                if (efficiencyClasses[index])
                    efficiencyClassCount++;
        }

        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static unsafe partial bool GetLogicalProcessorInformationEx(
        int relationshipType,
        byte* buffer,
        ref uint returnedLength);
}
