using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanAdvancedVisibilityResourceRuntime
{
    /// <summary>
    /// Captures DDGI surface outputs from the same physical generation as native
    /// shading. Disabled profiles reuse HDR only for inactive descriptors.
    /// </summary>
    private bool TryCaptureDdgiSurfaceClosure(
        VulkanBackendObjectContext context,
        VulkanResourceAllocator allocator,
        VulkanAdvancedNativeComputeClosureStorage storage,
        bool enabled,
        uint viewIndex,
        VulkanPhysicalImageGroup hdr,
        in VulkanAdvancedNativeImageClosure hdrResource,
        out VulkanAdvancedDdgiSurfaceClosure closure,
        out string reason)
    {
        closure = default;
        if (!enabled)
        {
            closure = new(false, hdr, hdr, hdr, hdr,
                hdrResource, hdrResource, hdrResource, hdrResource);
            reason = "Ready";
            return true;
        }

        if (!TryCaptureDdgiSurfaceOutput(DefaultRenderPipeline.EmissionColorTextureName,
                context, allocator, storage, hdr, viewIndex,
                out VulkanPhysicalImageGroup? emission, out VulkanAdvancedNativeImageClosure emissionResource) ||
            !TryCaptureDdgiSurfaceOutput(DefaultRenderPipeline.AlbedoOpacityTextureName,
                context, allocator, storage, hdr, viewIndex,
                out VulkanPhysicalImageGroup? albedo, out VulkanAdvancedNativeImageClosure albedoResource) ||
            !TryCaptureDdgiSurfaceOutput(DefaultRenderPipeline.NormalTextureName,
                context, allocator, storage, hdr, viewIndex,
                out VulkanPhysicalImageGroup? normal, out VulkanAdvancedNativeImageClosure normalResource) ||
            !TryCaptureDdgiSurfaceOutput(DefaultRenderPipeline.RMSETextureName,
                context, allocator, storage, hdr, viewIndex,
                out VulkanPhysicalImageGroup? rmse, out VulkanAdvancedNativeImageClosure rmseResource))
        {
            reason = "The frozen DDGI surface exports require allocated RGBA16F sampled/storage images matching the native output extent and view layers.";
            return false;
        }

        closure = new(true, emission!, albedo!, normal!, rmse!,
            emissionResource, albedoResource, normalResource, rmseResource);
        reason = "Ready";
        return true;
    }

    private bool TryCaptureDdgiSurfaceOutput(
        string name,
        VulkanBackendObjectContext context,
        VulkanResourceAllocator allocator,
        VulkanAdvancedNativeComputeClosureStorage storage,
        VulkanPhysicalImageGroup reference,
        uint viewIndex,
        out VulkanPhysicalImageGroup? group,
        out VulkanAdvancedNativeImageClosure resource)
    {
        resource = default;
        return allocator.TryGetPhysicalGroupForResource(name, out group) &&
            group is { IsAllocated: true } &&
            HasImage(group, Format.R16G16B16A16Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit) &&
            HasSameExtent(group, reference) &&
            viewIndex < Math.Max(1u, group.Template.Layers) &&
            TryAcquireNativeComputeView(context, storage, group, ImageAspectFlags.ColorBit, viewIndex, out ImageView view) &&
            TryCaptureNativeComputeImageClosure(group, view, out resource);
    }
}
