using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures immutable scheduling inputs before the renderer enters cache
/// validation. Cache storage remains behind the renderer facade during migration.
/// </summary>
internal sealed class VulkanCommandScheduler
{
    private CommandChainSchedule?[]? _scheduleCache;
    private ulong _scheduleGeneration;

    public VulkanCommandSchedulingContext<TVariant> Capture<TVariant>(
        uint imageIndex,
        bool preserveSwapchainForOverlay,
        VulkanRenderGraphPlan renderGraphPlan)
        where TVariant : class
        => new(imageIndex, preserveSwapchainForOverlay, renderGraphPlan);

    public bool RequiresFreshPrimary(bool hasStaticOperations, bool primaryReuseEnabled)
        => hasStaticOperations && !primaryReuseEnabled;

    public bool HasOperationSignatureChanged(
        bool hasOperations,
        ulong recordedSignature,
        ulong currentSignature)
        => hasOperations && recordedSignature != currentSignature;

    public bool HasPlannerGenerationChanged(
        bool usesCommandChains,
        ulong recordedRevision,
        ulong currentRevision)
        => !usesCommandChains && recordedRevision != currentRevision;

    public bool HasCameraGenerationChanged(
        bool usesCommandChains,
        ulong recordedGeneration,
        ulong currentGeneration)
        => !usesCommandChains && recordedGeneration != currentGeneration;

    public bool HasSwapchainLifecycleChanged(
        bool recordedEverPresented,
        bool currentlyEverPresented,
        bool requiresTrackedPresentSourceRefresh,
        bool recordedRefreshFromLastPresentSource)
        => recordedEverPresented != currentlyEverPresented ||
           (requiresTrackedPresentSourceRefresh && !recordedRefreshFromLastPresentSource);

    public int RecordingAttemptLimit => 2;

    public bool ShouldRetryRecording(
        int attempt,
        bool transientResourceRetirement,
        bool swapchainResourceRetirement)
        => attempt + 1 < RecordingAttemptLimit &&
           transientResourceRetirement &&
           !swapchainResourceRetirement;

    public ulong NextScheduleGeneration()
    {
        ulong generation = unchecked(++_scheduleGeneration);
        if (generation == 0)
            generation = unchecked(++_scheduleGeneration);
        return generation;
    }

    public CommandChainSchedule? GetReusableSchedule(int slot, int slotCount)
    {
        EnsureScheduleCache(slotCount);
        return (uint)slot < (uint)_scheduleCache!.Length
            ? _scheduleCache[slot]
            : null;
    }

    public void CacheSchedule(
        int slot,
        int slotCount,
        CommandChainSchedule schedule)
    {
        EnsureScheduleCache(slotCount);
        if ((uint)slot >= (uint)_scheduleCache!.Length)
            return;

        _scheduleCache[slot] = schedule;
    }

    public void InvalidateScheduleCache()
    {
        if (_scheduleCache is not null)
            Array.Clear(_scheduleCache);
    }

    public void ReleaseScheduleCache()
    {
        _scheduleCache = null;
    }

    public int ResolveParallelRecordingBucket(
        in VulkanMeshFrameDataRendererFamilyKey rendererFamily,
        int workerCount)
    {
        if (workerCount <= 1)
            return 0;

        int rendererIdentity = RuntimeHelpers.GetHashCode(rendererFamily.Renderer);
        return unchecked((int)((uint)rendererIdentity % (uint)workerCount));
    }

    private void EnsureScheduleCache(int slotCount)
    {
        int count = Math.Max(slotCount, 1);
        if (_scheduleCache is not null &&
            _scheduleCache.Length == count)
        {
            return;
        }

        _scheduleCache = new CommandChainSchedule?[count];
    }
}
