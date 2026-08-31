namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private bool _explicitTargetAllocationDiagnosticsEnabled;
    private VulkanExplicitTargetFrameAllocationCounters _lastExplicitTargetFrameAllocationCounters;

    internal bool ExplicitTargetAllocationDiagnosticsEnabled
    {
        get => _explicitTargetAllocationDiagnosticsEnabled;
        set => _explicitTargetAllocationDiagnosticsEnabled = value;
    }

    internal VulkanExplicitTargetFrameAllocationCounters LastExplicitTargetFrameAllocationCounters
        => _lastExplicitTargetFrameAllocationCounters;

    private void PublishExplicitTargetFrameAllocationCounters(
        long acquireFrameTarget,
        long beginFrameRecording,
        long beginTrackedCommandBuffer,
        long beginFrameResourceTracking,
        long beginBindStateInitialization,
        long beginTrackingInitialization,
        long beginNativeCommandBuffer,
        long recordCallback,
        long endFrameRecording,
        long queueSubmission,
        long completeFrameTarget)
        => _lastExplicitTargetFrameAllocationCounters = new(
            acquireFrameTarget,
            beginFrameRecording,
            beginTrackedCommandBuffer,
            beginFrameResourceTracking,
            beginBindStateInitialization,
            beginTrackingInitialization,
            beginNativeCommandBuffer,
            recordCallback,
            endFrameRecording,
            queueSubmission,
            completeFrameTarget);
}
