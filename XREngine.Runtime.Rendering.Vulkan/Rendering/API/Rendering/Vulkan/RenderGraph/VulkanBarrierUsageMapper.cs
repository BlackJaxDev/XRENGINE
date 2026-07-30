using Silk.NET.Vulkan;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Maps backend-neutral render-graph usage and synchronization contracts to Vulkan layouts, stages, access flags, and aspects.
/// </summary>
internal static class VulkanBarrierUsageMapper
{
    internal static ImageLayout ResolveLayout(ERenderPassResourceType type, VulkanPhysicalImageGroup? group = null)
        => type switch
        {
            ERenderPassResourceType.ColorAttachment or ERenderPassResourceType.ResolveAttachment => ImageLayout.ColorAttachmentOptimal,
            ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment => ImageLayout.DepthStencilAttachmentOptimal,
            ERenderPassResourceType.SampledTexture => ResolveSampledTextureLayout(group),
            ERenderPassResourceType.StorageTexture => ImageLayout.General,
            ERenderPassResourceType.TransferSource => ImageLayout.TransferSrcOptimal,
            ERenderPassResourceType.TransferDestination => ImageLayout.TransferDstOptimal,
            _ => ImageLayout.General
        };

    internal static PipelineStageFlags ResolveStage(ERenderPassResourceType type, ERenderGraphPassStage passStage)
    {
        return type switch
        {
            ERenderPassResourceType.ColorAttachment or ERenderPassResourceType.ResolveAttachment => PipelineStageFlags.ColorAttachmentOutputBit,
            ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment => PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            ERenderPassResourceType.TransferSource or ERenderPassResourceType.TransferDestination => PipelineStageFlags.TransferBit,
            ERenderPassResourceType.VertexBuffer or ERenderPassResourceType.IndexBuffer => PipelineStageFlags.VertexInputBit,
            ERenderPassResourceType.IndirectBuffer => PipelineStageFlags.DrawIndirectBit,
            ERenderPassResourceType.UniformBuffer => SampleStage(passStage),
            ERenderPassResourceType.StorageBuffer => StorageStage(passStage),
            ERenderPassResourceType.SampledTexture => SampleStage(passStage),
            ERenderPassResourceType.StorageTexture => StorageStage(passStage),
            _ => DefaultStage(passStage)
        };

        static PipelineStageFlags SampleStage(ERenderGraphPassStage stage)
            => stage switch
            {
                ERenderGraphPassStage.Compute => PipelineStageFlags.ComputeShaderBit,
                ERenderGraphPassStage.Transfer => PipelineStageFlags.TransferBit,
                _ => PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit
            };

        static PipelineStageFlags StorageStage(ERenderGraphPassStage stage)
            => stage switch
            {
                ERenderGraphPassStage.Compute => PipelineStageFlags.ComputeShaderBit,
                ERenderGraphPassStage.Transfer => PipelineStageFlags.TransferBit,
                _ => PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit
            };

        static PipelineStageFlags DefaultStage(ERenderGraphPassStage stage)
            => stage switch
            {
                ERenderGraphPassStage.Compute => PipelineStageFlags.ComputeShaderBit,
                ERenderGraphPassStage.Transfer => PipelineStageFlags.TransferBit,
                _ => PipelineStageFlags.AllGraphicsBit
            };
    }

