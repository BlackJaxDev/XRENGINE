using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns final cleanup of resource allocation records at logical-device teardown.</summary>
internal sealed partial class VulkanResourceRuntime
{
    internal void DestroyRemainingTrackedAllocations(VulkanBackendObjectContext context)
    {
        Buffers.DestroyRemainingTrackedAllocations(context);
        DestroyRemainingTrackedImageAllocations(context);
    }

    internal void DestroyRemainingTrackedImageAllocations(VulkanBackendObjectContext context)
    {
        IVulkanMemoryAllocator? allocator = Allocations.Buffers.MemoryAllocator;
        if (allocator is null)
            return;

        foreach ((ulong handle, VulkanMemoryAllocation allocation) in Allocations.Images.Allocations.ToArray())
        {
            if (!Allocations.Images.Allocations.TryRemove(handle, out _))
                continue;

            Image image = new() { Handle = handle };
            if (image.Handle != 0)
                DestroyImageImmediateTracked(context.Api, context.Device, image, "RendererShutdown.RemainingAllocation");
            if (!allocation.IsNull)
                allocator.Free(context.Api, context.Device, allocation);
        }
    }
}
