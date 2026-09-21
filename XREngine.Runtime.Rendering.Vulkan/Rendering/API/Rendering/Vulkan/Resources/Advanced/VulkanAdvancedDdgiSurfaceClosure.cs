using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen native surface exports consumed by DDGI screen sampling. Disabled
/// profiles reuse the compatible HDR descriptor without allocating attachments;
/// the native shader must not write these aliases unless DDGI is enabled.
/// </summary>
internal readonly record struct VulkanAdvancedDdgiSurfaceClosure(
    bool Enabled,
    VulkanPhysicalImageGroup Emission,
    VulkanPhysicalImageGroup Albedo,
    VulkanPhysicalImageGroup Normal,
    VulkanPhysicalImageGroup Rmse,
    VulkanAdvancedNativeImageClosure EmissionResource,
    VulkanAdvancedNativeImageClosure AlbedoResource,
    VulkanAdvancedNativeImageClosure NormalResource,
    VulkanAdvancedNativeImageClosure RmseResource)
{
    internal bool IsValid
        => Emission is { IsAllocated: true } && Albedo is { IsAllocated: true } &&
           Normal is { IsAllocated: true } && Rmse is { IsAllocated: true } &&
           EmissionResource.IsValid && AlbedoResource.IsValid &&
           NormalResource.IsValid && RmseResource.IsValid;

    internal DescriptorImageInfo EmissionDescriptor => StorageDescriptor(EmissionResource);
    internal DescriptorImageInfo AlbedoDescriptor => StorageDescriptor(AlbedoResource);
    internal DescriptorImageInfo NormalDescriptor => StorageDescriptor(NormalResource);
    internal DescriptorImageInfo RmseDescriptor => StorageDescriptor(RmseResource);

    private static DescriptorImageInfo StorageDescriptor(in VulkanAdvancedNativeImageClosure resource)
        => new() { ImageView = resource.View, ImageLayout = ImageLayout.General };
}