    internal static AccessFlags ResolveAccess(ERenderPassResourceType type, ERenderGraphAccess accessIntent)
    {
        bool reads = accessIntent is ERenderGraphAccess.Read or ERenderGraphAccess.ReadWrite;
        bool writes = accessIntent is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite;

        AccessFlags flags = AccessFlags.None;

        switch (type)
        {
            case ERenderPassResourceType.ColorAttachment:
            case ERenderPassResourceType.ResolveAttachment:
                if (reads)
                    flags |= AccessFlags.ColorAttachmentReadBit;
                if (writes)
                    flags |= AccessFlags.ColorAttachmentWriteBit;
                break;
            case ERenderPassResourceType.DepthAttachment:
            case ERenderPassResourceType.StencilAttachment:
                if (reads)
                    flags |= AccessFlags.DepthStencilAttachmentReadBit;
                if (writes)
                    flags |= AccessFlags.DepthStencilAttachmentWriteBit;
                break;
            case ERenderPassResourceType.SampledTexture:
            case ERenderPassResourceType.UniformBuffer:
                flags |= AccessFlags.ShaderReadBit;
                if (type == ERenderPassResourceType.UniformBuffer)
                    flags |= AccessFlags.UniformReadBit;
                break;
            case ERenderPassResourceType.StorageTexture:
            case ERenderPassResourceType.StorageBuffer:
                if (reads)
                    flags |= AccessFlags.ShaderReadBit;
                if (writes)
                    flags |= AccessFlags.ShaderWriteBit;
                break;
            case ERenderPassResourceType.VertexBuffer:
                flags |= AccessFlags.VertexAttributeReadBit;
                break;
            case ERenderPassResourceType.IndexBuffer:
                flags |= AccessFlags.IndexReadBit;
                break;
            case ERenderPassResourceType.IndirectBuffer:
                flags |= AccessFlags.IndirectCommandReadBit;
                break;
            case ERenderPassResourceType.TransferSource:
                flags |= AccessFlags.TransferReadBit;
                break;
            case ERenderPassResourceType.TransferDestination:
                flags |= AccessFlags.TransferWriteBit;
                break;
            default:
                if (reads)
                    flags |= AccessFlags.MemoryReadBit;
                if (writes)
                    flags |= AccessFlags.MemoryWriteBit;
                break;
        }

        return flags == AccessFlags.None ? AccessFlags.MemoryReadBit : flags;
    }

    internal static PipelineStageFlags ResolveStageFromSync(
        RenderGraphStageMask stageMask,
        ERenderPassResourceType resourceType,
        ERenderGraphPassStage fallbackStage)
    {
        if (stageMask == RenderGraphStageMask.None)
            return ResolveStage(resourceType, fallbackStage);

        PipelineStageFlags flags = 0;
        if (stageMask.HasFlag(RenderGraphStageMask.TopOfPipe))
            flags |= PipelineStageFlags.TopOfPipeBit;
        if (stageMask.HasFlag(RenderGraphStageMask.VertexInput))
            flags |= PipelineStageFlags.VertexInputBit;
        if (stageMask.HasFlag(RenderGraphStageMask.VertexShader))
            flags |= PipelineStageFlags.VertexShaderBit;
        if (stageMask.HasFlag(RenderGraphStageMask.FragmentShader))
            flags |= PipelineStageFlags.FragmentShaderBit;
        if (stageMask.HasFlag(RenderGraphStageMask.EarlyFragmentTests))
            flags |= PipelineStageFlags.EarlyFragmentTestsBit;
        if (stageMask.HasFlag(RenderGraphStageMask.LateFragmentTests))
            flags |= PipelineStageFlags.LateFragmentTestsBit;
        if (stageMask.HasFlag(RenderGraphStageMask.ColorAttachmentOutput))
            flags |= PipelineStageFlags.ColorAttachmentOutputBit;
        if (stageMask.HasFlag(RenderGraphStageMask.ComputeShader))
            flags |= PipelineStageFlags.ComputeShaderBit;
        if (stageMask.HasFlag(RenderGraphStageMask.Transfer))
            flags |= PipelineStageFlags.TransferBit;
        if (stageMask.HasFlag(RenderGraphStageMask.DrawIndirect))
            flags |= PipelineStageFlags.DrawIndirectBit;
        if (stageMask.HasFlag(RenderGraphStageMask.Host))
            flags |= PipelineStageFlags.HostBit;
        if (stageMask.HasFlag(RenderGraphStageMask.AllGraphics))
            flags |= PipelineStageFlags.AllGraphicsBit;
        if (stageMask.HasFlag(RenderGraphStageMask.AllCommands))
            flags |= PipelineStageFlags.AllCommandsBit;

        return flags == 0
            ? ResolveStage(resourceType, fallbackStage)
            : flags;
    }

