using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering;

/// <summary>
/// Low-overhead counters that separate CPU mesh construction from renderer-owner publication.
/// </summary>
public static class XRMeshCpuPreparationTelemetry
{
    [ThreadStatic]
    private static int _preparationDepth;

    private static long _completedMeshCount;
    private static long _failedMeshCount;
    private static long _preparedVertexCount;
    private static long _preparedBufferCount;
    private static long _preparedBufferBytes;
    private static long _peakMeshVertexCount;
    private static long _peakMeshBufferBytes;
    private static long _allocationCount;
    private static long _allocationBytes;
    private static long _allocationTicks;
    private static long _zeroFillCount;
    private static long _zeroFillBytes;
    private static long _zeroFillTicks;
    private static long _bufferCallbackCount;
    private static long _bufferCallbackTicks;
    private static long _vertexPopulationCount;
    private static long _vertexPopulationVertexCount;
    private static long _vertexPopulationTicks;
    private static long _cachePublicationBatchCount;
    private static long _cachePublicationObjectCount;
    private static long _cachePublicationTicks;
    private static long _wrapperLockWaitCount;
    private static long _wrapperLockWaitTicks;
    private static long _wrapperLockWaitPeakTicks;
    private static long _openGlWrapperCreationCount;
    private static long _openGlWrapperCreationFailureCount;
    private static long _openGlWrapperCreationTicks;
    private static long _vulkanWrapperCreationCount;
    private static long _vulkanWrapperCreationFailureCount;
    private static long _vulkanWrapperCreationTicks;
    private static long _wrapperCreationDuringCpuPreparationCount;
    private static long _wrapperCreationOffOwnerThreadCount;
    private static long _activePreparationCount;
    private static int _lastPreparationThreadId;
    private static int _lastCachePublicationThreadId;
    private static int _lastWrapperCreationThreadId;

    public static bool IsPreparationActive
        => _preparationDepth > 0 || GenericRenderObject.CurrentDeferredPublicationScope is not null;

    internal static void EnterPreparation()
    {
        _preparationDepth++;
        Interlocked.Increment(ref _activePreparationCount);
        Volatile.Write(ref _lastPreparationThreadId, Environment.CurrentManagedThreadId);
    }

    internal static void ExitPreparation(bool success, int vertexCount, int bufferCount, long bufferBytes)
    {
        if (_preparationDepth <= 0)
            throw new InvalidOperationException("Mesh CPU preparation scopes must exit in stack order.");

        _preparationDepth--;
        Interlocked.Decrement(ref _activePreparationCount);
        if (!success)
        {
            Interlocked.Increment(ref _failedMeshCount);
            return;
        }

        Interlocked.Increment(ref _completedMeshCount);
        Interlocked.Add(ref _preparedVertexCount, vertexCount);
        Interlocked.Add(ref _preparedBufferCount, bufferCount);
        Interlocked.Add(ref _preparedBufferBytes, bufferBytes);
        UpdateMaximum(ref _peakMeshVertexCount, vertexCount);
        UpdateMaximum(ref _peakMeshBufferBytes, bufferBytes);
    }

    internal static void RecordAllocation(long byteCount, long elapsedTicks)
    {
        Interlocked.Increment(ref _allocationCount);
        Interlocked.Add(ref _allocationBytes, byteCount);
        Interlocked.Add(ref _allocationTicks, elapsedTicks);
    }

    internal static void RecordZeroFill(long byteCount, long elapsedTicks)
    {
        Interlocked.Increment(ref _zeroFillCount);
        Interlocked.Add(ref _zeroFillBytes, byteCount);
        Interlocked.Add(ref _zeroFillTicks, elapsedTicks);
    }

    internal static void RecordBufferCallback(long elapsedTicks)
    {
        Interlocked.Increment(ref _bufferCallbackCount);
        Interlocked.Add(ref _bufferCallbackTicks, elapsedTicks);
    }

    internal static void RecordVertexPopulation(int vertexCount, long elapsedTicks)
    {
        Interlocked.Increment(ref _vertexPopulationCount);
        Interlocked.Add(ref _vertexPopulationVertexCount, vertexCount);
        Interlocked.Add(ref _vertexPopulationTicks, elapsedTicks);
    }

