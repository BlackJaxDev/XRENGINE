using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Per-call command capability used by physical planning to preserve the
/// auto-exposure image without retaining the command authority.
/// </summary>
internal readonly unsafe struct VulkanAutoExposureHistoryCommandCapability(
    VulkanCommandRuntime commandRuntime)
{
    internal bool TryCopy(
        VulkanPhysicalImageGroup? oldGroup,
        VulkanPhysicalImageGroup newGroup,
        string sourceLabel)
    {
        if (!IsUsableHistory(oldGroup) ||
            !IsUsableTarget(newGroup) ||
            ReferenceEquals(oldGroup, newGroup) ||
            oldGroup!.Format != newGroup.Format ||
            oldGroup.ResolvedExtent.Width != newGroup.ResolvedExtent.Width ||
            oldGroup.ResolvedExtent.Height != newGroup.ResolvedExtent.Height ||
            oldGroup.ResolvedExtent.Depth != newGroup.ResolvedExtent.Depth)
        {
            return false;
        }

        ImageLayout oldLayout = oldGroup.LastKnownLayout;
        ImageLayout newCurrentLayout = newGroup.LastKnownLayout;
        ImageLayout newRestoreLayout = newCurrentLayout == ImageLayout.Undefined
            ? VulkanResourcePlanningCompatibility.ResolveInitialPhysicalGroupLayout(
                newGroup.Usage,
                VulkanResourceAllocator.IsDepthStencilFormat(newGroup.Format))
            : newCurrentLayout;

        using var scope = commandRuntime.NewCommandScope();
        const PipelineStageFlags autoExposureStages =
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;

        Transition(
            scope.CommandBuffer,
            oldGroup,
            oldLayout,
            ImageLayout.TransferSrcOptimal,
            AccessFlags.ShaderWriteBit,
            AccessFlags.TransferReadBit,
            autoExposureStages,
            PipelineStageFlags.TransferBit);
        Transition(
            scope.CommandBuffer,
            newGroup,
            newCurrentLayout,
            ImageLayout.TransferDstOptimal,
            newCurrentLayout == ImageLayout.Undefined ? AccessFlags.None : AccessFlags.ShaderWriteBit,
            AccessFlags.TransferWriteBit,
            newCurrentLayout == ImageLayout.Undefined ? PipelineStageFlags.TopOfPipeBit : autoExposureStages,
            PipelineStageFlags.TransferBit);

        ImageCopy copy = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            Extent = new Extent3D(
                Math.Max(1u, oldGroup.ResolvedExtent.Width),
                Math.Max(1u, oldGroup.ResolvedExtent.Height),
                Math.Max(1u, oldGroup.ResolvedExtent.Depth)),
        };

        commandRuntime.CopyImageTracked(
            scope.CommandBuffer,
            oldGroup.Image,
            ImageLayout.TransferSrcOptimal,
            newGroup.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &copy);

        Transition(
            scope.CommandBuffer,
            newGroup,
            ImageLayout.TransferDstOptimal,
            newRestoreLayout,
            AccessFlags.TransferWriteBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            PipelineStageFlags.TransferBit,
            autoExposureStages);
        Transition(
            scope.CommandBuffer,
            oldGroup,
            ImageLayout.TransferSrcOptimal,
            oldLayout,
            AccessFlags.TransferReadBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            PipelineStageFlags.TransferBit,
            autoExposureStages);

        oldGroup.LastKnownLayout = oldLayout;
        newGroup.LastKnownLayout = newRestoreLayout;
        Debug.VulkanEvery(
            $"Vulkan.AutoExposure.HistoryPreserve.{sourceLabel}",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Preserved auto exposure history via {0}: src=0x{1:X} dst=0x{2:X} layout={3}->{4}.",
            sourceLabel,
            oldGroup.Image.Handle,
            newGroup.Image.Handle,
            oldLayout,
            newRestoreLayout);
        return true;
    }

    private void Transition(
        CommandBuffer commandBuffer,
        VulkanPhysicalImageGroup group,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        AccessFlags srcAccess,
        AccessFlags dstAccess,
        PipelineStageFlags srcStage,
        PipelineStageFlags dstStage)
    {
        if (oldLayout == newLayout)
            return;

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = group.Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = Math.Max(1u, group.MipLevels),
                BaseArrayLayer = 0,
                LayerCount = Math.Max(1u, group.Template.Layers),
            },
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
        };

        commandRuntime.CmdPipelineBarrierTracked(
            commandBuffer,
            srcStage,
            dstStage,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
    }

    private static bool IsUsableHistory(VulkanPhysicalImageGroup? group)
        => group is not null &&
           group.IsAllocated &&
           group.Image.Handle != 0 &&
           group.LastKnownLayout != ImageLayout.Undefined;

    private static bool IsUsableTarget(VulkanPhysicalImageGroup? group)
        => group is not null && group.IsAllocated && group.Image.Handle != 0;
}
