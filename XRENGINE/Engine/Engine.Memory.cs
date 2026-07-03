using System.Runtime;

namespace XREngine;

public enum EngineMaintenanceGcReason
{
    SceneOrWorldUnload,
    BulkAssetImportCompleted,
    EditorExitedPlayMode,
    BenchmarkSetup,
    BenchmarkTeardown,
    EditorIdleMemoryReclaim,
    DynamicAssemblyUnload,
    EngineShutdown,
}

public readonly record struct EngineMaintenanceGcRequest(
    EngineMaintenanceGcReason Reason,
    string Detail,
    int Generation = 2,
    bool CompactLargeObjectHeap = false,
    bool WaitForPendingFinalizers = true);

public readonly record struct EngineMaintenanceGcResult(
    bool Ran,
    string Message,
    long HeapBeforeBytes,
    long HeapAfterBytes,
    int Generation);

public static partial class Engine
{
    private static int _memoryPolicyConfigured;
    private static int _benchmarkNoGcRegionActive;

    public static EngineMemoryPolicySnapshot MemoryPolicy => EngineMemoryPolicy.Current;

    public static EngineMemoryPolicySnapshot ConfigureMemoryPolicy(EngineMemoryProfile profile)
    {
        EngineMemoryPolicySnapshot snapshot = EngineMemoryPolicy.Apply(profile, static message => Debug.Out(message));
        Interlocked.Exchange(ref _memoryPolicyConfigured, 1);
        return snapshot;
    }

    internal static EngineMemoryPolicySnapshot EnsureMemoryPolicyConfigured(
        GameStartupSettings startupSettings,
        EngineMemoryProfile fallbackProfile = EngineMemoryProfile.DesktopRuntime)
    {
        if (Volatile.Read(ref _memoryPolicyConfigured) != 0)
            return EngineMemoryPolicy.Current;

        EngineMemoryProfile profile = startupSettings is IVRGameStartupSettings
            ? EngineMemoryProfile.VRLowLatency
            : fallbackProfile;

        return ConfigureMemoryPolicy(profile);
    }

    public static bool TryStartBenchmarkNoGcRegion(long? byteBudget = null)
    {
        EngineMemoryPolicySnapshot policy = EngineMemoryPolicy.Current;
        long bytes = byteBudget.GetValueOrDefault(policy.BenchmarkNoGcRegionBytes);
        if (!policy.BenchmarkNoGcRegionAllowed || bytes <= 0L)
        {
            Debug.Out("[MemoryPolicy] Benchmark no-GC region not enabled. Set XRE_MEMORY_PROFILE=Benchmark, XRE_BENCHMARK_NOGC_REGION=1, and XRE_BENCHMARK_NOGC_BYTES.");
            return false;
        }

        if (Interlocked.CompareExchange(ref _benchmarkNoGcRegionActive, 1, 0) != 0)
            return true;

        try
        {
            bool started = GC.TryStartNoGCRegion(bytes, disallowFullBlockingGC: true);
            if (!started)
            {
                Interlocked.Exchange(ref _benchmarkNoGcRegionActive, 0);
                Debug.LogWarning($"[MemoryPolicy] Failed to start benchmark no-GC region for {bytes} bytes.");
            }
            else
            {
                Debug.Out($"[MemoryPolicy] Started benchmark no-GC region for {bytes} bytes.");
            }

            return started;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _benchmarkNoGcRegionActive, 0);
            Debug.LogWarning($"[MemoryPolicy] Failed to start benchmark no-GC region: {ex.Message}");
            return false;
        }
    }

    public static void EndBenchmarkNoGcRegion()
    {
        if (Interlocked.Exchange(ref _benchmarkNoGcRegionActive, 0) == 0)
            return;

        try
        {
            GC.EndNoGCRegion();
            Debug.Out("[MemoryPolicy] Ended benchmark no-GC region.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MemoryPolicy] Failed to end benchmark no-GC region cleanly: {ex.Message}");
        }
    }

    public static EngineMaintenanceGcResult RequestMaintenanceGarbageCollection(EngineMaintenanceGcRequest request)
    {
        EngineMemoryPolicySnapshot policy = EngineMemoryPolicy.Current;
        int generation = Math.Clamp(request.Generation, 0, GC.MaxGeneration);
        long heapBefore = GC.GetTotalMemory(forceFullCollection: false);

        if (!policy.MaintenanceGcAllowed)
        {
            string disabled = $"Maintenance GC skipped for {request.Reason}: disabled by {XREngineEnvironmentVariables.DisableMaintenanceGc}.";
            Debug.Out("[MemoryPolicy] " + disabled);
            return new EngineMaintenanceGcResult(false, disabled, heapBefore, heapBefore, generation);
        }

        if (IsDispatchingRenderFrame)
        {
            string hotPath = $"Maintenance GC rejected for {request.Reason}: render frame dispatch is active.";
            Debug.LogWarning("[MemoryPolicy] " + hotPath);
            return new EngineMaintenanceGcResult(false, hotPath, heapBefore, heapBefore, generation);
        }

        string detail = string.IsNullOrWhiteSpace(request.Detail) ? string.Empty : " " + request.Detail.Trim();
        using var scope = Profiler.Start($"Engine.MaintenanceGC.{request.Reason}");
        Debug.Out(
            "[MemoryPolicy] Maintenance GC start reason={0}{1}; generation={2}; compactLOH={3}; waitFinalizers={4}; heapBefore={5}.",
            request.Reason,
            detail,
            generation,
            request.CompactLargeObjectHeap,
            request.WaitForPendingFinalizers,
            heapBefore);

        if (request.CompactLargeObjectHeap)
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

        GC.Collect(
            generation,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: request.CompactLargeObjectHeap);

        if (request.WaitForPendingFinalizers)
        {
            GC.WaitForPendingFinalizers();
            GC.Collect(
                generation,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: request.CompactLargeObjectHeap);
        }

        long heapAfter = GC.GetTotalMemory(forceFullCollection: false);
        string message = $"Maintenance GC completed for {request.Reason}; heapAfter={heapAfter}; reclaimed={Math.Max(0L, heapBefore - heapAfter)}.";
        Debug.Out("[MemoryPolicy] " + message);
        return new EngineMaintenanceGcResult(true, message, heapBefore, heapAfter, generation);
    }
}
