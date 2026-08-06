using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns persistent queue synchronization, image-state tracking, and submission
/// marker storage used by the command authority.
/// </summary>
internal sealed class VulkanCommandSynchronizationState
{
    private const int QueueOperationHistoryCapacity = 64;

    internal Semaphore[]? acquireBridgeSemaphores;
    internal Semaphore _graphicsTimelineSemaphore;
    internal Semaphore _presentTimelineSemaphore;
    internal Semaphore _transferTimelineSemaphore;
    internal ulong[]? _frameSlotTimelineValues;
    internal ulong _acquireTimelineValue;
    internal ulong _graphicsTimelineValue;
    internal readonly VulkanSynchronizationThreadWorkspace _synchronizationThreadWorkspace = new();
    internal EVulkanSynchronizationBackend _activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
    internal readonly object _vulkanImageLayoutLock = new();
    internal readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageSubresourceState> _trackedImageSubresourceStates = new();
    internal readonly Dictionary<ulong, (ulong ResourceGeneration, EVulkanExternalImageOwnership Ownership)> _externalImageOwnershipByHandle = new();
    internal readonly Dictionary<ulong, VulkanRecordedImageLayoutState> _recordedImageLayoutsByCommandBuffer = new();
    internal readonly VulkanQueueOperationRecord[] _vulkanQueueOperationHistory =
        new VulkanQueueOperationRecord[QueueOperationHistoryCapacity];
    internal long _vulkanQueueOperationSerial;
    internal readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> _submissionImageStateScratch = new(64);
    internal readonly List<VulkanQueueSemaphoreRequirement> _submissionQueueSemaphoreRequirements = new(8);
    internal readonly object _submissionMarkerLock = new();
    internal readonly Dictionary<nint, List<VulkanRenderer.VulkanTimelineGpuFence>> _submissionMarkersByCommandBuffer = [];
    internal readonly Stack<VulkanRenderer.VulkanTimelineGpuFence> _timelineGpuFencePool = [];
}
