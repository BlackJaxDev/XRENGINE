namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Per-thread Vulkan OpenXR execution identity. A backend owns the thread-local
/// slot; frame code passes the immutable frame context into each scope.
/// </summary>
internal sealed class VulkanOpenXrThreadExecutionState
{
    internal VulkanOpenXrFrameContext FrameContext;
    internal int ExternalSwapchainDepth;
    internal int SynchronousUploadBlockDepth;
    internal VulkanOpenXrViewResourcePlannerContextKey ResourcePlannerKey;
    internal int ResourcePlannerDepth;

    internal void Reset()
    {
        FrameContext = default;
        ExternalSwapchainDepth = 0;
        SynchronousUploadBlockDepth = 0;
        ResourcePlannerKey = default;
        ResourcePlannerDepth = 0;
    }
}
