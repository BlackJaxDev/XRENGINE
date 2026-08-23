using Silk.NET.Vulkan;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

internal unsafe abstract partial class VkImageBackedTexture<TTexture> : VkTexture<TTexture>, IVkFrameBufferAttachmentSource where TTexture : XRTexture
{
    #region Image Layout Transitions

    /// <summary>
    /// Performs a full pipeline barrier to transition the image from <paramref name="oldLayout"/>
    /// to <paramref name="newLayout"/>. Layouts are first coerced to be valid for the
    /// image's actual usage flags.
    /// </summary>
    internal void TransitionImageLayout(ImageLayout oldLayout, ImageLayout newLayout)
    {
        if (!BackendContext.IsDeviceOperational || Image.Handle == 0)
            return;

        RefreshPhysicalGroupImageIfStale();

        ImageLayout liveLayout = CurrentImageLayout;
        if (liveLayout != oldLayout)
            oldLayout = liveLayout;

        oldLayout = CoerceLayoutForUsage(oldLayout);
        newLayout = CoerceLayoutForUsage(newLayout);
        AssembleTransitionImageLayout(oldLayout, newLayout, out ImageMemoryBarrier barrier, out PipelineStageFlags src, out PipelineStageFlags dst);
        ResourceCommandPort.PipelineBarrier(
            src,
            dst,
            1,
            &barrier,
            "VkImageBackedTexture.TransitionImageLayout");
        _currentImageLayout = newLayout;
        if (_physicalGroup is not null)
            _physicalGroup.LastKnownLayout = newLayout;
        ResetAttachmentLayoutTracking();
    }

