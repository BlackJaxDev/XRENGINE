using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow native allocation and retirement adapters used by command-buffer
/// storage. The command authority never reaches back through the renderer or
/// output facade; lifetime decisions stay in <see cref="VulkanResourceRuntime"/>.
/// </summary>
internal sealed unsafe partial class VulkanCommandRuntime
{
    private bool _deviceLost => !DeviceContext.IsOperational;
    private object _computeDescriptorCacheLock => CommandBuffers.BindStateGate;

    private Result ResetVulkanCommandBufferTracked(CommandBuffer commandBuffer)
    {
        if (!ResourceRuntime.CanResetCommandBuffer(commandBuffer))
            throw new InvalidOperationException(
                $"Command buffer 0x{unchecked((ulong)commandBuffer.Handle):X} is not resettable.");

        Result result = Api.ResetCommandBuffer(commandBuffer, 0);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResetCommandBufferCall();
        if (result == Result.Success)
            ResourceRuntime.CompleteCommandBufferReset(unchecked((ulong)commandBuffer.Handle));
        return result;
    }

    private void FreeVulkanCommandBufferTracked(
        CommandPool commandPool,
        ref CommandBuffer commandBuffer,
        string owner)
    {
        if (commandBuffer.Handle == 0)
            return;

        if (!DeviceContext.IsOperational)
        {
            RemoveCommandBufferState(commandBuffer);
            ResourceRuntime.CompleteCommandBufferDestruction(commandBuffer);
            commandBuffer = default;
            return;
        }

        FreeTrackedCommandBuffer(
            Api,
            DeviceContext.Device,
            ResourceRuntime,
            ResourceRuntime.FramebufferRetirementFrameSlot,
            commandPool,
            ref commandBuffer,
            owner);
    }

    private void FreeVulkanCommandBuffersTracked(
        CommandPool commandPool,
        uint commandBufferCount,
        CommandBuffer* commandBuffers,
        string owner)
    {
        for (uint index = 0; index < commandBufferCount; index++)
            FreeVulkanCommandBufferTracked(
                commandPool,
                ref commandBuffers[index],
                $"{owner}[{index}]");
    }

    private bool IsCommandBufferPendingRetirement(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return false;

        lock (ResourceRuntime.Lifetime.Retirement.SyncRoot)
            return ResourceRuntime.Lifetime.Retirement.AllCommandBufferHandles.Contains(
                unchecked((ulong)commandBuffer.Handle));
    }

    private void RetireDescriptorPool(DescriptorPool descriptorPool)
        => ResourceRuntime.DescriptorLifetime.RetireDescriptorPool(descriptorPool);

    private void DestroyBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
        => ResourceRuntime.Buffers.Retire(buffer, memory, "CommandRuntime.ComputeTransient");

    private void SetDebugDescriptorSetNames(DescriptorSet[] descriptorSets, string prefix)
    {
        for (int index = 0; index < descriptorSets.Length; index++)
            ResourceRuntime.DescriptorLifetime.SetDebugName(
                descriptorSets[index],
                $"{prefix}[{index}]");
    }

    private void RecordVulkanDescriptorTableGeneration(string reason)
    {
        _ = reason;
        ResourceRuntime.DescriptorLifetime.RecordTableGeneration();
    }
}
