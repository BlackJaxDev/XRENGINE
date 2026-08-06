
namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures immutable scheduling inputs before the renderer enters cache
/// validation. Persistent schedule artifacts belong to <see cref="VulkanCommandRuntime"/>.
/// </summary>
internal sealed class VulkanCommandScheduler
{
    private ulong _scheduleGeneration;

    public VulkanCommandSchedulingContext<TVariant> Capture<TVariant>(
        uint imageIndex,
        bool preserveSwapchainForOverlay,
        in RenderGraph.VulkanFramePlanningSnapshot planningSnapshot)
        where TVariant : class
        => new(imageIndex, preserveSwapchainForOverlay, planningSnapshot);

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

}
