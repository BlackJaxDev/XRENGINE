namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    private const ulong MappedFrameArenaInitialCapacity = 32 * 1024 * 1024;

    private static bool? DynamicUniformBufferEnabledOverride
        => XREnvironment.GetBooleanOverride(
            XREngineEnvironmentVariables.VulkanDynamicUniformBuffer);

    internal bool IsMappedFrameArenaEnabled
        => ResolveDynamicUniformBufferEnabled() &&
           (Descriptors.Heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap ||
            !Descriptors.Heap.StorageReady) &&
           MappedFrameArena?.IsActive == true;

    internal void InitializeMappedFrameArena(
        VulkanDeviceContext deviceContext,
        int desktopFrameSlotCount)
    {
        if (!ResolveDynamicUniformBufferEnabled())
        {
            if (DynamicUniformBufferEnabledOverride is false)
            {
                Debug.Vulkan(
                    "[Vulkan] Mapped frame arena disabled by {0}=0 for this process.",
                    XREngineEnvironmentVariables.VulkanDynamicUniformBuffer);
            }
            return;
        }

        int frameSlotCount = Math.Max(desktopFrameSlotCount, Descriptors.FrameSlotCount);
        if (frameSlotCount == 0)
            return;

        VulkanMappedFrameArenaBackend backend = new(
            deviceContext.Api,
            deviceContext.PhysicalDevice,
            deviceContext.Device,
            deviceContext,
            Allocations.Buffers,
            deviceContext.NonCoherentAtomSize);
        VulkanMappedFrameArena arena = new(
            backend,
            MappedFrameArenaInitialCapacity,
            checked((uint)Math.Max(deviceContext.MinUniformBufferOffsetAlignment, 1UL)));
        try
        {
            arena.Initialize(frameSlotCount);
            PublishMappedFrameArena(arena);
            Debug.Vulkan(
                "[Vulkan] Mapped frame arena initialized: {0} x {1} KB, dynamic-offset alignment={2}.",
                frameSlotCount,
                MappedFrameArenaInitialCapacity / 1024,
                arena.DynamicOffsetAlignment);
        }
        catch
        {
            arena.Destroy();
            throw;
        }
    }

    internal void EnsureMappedFrameArenaFrameSlotCapacity(int frameSlotCount)
    {
        if (!ResolveDynamicUniformBufferEnabled() || MappedFrameArena is not { } arena)
            return;

        arena.EnsureFrameSlotCount(frameSlotCount);
    }

    internal void DestroyMappedFrameArena()
    {
        VulkanMappedFrameArena? arena = DetachMappedFrameArena();
        arena?.Destroy();
    }

    private static bool ResolveDynamicUniformBufferEnabled()
        => DynamicUniformBufferEnabledOverride ??
           RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DynamicUniformBufferEnabled;
}
