using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Coordinates native resources whose creation or use must be published to
/// both the command and resource lifetime authorities.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    internal unsafe Result CreateImageWithLifetime(
        ref ImageCreateInfo createInfo,
        out Image image,
        string owner)
    {
        ThrowIfVulkanDeviceOperationNotAdmitted("vkCreateImage." + owner);
        ThrowIfPersistentResourceAllocationDuringCommandRecording(owner);

        image = default;
        fixed (Image* imagePointer = &image)
        {
            Result result = ResourceRuntime.CreateImageTracked(
                Api,
                DeviceContext.Device,
                ref createInfo,
                imagePointer,
                owner);
            if (result == Result.Success && image.Handle != 0)
                RegisterTrackedImageInitialLayouts(image, in createInfo);
            return result;
        }
    }

    internal void DestroyImageWithLifetime(Image image, string owner)
    {
        if (image.Handle == 0)
            return;

        PublishTrackingDependenciesBeforeResourceRetirement(
            new VulkanResourceLifetimeKey(ObjectType.Image, image.Handle));
        ResourceRuntime.DestroyImageImmediateTracked(
            Api,
            DeviceContext.Device,
            image,
            owner);
    }

    internal unsafe void BlitImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ref ImageBlit region,
        Filter filter)
    {
        fixed (ImageBlit* regionPointer = &region)
        {
            BlitImageTracked(
                commandBuffer,
                source,
                sourceLayout,
                destination,
                destinationLayout,
                regionCount,
                regionPointer,
                filter);
        }
    }

    private void ThrowIfPersistentResourceAllocationDuringCommandRecording(string operation)
    {
        if (!ThreadWorkspace.TryGetCurrent(out VulkanCommandThreadContext context) ||
            !ReferenceEquals(context.FrameOpResourcePlannerSwitchingStateOwner, this) ||
            context.FrameOpResourcePlannerSwitchingState?.RecordingScopeActive != true)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Persistent Vulkan resource allocation '{operation}' is forbidden while command recording is active. " +
            "Allocate persistent resources during planning or upload preparation.");
    }
}
