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

/// <summary>Provides host-coordinated memory maintenance to lower runtime systems.</summary>
public interface IRuntimeMaintenanceServices
{
    EngineMaintenanceGcResult RequestGarbageCollection(EngineMaintenanceGcRequest request);
}

public static class RuntimeMaintenanceServices
{
    private static IRuntimeMaintenanceServices _current = new DefaultRuntimeMaintenanceServices();

    public static IRuntimeMaintenanceServices Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    private sealed class DefaultRuntimeMaintenanceServices : IRuntimeMaintenanceServices
    {
        public EngineMaintenanceGcResult RequestGarbageCollection(EngineMaintenanceGcRequest request)
        {
            long heapBytes = GC.GetTotalMemory(forceFullCollection: false);
            return new EngineMaintenanceGcResult(
                false,
                $"Maintenance GC skipped for {request.Reason}: no host maintenance service is configured.",
                heapBytes,
                heapBytes,
                Math.Clamp(request.Generation, 0, GC.MaxGeneration));
        }
    }
}
