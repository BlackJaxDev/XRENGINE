using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Command-emission authority. The renderer facade supplies native services while
/// this owner transports one explicit recording context through the operation.
/// </summary>
internal sealed class VulkanCommandRecorder
{
    private int _activeRecordingScopes;

    public bool IsRecording => Volatile.Read(ref _activeRecordingScopes) > 0;

    public void EnterRecordingScope()
        => Interlocked.Increment(ref _activeRecordingScopes);

    public void ExitRecordingScope()
        => Interlocked.Decrement(ref _activeRecordingScopes);
    public bool Prepare(ref VulkanCommandRecordingContext context)
    {
        context.RecordedSwapchainWriteCount = 0;
        context.RecordedSwapchainFinalLayout = ImageLayout.Undefined;
        context.RecordingDeferredReason = string.Empty;
        context.QueryFrameOpsRequireRerecord = false;

        if (context.CommandBuffer.Handle != 0)
            return true;

        context.RecordingDeferredReason = "Cannot record a null Vulkan command-buffer handle.";
        return false;
    }

    public void Begin(Vk api, CommandBuffer commandBuffer)
    {
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
        };

        if (api.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
            throw new InvalidOperationException("Failed to begin recording command buffer.");
    }

    public Result End(
        VulkanRenderer renderer,
        CommandBuffer commandBuffer,
        out string trackingFailure)
        => renderer.EndCommandBufferTracked(
            commandBuffer,
            cacheVariant: true,
            out trackingFailure);
}