    internal static void RecordCachePublication(int objectCount, long elapsedTicks)
    {
        Interlocked.Increment(ref _cachePublicationBatchCount);
        Interlocked.Add(ref _cachePublicationObjectCount, objectCount);
        Interlocked.Add(ref _cachePublicationTicks, elapsedTicks);
        Volatile.Write(ref _lastCachePublicationThreadId, Environment.CurrentManagedThreadId);
    }

    public static void RecordWrapperLockWait(long elapsedTicks)
    {
        Interlocked.Increment(ref _wrapperLockWaitCount);
        Interlocked.Add(ref _wrapperLockWaitTicks, elapsedTicks);
        UpdateMaximum(ref _wrapperLockWaitPeakTicks, elapsedTicks);
    }

    public static void RecordWrapperCreation(EMeshWrapperBackend backend, long elapsedTicks, bool success)
    {
        if (IsPreparationActive)
            Interlocked.Increment(ref _wrapperCreationDuringCpuPreparationCount);
        if (RuntimeEngine.RenderThreadId != 0 && !RuntimeEngine.IsRenderThread)
            Interlocked.Increment(ref _wrapperCreationOffOwnerThreadCount);

        Volatile.Write(ref _lastWrapperCreationThreadId, Environment.CurrentManagedThreadId);
        if (backend == EMeshWrapperBackend.Vulkan)
        {
            Interlocked.Increment(ref _vulkanWrapperCreationCount);
            Interlocked.Add(ref _vulkanWrapperCreationTicks, elapsedTicks);
            if (!success)
                Interlocked.Increment(ref _vulkanWrapperCreationFailureCount);
            return;
        }

        Interlocked.Increment(ref _openGlWrapperCreationCount);
        Interlocked.Add(ref _openGlWrapperCreationTicks, elapsedTicks);
        if (!success)
            Interlocked.Increment(ref _openGlWrapperCreationFailureCount);
    }

    public static XRMeshCpuPreparationTelemetrySnapshot CaptureSnapshot()
        => new(
            Interlocked.Read(ref _completedMeshCount),
            Interlocked.Read(ref _failedMeshCount),
            Interlocked.Read(ref _preparedVertexCount),
            Interlocked.Read(ref _preparedBufferCount),
            Interlocked.Read(ref _preparedBufferBytes),
            Interlocked.Read(ref _peakMeshVertexCount),
            Interlocked.Read(ref _peakMeshBufferBytes),
            Interlocked.Read(ref _allocationCount),
            Interlocked.Read(ref _allocationBytes),
            Interlocked.Read(ref _allocationTicks),
            Interlocked.Read(ref _zeroFillCount),
            Interlocked.Read(ref _zeroFillBytes),
            Interlocked.Read(ref _zeroFillTicks),
            Interlocked.Read(ref _bufferCallbackCount),
            Interlocked.Read(ref _bufferCallbackTicks),
            Interlocked.Read(ref _vertexPopulationCount),
            Interlocked.Read(ref _vertexPopulationVertexCount),
            Interlocked.Read(ref _vertexPopulationTicks),
            Interlocked.Read(ref _cachePublicationBatchCount),
            Interlocked.Read(ref _cachePublicationObjectCount),
            Interlocked.Read(ref _cachePublicationTicks),
            Interlocked.Read(ref _wrapperLockWaitCount),
            Interlocked.Read(ref _wrapperLockWaitTicks),
            Interlocked.Read(ref _wrapperLockWaitPeakTicks),
            Interlocked.Read(ref _openGlWrapperCreationCount),
            Interlocked.Read(ref _openGlWrapperCreationFailureCount),
            Interlocked.Read(ref _openGlWrapperCreationTicks),
            Interlocked.Read(ref _vulkanWrapperCreationCount),
            Interlocked.Read(ref _vulkanWrapperCreationFailureCount),
            Interlocked.Read(ref _vulkanWrapperCreationTicks),
            Interlocked.Read(ref _wrapperCreationDuringCpuPreparationCount),
            Interlocked.Read(ref _wrapperCreationOffOwnerThreadCount),
            Interlocked.Read(ref _activePreparationCount),
            Volatile.Read(ref _lastPreparationThreadId),
            Volatile.Read(ref _lastCachePublicationThreadId),
            Volatile.Read(ref _lastWrapperCreationThreadId));

    private static void UpdateMaximum(ref long target, long value)
    {
        long current = Volatile.Read(ref target);
        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
}
