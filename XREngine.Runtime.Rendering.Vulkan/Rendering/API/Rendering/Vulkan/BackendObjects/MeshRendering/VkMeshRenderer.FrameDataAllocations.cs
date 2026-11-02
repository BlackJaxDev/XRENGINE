using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    private bool IsFrameDataOwnerOpen(in PendingMeshDraw draw, out string reason)
    {
        if (IsRetired || Data.IsDestroyed || draw.CaptureOwnerToken is { IsOpen: false })
        {
            reason = "frame-data owner retired";
            return false;
        }
        reason = "Ready";
        return true;
    }

    /// <summary>Retains every range selected by this draw before native binding.</summary>
    private bool TryRetainDrawFrameData(CommandBuffer commandBuffer, VkRenderProgram program, int frameIndex, int drawSlot)
    {
        if (BackendContext.Resources.MappedFrameArena is not { } arena)
            return true;
        if (arena.TryGetDrawFamily(this, frameIndex, drawSlot, out VulkanMappedFrameAllocation? family) &&
            !BackendContext.Resources.TryRetainFrameDataAllocation(commandBuffer, family))
            return false;
        foreach (DescriptorBindingInfo binding in program.DescriptorBindings)
        {
            if (binding.DescriptorType is not (DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic))
                continue;
            VulkanMappedFrameSlice slice;
            if (program.TryGetAutoUniformBlock(binding.Set, binding.Binding, out AutoUniformBlockInfo block))
            {
                if (!_autoUniformBuffers.TryGetValue(block.InstanceName, out AutoUniformBuffer[]? buffers) || buffers.Length == 0)
                    return false;
                slice = buffers[ResolvePublishedAutoUniformBufferIndex(block, frameIndex, drawSlot, buffers.Length)].MappedSlice;
            }
            else
            {
                string name = NormalizeEngineUniformName(binding.Name ?? string.Empty);
                if (GetEngineUniformSize(binding.Name ?? string.Empty) == 0)
                    continue;
                if (!_engineUniformBuffers.TryGetValue(name, out EngineUniformBuffer[]? buffers) || buffers.Length == 0)
                    return false;
                slice = buffers[ResolveUniformBufferIndex(frameIndex, drawSlot, buffers.Length)].MappedSlice;
            }
            if (slice.IsValid && (slice.Allocation is not { } allocation ||
                !BackendContext.Resources.TryRetainFrameDataAllocation(commandBuffer, allocation)))
                return false;
        }
        return true;
    }
}
