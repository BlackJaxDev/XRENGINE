namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies the furthest Vulkan startup boundary entered by a frame loop so
/// shutdown can safely unwind a partially initialized renderer.
/// </summary>
internal enum VulkanFrameLoopInitializationStage
{
    None,
    Instance,
    TargetInstanceResources,
    OutputServices,
    PhysicalDevice,
    LogicalDevice,
    MemoryAllocator,
    StreamingScheduler,
    CanonicalSampler,
    CommandPool,
    RootDescriptorLayout,
    TargetFinalOutput,
    DesktopSwapchain,
    SynchronizationObjects,
    FrameTiming,
    SynchronizationBackend,
    MappedFrameArena,
    FrameDataArenas,
    Initialized,
    CleanedUp,
}
