using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Command-owned helpers used while encoding prepared image and buffer barriers.
/// Every lookup is generation-local; no renderer facade, output authority, or
/// mutable render-graph planner participates in command recording.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    private static bool IsBloomDiagnosticName(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
           name.Contains("Bloom", StringComparison.OrdinalIgnoreCase);

    internal static bool IsColorAttachment(EFrameBufferAttachment attachment)
        => attachment is >= EFrameBufferAttachment.ColorAttachment0 and <= EFrameBufferAttachment.ColorAttachment31;

    private static bool IsColorLikeAttachmentRole(AttachmentRole role)
        => role is AttachmentRole.Color or AttachmentRole.Resolve;

    internal static bool IsDepthOrStencilAspect(ImageAspectFlags aspectMask)
        => (aspectMask & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0;

    internal static bool IsDepthOrStencilFormat(Format format)
        => format is Format.D16Unorm
            or Format.D32Sfloat
            or Format.D24UnormS8Uint
            or Format.D32SfloatS8Uint
            or Format.D16UnormS8Uint
            or Format.X8D24UnormPack32
            or Format.S8Uint;

    internal static bool IsCombinedDepthStencilFormat(Format format)
        => format is Format.D24UnormS8Uint
            or Format.D32SfloatS8Uint
            or Format.D16UnormS8Uint;

    internal static ImageAspectFlags NormalizeBarrierAspectMask(
        Format format,
        ImageAspectFlags aspectMask)
    {
        if (!IsDepthOrStencilFormat(format))
        {
            ImageAspectFlags color = aspectMask & ImageAspectFlags.ColorBit;
            return color != ImageAspectFlags.None ? color : ImageAspectFlags.ColorBit;
        }

        ImageAspectFlags supported = format switch
        {
            Format.S8Uint => ImageAspectFlags.StencilBit,
            Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint =>
                ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            _ => ImageAspectFlags.DepthBit,
        };
        ImageAspectFlags normalized = aspectMask & supported;
        if (normalized == ImageAspectFlags.None)
            return supported;

        return IsCombinedDepthStencilFormat(format)
            ? normalized | ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
            : normalized;
    }

    private bool TryResolveAttachmentImage(
        IFrameBufferAttachement attachment,
        int mipLevel,
        int layerIndex,
        ImageAspectFlags aspectMask,
        out BlitImageInfo info)
    {
        info = default;
        VkObjectBase? wrapper = attachment is GenericRenderObject renderObject
            ? ResourceRuntime.BackendObjects.Get(renderObject) as VkObjectBase
            : null;

        ImageLayout preferredLayout = (aspectMask & ImageAspectFlags.ColorBit) != 0
            ? ImageLayout.ColorAttachmentOptimal
            : ImageLayout.DepthStencilAttachmentOptimal;
        PipelineStageFlags stageMask = (aspectMask & ImageAspectFlags.ColorBit) != 0
            ? PipelineStageFlags.ColorAttachmentOutputBit
            : PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        AccessFlags accessMask = (aspectMask & ImageAspectFlags.ColorBit) != 0
            ? AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit
            : AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;

        if (wrapper is VkRenderBuffer renderBuffer)
        {
            renderBuffer.RefreshIfStale();
            if (renderBuffer.Image.Handle == 0 ||
                IsDepthOrStencilAspect(aspectMask) && (renderBuffer.Aspect & aspectMask) != aspectMask)
            {
                return false;
            }

            ImageLayout trackedLayout = TryGetTrackedImageLayout(
                renderBuffer.Image,
                new ImageSubresourceRange(aspectMask, 0, 1, 0, 1),
                out ImageLayout layout)
                ? layout
                : preferredLayout;
            info = new BlitImageInfo(
                renderBuffer.Image,
                renderBuffer.Format,
                aspectMask,
                0,
                1,
                0,
                renderBuffer.ResolveAttachmentExtent(),
                trackedLayout,
                stageMask,
                accessMask,
                renderBufferSource: renderBuffer);
            return info.IsValid;
        }

        if (wrapper is not IVkImageDescriptorSource source || source.DescriptorImage.Handle == 0)
            return false;

        Format format = source.DescriptorFormat;
        if (IsDepthOrStencilAspect(aspectMask) ? !IsDepthOrStencilFormat(format) : (aspectMask & ImageAspectFlags.ColorBit) == 0)
            return false;

        uint mipLevels = Math.Max(source.DescriptorMipLevels, 1u);
        uint resolvedMip = Math.Min((uint)Math.Max(mipLevel, 0), mipLevels - 1u);
        uint availableLayers = Math.Max(source.DescriptorArrayLayers, 1u);
        uint baseLayer = layerIndex < 0
            ? 0u
            : Math.Min((uint)layerIndex, availableLayers - 1u);
        uint layerCount = layerIndex < 0 ? availableLayers : 1u;
        Extent2D extent = source is IVkFrameBufferAttachmentSource attachmentSource &&
                          attachmentSource.TryGetAttachmentExtent((int)resolvedMip, layerIndex, out Extent2D resolvedExtent)
            ? resolvedExtent
            : new Extent2D(
                Math.Max(attachment.Width >> (int)resolvedMip, 1u),
                Math.Max(attachment.Height >> (int)resolvedMip, 1u));
        ImageSubresourceRange range = new(
            NormalizeBarrierAspectMask(format, aspectMask),
            resolvedMip,
            1,
            baseLayer,
            layerCount);
        ImageLayout tracked = TryGetTrackedImageLayout(source.DescriptorImage, range, out ImageLayout exactLayout)
            ? exactLayout
            : source.TrackedImageLayout;
        if (tracked == ImageLayout.Undefined && !source.UsesAllocatorImage)
            tracked = preferredLayout;

        info = new BlitImageInfo(
            source.DescriptorImage,
            format,
            aspectMask,
            baseLayer,
            layerCount,
            resolvedMip,
            extent,
            tracked,
            stageMask,
            accessMask,
            source);
        return info.IsValid;
    }

    internal bool TryGetTrackedImageLayout(
        Image image,
        in ImageSubresourceRange range,
        out ImageLayout layout)
    {
        layout = ImageLayout.Undefined;
        if (image.Handle == 0)
            return false;

        ImageSubresourceRange requestedRange = range;
        VulkanImageAccessState? combined = null;
        lock (Synchronization._vulkanImageLayoutLock)
        {
            uint levelCount = Math.Max(requestedRange.LevelCount, 1u);
            uint layerCount = Math.Max(requestedRange.LayerCount, 1u);
            for (uint levelOffset = 0; levelOffset < levelCount; levelOffset++)
            for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
            {
                uint mip = requestedRange.BaseMipLevel + levelOffset;
                uint layer = requestedRange.BaseArrayLayer + layerOffset;
                if (!MergeAspect(ImageAspectFlags.ColorBit) ||
                    !MergeAspect(ImageAspectFlags.DepthBit) ||
                    !MergeAspect(ImageAspectFlags.StencilBit))
                {
                    return false;
                }

                bool MergeAspect(ImageAspectFlags aspect)
                {
                    if ((requestedRange.AspectMask & aspect) == 0)
                        return true;

                    VulkanTrackedImageSubresource key = new(image.Handle, mip, layer, aspect);
                    if (!Synchronization._trackedImageSubresourceStates.TryGetValue(
                            key,
                            out VulkanImageSubresourceState? submitted))
                    {
                        return false;
                    }

                    VulkanImageAccessState candidate = submitted.Submitted;
                    if (!combined.HasValue)
                    {
                        combined = candidate;
                        return true;
                    }

                    return combined.Value.Layout == candidate.Layout &&
                           combined.Value.QueueFamilyIndex == candidate.QueueFamilyIndex &&
                           combined.Value.ResourceGeneration == candidate.ResourceGeneration;
                }
            }
        }

        if (!combined.HasValue)
            return false;
        layout = combined.Value.Layout;
        return layout != ImageLayout.Undefined;
    }

    internal bool TryGetRecordedImageLayout(
        CommandBuffer commandBuffer,
        Image image,
        in ImageSubresourceRange range,
        out ImageLayout layout)
    {
        if (TryGetRecordedImageAccessState(commandBuffer, image, in range, out VulkanImageAccessState state))
        {
            layout = state.Layout;
            return layout != ImageLayout.Undefined;
        }

        layout = ImageLayout.Undefined;
        return false;
    }

    private void RecordImageAccess(
        CommandBuffer commandBuffer,
        Image image,
        in ImageSubresourceRange range,
        ImageLayout layout,
        PipelineStageFlags stageMask,
        AccessFlags accessMask,
        uint queueFamilyIndex)
    {
        ulong generation = GetCurrentVulkanResourceGeneration(ObjectType.Image, image.Handle);
        VulkanImageAccessState next = ResolveCommandImageAccessState(
            layout,
            range.AspectMask,
            stageMask,
            accessMask,
            queueFamilyIndex,
            generation);
        PrimaryCommandEncoder.RecordImageAccess(commandBuffer, image, in range, in next);
    }

    private static VulkanImageAccessState ResolveCommandImageAccessState(
        ImageLayout layout,
        ImageAspectFlags aspectMask,
        PipelineStageFlags requestedStages = 0,
        AccessFlags requestedAccess = 0,
        uint queueFamilyIndex = Vk.QueueFamilyIgnored,
        ulong generation = 0)
    {
        const PipelineStageFlags shaderStages =
            PipelineStageFlags.VertexShaderBit |
            PipelineStageFlags.FragmentShaderBit |
            PipelineStageFlags.ComputeShaderBit;

        (PipelineStageFlags stages, AccessFlags access, ImageLayout descriptorLayout) = layout switch
        {
            ImageLayout.Undefined => (PipelineStageFlags.TopOfPipeBit, AccessFlags.None, ImageLayout.Undefined),
            ImageLayout.PresentSrcKhr => (PipelineStageFlags.BottomOfPipeBit, AccessFlags.MemoryReadBit, ImageLayout.Undefined),
            ImageLayout.ColorAttachmentOptimal or ImageLayout.AttachmentOptimal
                when (aspectMask & ImageAspectFlags.ColorBit) != 0 =>
                (PipelineStageFlags.ColorAttachmentOutputBit,
                 AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                 ImageLayout.Undefined),
            ImageLayout.ColorAttachmentOptimal or ImageLayout.AttachmentOptimal or
            ImageLayout.DepthAttachmentOptimal or ImageLayout.StencilAttachmentOptimal or ImageLayout.DepthStencilAttachmentOptimal =>
                (PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                 AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                 ImageLayout.Undefined),
            ImageLayout.DepthReadOnlyOptimal or ImageLayout.StencilReadOnlyOptimal or ImageLayout.DepthStencilReadOnlyOptimal =>
                (shaderStages | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                 AccessFlags.ShaderReadBit | AccessFlags.DepthStencilAttachmentReadBit,
                 ImageLayout.DepthStencilReadOnlyOptimal),
            ImageLayout.ShaderReadOnlyOptimal or ImageLayout.ReadOnlyOptimal =>
                (shaderStages, AccessFlags.ShaderReadBit, ImageLayout.ShaderReadOnlyOptimal),
            ImageLayout.TransferSrcOptimal => (PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit, ImageLayout.Undefined),
            ImageLayout.TransferDstOptimal => (PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, ImageLayout.Undefined),
            _ => (shaderStages, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit, ImageLayout.General),
        };

        if (layout == ImageLayout.General)
        {
            if (requestedStages != 0)
                stages = requestedStages;
            if (requestedAccess != 0)
                access = requestedAccess;
        }

        return new VulkanImageAccessState(
            layout,
            (PipelineStageFlags2)(ulong)stages,
            (AccessFlags2)(ulong)access,
            queueFamilyIndex,
            descriptorLayout,
            Serial: 0,
            ResourceGeneration: generation);
    }

    internal static int EnsureValidPassIndex(
        int passIndex,
        string opName,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata = null)
    {
        if (passIndex == VulkanBarrierPlanner.SwapchainPassIndex)
            passIndex = int.MinValue;

        bool hasMetadata = passMetadata is { Count: > 0 };
        if (!hasMetadata && passIndex != int.MinValue &&
            Enum.IsDefined<EDefaultRenderPass>((EDefaultRenderPass)passIndex))
            return passIndex;

        if (passIndex != int.MinValue && (!hasMetadata || ContainsPass(passMetadata!, passIndex)))
            return passIndex;

        int currentPass = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex == int.MinValue &&
            currentPass != int.MinValue &&
            (!hasMetadata || ContainsPass(passMetadata!, currentPass)))
        {
            return currentPass;
        }

        if (hasMetadata && !opName.Contains("Compute", StringComparison.OrdinalIgnoreCase))
        {
            const int preRenderPass = (int)EDefaultRenderPass.PreRender;
            if (ContainsPass(passMetadata!, preRenderPass))
                return preRenderPass;
        }

        if (!hasMetadata)
            return int.MinValue;

        bool preferCompute = opName.Contains("Compute", StringComparison.OrdinalIgnoreCase);
        bool preferTransfer = opName.Contains("Blit", StringComparison.OrdinalIgnoreCase);
        int firstPass = int.MaxValue;
        int preferredPass = int.MaxValue;
        foreach (RenderPassMetadata metadata in passMetadata!)
        {
            firstPass = Math.Min(firstPass, metadata.PassIndex);
            bool preferred = preferCompute
                ? metadata.Stage == ERenderGraphPassStage.Compute
                : preferTransfer
                    ? metadata.Stage == ERenderGraphPassStage.Transfer
                    : metadata.Stage == ERenderGraphPassStage.Graphics;
            if (preferred)
                preferredPass = Math.Min(preferredPass, metadata.PassIndex);
        }

        return preferredPass != int.MaxValue ? preferredPass : firstPass;

        static bool ContainsPass(IReadOnlyCollection<RenderPassMetadata> metadata, int candidate)
        {
            foreach (RenderPassMetadata pass in metadata)
                if (pass.PassIndex == candidate)
                    return true;
            return false;
        }
    }

    private static ImageLayout ResolveDescriptorImageLayout(
        IVkImageDescriptorSource source,
        DescriptorType descriptorType)
        => VulkanProgramUtilities.ResolveDescriptorImageLayout(source, descriptorType);

    private static VulkanImageAccessState ResolveVulkanImageAccessState(
        ImageLayout layout,
        ImageAspectFlags aspectMask)
        => ResolveCommandImageAccessState(layout, aspectMask);
}
