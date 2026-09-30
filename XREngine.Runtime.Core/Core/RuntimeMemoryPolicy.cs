using System.Runtime;

namespace XREngine;

/// <summary>
/// Owns process memory policy and explicit maintenance collections for runtime hosts.
/// Diagnostics and hot-path state are supplied by composition rather than referenced upward.
/// </summary>
public static class RuntimeMemoryPolicy
{
    private static int _configured;
    private static int _benchmarkNoGcRegionActive;

    public static EngineMemoryPolicySnapshot Current => EngineMemoryPolicy.Current;

    public static EngineMemoryPolicySnapshot Configure(
        EngineMemoryProfile profile,
        Action<string>? log = null)
    {
        EngineMemoryPolicySnapshot snapshot = EngineMemoryPolicy.Apply(profile, log);
        Interlocked.Exchange(ref _configured, 1);
        return snapshot;
    }

    public static EngineMemoryPolicySnapshot EnsureConfigured(
        EngineMemoryProfile profile,
        Action<string>? log = null)
        => Volatile.Read(ref _configured) != 0
            ? EngineMemoryPolicy.Current
            : Configure(profile, log);

    public static bool TryStartBenchmarkNoGcRegion(
        long? byteBudget = null,
        Action<string>? log = null,
        Action<string>? warning = null)
    {
        EngineMemoryPolicySnapshot policy = EngineMemoryPolicy.Current;
        long bytes = byteBudget.GetValueOrDefault(policy.BenchmarkNoGcRegionBytes);
        if (!policy.BenchmarkNoGcRegionAllowed || bytes <= 0L)
        {
            log?.Invoke(
                "[MemoryPolicy] Benchmark no-GC region not enabled. Set XRE_MEMORY_PROFILE=Benchmark, " +
                "XRE_BENCHMARK_NOGC_REGION=1, and XRE_BENCHMARK_NOGC_BYTES.");
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
                warning?.Invoke($"[MemoryPolicy] Failed to start benchmark no-GC region for {bytes} bytes.");
            }
            else
                log?.Invoke($"[MemoryPolicy] Started benchmark no-GC region for {bytes} bytes.");

            return started;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _benchmarkNoGcRegionActive, 0);
            warning?.Invoke($"[MemoryPolicy] Failed to start benchmark no-GC region: {ex.Message}");
            return false;
        }
    }

    public static void EndBenchmarkNoGcRegion(
        Action<string>? log = null,
        Action<string>? warning = null)
    {
        if (Interlocked.Exchange(ref _benchmarkNoGcRegionActive, 0) == 0)
            return;

        try
        {
            GC.EndNoGCRegion();
            log?.Invoke("[MemoryPolicy] Ended benchmark no-GC region.");
        }
        catch (Exception ex)
        {
            warning?.Invoke($"[MemoryPolicy] Failed to end benchmark no-GC region cleanly: {ex.Message}");
        }
    }

    public static EngineMaintenanceGcResult RequestMaintenanceGarbageCollection(
        EngineMaintenanceGcRequest request,
        bool criticalWorkActive,
        Action<string>? log = null,
        Action<string>? warning = null)
    {
        EngineMemoryPolicySnapshot policy = EngineMemoryPolicy.Current;
        int generation = Math.Clamp(request.Generation, 0, GC.MaxGeneration);
        long heapBefore = GC.GetTotalMemory(forceFullCollection: false);

        if (!policy.MaintenanceGcAllowed)
        {
            string disabled =
                $"Maintenance GC skipped for {request.Reason}: disabled by {XREngineEnvironmentVariables.DisableMaintenanceGc}.";
            log?.Invoke("[MemoryPolicy] " + disabled);
            return new EngineMaintenanceGcResult(false, disabled, heapBefore, heapBefore, generation);
        }

        if (criticalWorkActive)
        {
            string hotPath = $"Maintenance GC rejected for {request.Reason}: critical frame work is active.";
            warning?.Invoke("[MemoryPolicy] " + hotPath);
            return new EngineMaintenanceGcResult(false, hotPath, heapBefore, heapBefore, generation);
        }

        string detail = string.IsNullOrWhiteSpace(request.Detail) ? string.Empty : " " + request.Detail.Trim();
        log?.Invoke(
            $"[MemoryPolicy] Maintenance GC start reason={request.Reason}{detail}; generation={generation}; " +
            $"compactLOH={request.CompactLargeObjectHeap}; waitFinalizers={request.WaitForPendingFinalizers}; " +
            $"heapBefore={heapBefore}.");

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
        string message =
            $"Maintenance GC completed for {request.Reason}; heapAfter={heapAfter}; " +
            $"reclaimed={Math.Max(0L, heapBefore - heapAfter)}.";
        log?.Invoke("[MemoryPolicy] " + message);
        return new EngineMaintenanceGcResult(true, message, heapBefore, heapAfter, generation);
    }
}