    /// <summary>
    /// Coerces <see cref="ImageLayout.ShaderReadOnlyOptimal"/> to the stable descriptor
    /// layout required by the image usage. Sampled/storage images remain in
    /// <see cref="ImageLayout.General"/> so upload, descriptor publication, and later
    /// storage dispatches all agree on one whole-image layout.
    /// </summary>
    private ImageLayout CoerceLayoutForUsage(ImageLayout requested)
    {
        if (requested != ImageLayout.ShaderReadOnlyOptimal)
            return requested;

        bool canSample = (Usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0;
        bool canStore = (Usage & ImageUsageFlags.StorageBit) != 0;
        if (canSample && canStore)
            return ImageLayout.General;

        bool isDepthOrStencil = (AspectFlags & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0 ||
            VkFormatConversions.IsDepthStencilFormat(ResolvedFormat);
        if (canSample && isDepthOrStencil)
            return ImageLayout.DepthStencilReadOnlyOptimal;

        if (canSample)
            return requested;

        if (canStore)
            return ImageLayout.General;

        return ImageLayout.TransferSrcOptimal;
    }

    /// <summary>
    /// Builds the <see cref="ImageMemoryBarrier"/> and selects appropriate pipeline stages
    /// for transitioning from <paramref name="oldLayout"/> to <paramref name="newLayout"/>.
    /// Common transitions (undefinedâ†’transfer-dst, transfer-dstâ†’shader-read) use precise
    /// stages; other pairs derive stages/access per layout role, falling back to
    /// <c>AllCommands</c> only for unrecognized layouts.
    /// </summary>
    private void AssembleTransitionImageLayout(
        ImageLayout oldLayout,
        ImageLayout newLayout,
        out ImageMemoryBarrier barrier,
        out PipelineStageFlags sourceStage,
        out PipelineStageFlags destinationStage)
    {
        barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = AspectFlags,
                BaseMipLevel = 0,
                LevelCount = ResolvedMipLevels,
                BaseArrayLayer = 0,
                LayerCount = ResolvedArrayLayers,
            }
        };

        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            sourceStage = PipelineStageFlags.TransferBit;
            destinationStage = PipelineStageFlags.FragmentShaderBit;
        }
        else if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.ColorAttachmentOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.ColorAttachmentWriteBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.ColorAttachmentOutputBit;
        }
        else if (oldLayout == ImageLayout.Undefined && (newLayout == ImageLayout.DepthStencilAttachmentOptimal || newLayout == ImageLayout.DepthAttachmentOptimal))
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        }
        else if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.General)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
        }
        else if (oldLayout == ImageLayout.Undefined && (newLayout == ImageLayout.ShaderReadOnlyOptimal || newLayout == ImageLayout.DepthStencilReadOnlyOptimal))
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
        }
        else if (oldLayout == ImageLayout.ColorAttachmentOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.ColorAttachmentWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            sourceStage = PipelineStageFlags.ColorAttachmentOutputBit;
            destinationStage = PipelineStageFlags.FragmentShaderBit;
        }
        else if ((oldLayout == ImageLayout.DepthStencilAttachmentOptimal || oldLayout == ImageLayout.DepthAttachmentOptimal) &&
            (newLayout == ImageLayout.ShaderReadOnlyOptimal || newLayout == ImageLayout.DepthStencilReadOnlyOptimal))
        {
            barrier.SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            sourceStage = PipelineStageFlags.LateFragmentTestsBit;
            destinationStage = PipelineStageFlags.FragmentShaderBit;
        }
        else if (oldLayout == ImageLayout.ShaderReadOnlyOptimal && newLayout == ImageLayout.TransferSrcOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.ShaderReadBit;
            barrier.DstAccessMask = AccessFlags.TransferReadBit;
            sourceStage = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
            destinationStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferSrcOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferReadBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            sourceStage = PipelineStageFlags.TransferBit;
            destinationStage = PipelineStageFlags.FragmentShaderBit;
        }
        else if (oldLayout == ImageLayout.ColorAttachmentOptimal && newLayout == ImageLayout.TransferSrcOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.ColorAttachmentWriteBit;
            barrier.DstAccessMask = AccessFlags.TransferReadBit;
            sourceStage = PipelineStageFlags.ColorAttachmentOutputBit;
            destinationStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.General && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.ShaderWriteBit;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            sourceStage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
            destinationStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.General)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
            sourceStage = PipelineStageFlags.TransferBit;
            destinationStage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
        }
        else
        {
            // Derive stages/access from the layout roles instead of AllCommands.
            // Unrecognized layouts still fall back to broad masks inside the helpers.
            GetLayoutSourceSync(oldLayout, out sourceStage, out AccessFlags srcAccess);
            GetLayoutDestinationSync(newLayout, out destinationStage, out AccessFlags dstAccess);
            barrier.SrcAccessMask = srcAccess;
            barrier.DstAccessMask = dstAccess;
        }
    }

    /// <summary>
    /// Derives the pipeline stages and access mask covering all prior GPU work for an
    /// image leaving <paramref name="layout"/>. Unrecognized layouts fall back to broad masks.
    /// </summary>
    private static void GetLayoutSourceSync(ImageLayout layout, out PipelineStageFlags stage, out AccessFlags access)
    {
        switch (layout)
        {
            case ImageLayout.Undefined:
            case ImageLayout.Preinitialized:
                stage = PipelineStageFlags.TopOfPipeBit;
                access = 0;
                break;
            case ImageLayout.General:
                // Storage-image usage: written by compute or fragment shaders.
                stage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
                access = AccessFlags.ShaderWriteBit;
                break;
            case ImageLayout.ColorAttachmentOptimal:
                stage = PipelineStageFlags.ColorAttachmentOutputBit;
                access = AccessFlags.ColorAttachmentWriteBit;
                break;
            case ImageLayout.DepthStencilAttachmentOptimal:
            case ImageLayout.DepthAttachmentOptimal:
                stage = PipelineStageFlags.LateFragmentTestsBit;
                access = AccessFlags.DepthStencilAttachmentWriteBit;
                break;
            case ImageLayout.ShaderReadOnlyOptimal:
            case ImageLayout.DepthStencilReadOnlyOptimal:
                // Prior reads need execution ordering only; no writes to make available.
                stage = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
                access = 0;
                break;
            case ImageLayout.TransferSrcOptimal:
                stage = PipelineStageFlags.TransferBit;
                access = 0;
                break;
            case ImageLayout.TransferDstOptimal:
                stage = PipelineStageFlags.TransferBit;
                access = AccessFlags.TransferWriteBit;
                break;
            case ImageLayout.PresentSrcKhr:
                stage = PipelineStageFlags.BottomOfPipeBit;
                access = 0;
                break;
            default:
                stage = PipelineStageFlags.AllCommandsBit;
                access = AccessFlags.MemoryWriteBit;
                break;
        }
    }

    /// <summary>
    /// Derives the pipeline stages and access mask covering the first GPU work consuming
    /// an image entering <paramref name="layout"/>. Unrecognized layouts fall back to broad masks.
    /// </summary>
    private static void GetLayoutDestinationSync(ImageLayout layout, out PipelineStageFlags stage, out AccessFlags access)
    {
        switch (layout)
        {
            case ImageLayout.General:
                stage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
                access = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
                break;
            case ImageLayout.ColorAttachmentOptimal:
                stage = PipelineStageFlags.ColorAttachmentOutputBit;
                access = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;
                break;
            case ImageLayout.DepthStencilAttachmentOptimal:
            case ImageLayout.DepthAttachmentOptimal:
                stage = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
                access = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
                break;
            case ImageLayout.ShaderReadOnlyOptimal:
            case ImageLayout.DepthStencilReadOnlyOptimal:
                stage = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
                access = AccessFlags.ShaderReadBit;
                break;
            case ImageLayout.TransferSrcOptimal:
                stage = PipelineStageFlags.TransferBit;
                access = AccessFlags.TransferReadBit;
                break;
            case ImageLayout.TransferDstOptimal:
                stage = PipelineStageFlags.TransferBit;
                access = AccessFlags.TransferWriteBit;
                break;
            case ImageLayout.PresentSrcKhr:
                stage = PipelineStageFlags.BottomOfPipeBit;
                access = 0;
                break;
            default:
                stage = PipelineStageFlags.AllCommandsBit;
                access = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
                break;
        }
    }

    #endregion
}
