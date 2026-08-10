using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanDescriptorManager
{
    /// <summary>
    /// Republishes a streamed texture before its old image generation enters the
    /// retirement queues. The bindless table state remains singular in
    /// <see cref="BindlessMaterialTextures"/>; this descriptor authority owns
    /// the native update rather than routing through the renderer facade.
    /// </summary>
    internal unsafe void RefreshGlobalMaterialTextureDescriptorForPublishedTexture(XRTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        VulkanBackendObjectContext context = _backendContext ?? throw new InvalidOperationException(
            "The descriptor manager has not been bound to a Vulkan backend context.");
        VulkanBindlessMaterialTextureTableState state = BindlessMaterialTextures;
        lock (state.Sync)
        {
            if (state.Set.Handle == 0 || !state.SlotsByTexture.TryGetValue(texture, out uint slotIndex))
                return;

            if (!TryResolvePublishedTextureDescriptor(context, texture, out DescriptorImageInfo imageInfo))
                imageInfo = state.Slots[0].ImageInfo;

            ref MaterialTextureDescriptorSlot slot = ref state.Slots[slotIndex];
            if (slot.ImageInfo.ImageView.Handle == imageInfo.ImageView.Handle &&
                slot.ImageInfo.Sampler.Handle == imageInfo.Sampler.Handle &&
                slot.ImageInfo.ImageLayout == imageInfo.ImageLayout)
            {
                return;
            }

            slot.ImageInfo = imageInfo;
            slot.Generation++;
            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = state.Set,
                DstBinding = VulkanBindlessMaterialDescriptors.TextureArrayBinding,
                DstArrayElement = slotIndex,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &imageInfo,
            };
            context.DescriptorLifetime.UpdateDescriptorSets(1, &write);
            state.WritesLastFlush = 1;
            state.WritesTotal++;
            slot.Dirty = false;
        }
    }

    private bool TryResolvePublishedTextureDescriptor(
        VulkanBackendObjectContext context,
        XRTexture texture,
        out DescriptorImageInfo imageInfo)
    {
        imageInfo = default;
        if (context.GetOrCreateAPIRenderObject(texture, generateNow: context.AllowSynchronousResourceUploads) is not IVkImageDescriptorSource source ||
            !source.TryEnsureDescriptorReadyForUse("streamed texture publication", context.AllowSynchronousResourceUploads))
        {
            return false;
        }

        ImageView view = source.DescriptorViewType == ImageViewType.Type2D
            ? source.DescriptorView
            : source.GetDescriptorView(ImageViewType.Type2D);
        if (view.Handle == 0 || !context.Images.IsAvailableForDescriptor(view))
            return false;

        Sampler sampler = source.DescriptorSampler;
        if (sampler.Handle == 0 || !IsLiveSampler(sampler))
            sampler = context.FallbackTexture.GetSampler();
        if (sampler.Handle == 0 || !IsLiveSampler(sampler))
            return false;

        ImageLayout layout = source.TrackedImageLayout;
        if (layout == ImageLayout.Undefined)
            layout = (source.DescriptorAspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0
                ? ImageLayout.DepthStencilReadOnlyOptimal
                : ImageLayout.ShaderReadOnlyOptimal;
        imageInfo = new DescriptorImageInfo
        {
            ImageLayout = (source.DescriptorUsage & ImageUsageFlags.StorageBit) != 0
                ? ImageLayout.General
                : layout,
            ImageView = view,
            Sampler = sampler,
        };
        return true;
    }
}