    internal static AccessFlags ResolveAccessFromSync(RenderGraphAccessMask accessMask, ERenderPassResourceType resourceType)
    {
        if (accessMask == RenderGraphAccessMask.None)
            return ResolveAccess(resourceType, ERenderGraphAccess.ReadWrite);

        AccessFlags flags = AccessFlags.None;
        if (accessMask.HasFlag(RenderGraphAccessMask.MemoryRead))
            flags |= AccessFlags.MemoryReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.MemoryWrite))
            flags |= AccessFlags.MemoryWriteBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.ShaderRead))
            flags |= AccessFlags.ShaderReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.ShaderWrite))
            flags |= AccessFlags.ShaderWriteBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.UniformRead))
            flags |= AccessFlags.UniformReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.ColorAttachmentRead))
            flags |= AccessFlags.ColorAttachmentReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.ColorAttachmentWrite))
            flags |= AccessFlags.ColorAttachmentWriteBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.DepthStencilRead))
            flags |= AccessFlags.DepthStencilAttachmentReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.DepthStencilWrite))
            flags |= AccessFlags.DepthStencilAttachmentWriteBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.VertexAttributeRead))
            flags |= AccessFlags.VertexAttributeReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.IndexRead))
            flags |= AccessFlags.IndexReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.IndirectCommandRead))
            flags |= AccessFlags.IndirectCommandReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.TransferRead))
            flags |= AccessFlags.TransferReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.TransferWrite))
            flags |= AccessFlags.TransferWriteBit;

        return flags == AccessFlags.None
            ? ResolveAccess(resourceType, ERenderGraphAccess.ReadWrite)
            : flags;
    }

    internal static ImageLayout ResolveLayoutFromSync(
        RenderGraphImageLayout? layout,
        ERenderPassResourceType resourceType,
        VulkanPhysicalImageGroup? group)
    {
        if (!layout.HasValue)
            return ResolveLayout(resourceType, group);

        return layout.Value switch
        {
            RenderGraphImageLayout.Undefined => ImageLayout.Undefined,
            RenderGraphImageLayout.ColorAttachment => ImageLayout.ColorAttachmentOptimal,
            RenderGraphImageLayout.DepthStencilAttachment => ImageLayout.DepthStencilAttachmentOptimal,
            RenderGraphImageLayout.RenderingLocalRead => ImageLayout.RenderingLocalRead,
            RenderGraphImageLayout.ShaderReadOnly => ResolveSampledTextureLayout(group),
            RenderGraphImageLayout.General => ImageLayout.General,
            RenderGraphImageLayout.TransferSource => ImageLayout.TransferSrcOptimal,
            RenderGraphImageLayout.TransferDestination => ImageLayout.TransferDstOptimal,
            RenderGraphImageLayout.Present => ImageLayout.PresentSrcKhr,
            _ => ResolveLayout(resourceType, group)
        };
    }

    internal static ImageAspectFlags ResolveAspect(VulkanPhysicalImageGroup group, ERenderPassResourceType type)
    {
        if (IsDepthFormat(group.Format) || type is ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment)
        {
            bool hasStencil = FormatHasStencil(group.Format) || type == ERenderPassResourceType.StencilAttachment;
            return hasStencil ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit;
        }

        return ImageAspectFlags.ColorBit;
    }

    internal static ImageLayout ResolveSampledTextureLayout(VulkanPhysicalImageGroup? group)
    {
        if (IsDepthOrStencilGroup(group))
            return ImageLayout.DepthStencilReadOnlyOptimal;

        ImageUsageFlags usage = group?.Usage ?? ImageUsageFlags.None;
        bool sampled = (usage & ImageUsageFlags.SampledBit) != 0;
        bool storage = (usage & ImageUsageFlags.StorageBit) != 0;
        return sampled && storage
            ? ImageLayout.General
            : ImageLayout.ShaderReadOnlyOptimal;
    }

    internal static bool IsDepthFormat(Format format)
        => format is Format.D16Unorm
            or Format.D32Sfloat
            or Format.D24UnormS8Uint
            or Format.D32SfloatS8Uint
            or Format.X8D24UnormPack32
            or Format.D16UnormS8Uint;

    internal static bool IsDepthOrStencilGroup(VulkanPhysicalImageGroup? group)
        => group is not null && IsDepthFormat(group.Format);

    internal static bool FormatHasStencil(Format format)
        => format is Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint;
}
