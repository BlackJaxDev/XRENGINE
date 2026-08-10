using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen planner/resource facts supplied by FrameLoop for one mesh materialization.</summary>
internal readonly record struct VulkanMeshMaterializationSnapshot(
    FrameOpContext? ActiveFrameOpContext,
    int DescriptorViewFamilyIdentity,
    bool AvoidSynchronousImageAllocationForOpenXr)
{
    internal static ImageLayout ResolveDescriptorImageLayout(
        IVkImageDescriptorSource source,
        in VkImageDescriptorSnapshot snapshot,
        DescriptorType descriptorType)
    {
        _ = source;
        if (descriptorType == DescriptorType.StorageImage ||
            (snapshot.Usage & ImageUsageFlags.StorageBit) != 0 &&
            (snapshot.Usage & ImageUsageFlags.SampledBit) != 0)
        {
            return ImageLayout.General;
        }

        if (snapshot.TrackedLayout is ImageLayout.ShaderReadOnlyOptimal or
            ImageLayout.DepthStencilReadOnlyOptimal or ImageLayout.DepthReadOnlyOptimal or
            ImageLayout.StencilReadOnlyOptimal or ImageLayout.ReadOnlyOptimal)
        {
            return snapshot.TrackedLayout;
        }

        bool depthOrStencil =
            (snapshot.Aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0 ||
            snapshot.Format is Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint;
        return depthOrStencil ? ImageLayout.DepthStencilReadOnlyOptimal : ImageLayout.ShaderReadOnlyOptimal;
    }
}
