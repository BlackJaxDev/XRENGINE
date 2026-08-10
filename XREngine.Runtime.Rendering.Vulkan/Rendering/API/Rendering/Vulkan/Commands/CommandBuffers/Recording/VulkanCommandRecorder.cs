using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Command-emission authority over explicit native and device-lifetime inputs.
/// </summary>
internal sealed class VulkanCommandRecorder
{
    private int _activeRecordingScopes;

    /// <summary>
    /// Indicates whether the command recorder is currently in a recording state.
    /// </summary>
    public bool IsRecording => Volatile.Read(ref _activeRecordingScopes) > 0;

    /// <summary>
    /// Increments the count of active recording scopes, indicating that a new recording session has begun.
    /// </summary>
    public void EnterRecordingScope()
        => Interlocked.Increment(ref _activeRecordingScopes);

    /// <summary>
    /// Decrements the count of active recording scopes, indicating that a recording session has ended.
    /// </summary>
    public void ExitRecordingScope()
        => Interlocked.Decrement(ref _activeRecordingScopes);

    /// <summary>
    /// Prepares the command recorder for a new recording session.
    /// This method initializes the recording context and checks if the command buffer handle is valid.
    /// If the handle is null, it sets a deferred reason and returns false, indicating that recording cannot proceed.
    /// Otherwise, it resets the recorded swapchain write count and final layout,
    /// and returns true to indicate readiness for recording.
    /// </summary>
    /// <param name="context">The Vulkan command recording context to prepare.</param>
    /// <returns>True if the command recorder is ready for recording; otherwise, false.</returns>
    public bool Prepare(ref VulkanCommandRecordingContext context)
    {
        context.RecordedSwapchainWriteCount = 0;
        context.RecordedSwapchainFinalLayout = ImageLayout.Undefined;
        context.RecordingDeferredReason = string.Empty;
        context.FrameOpsRequireRerecord = false;

        if (context.CommandBuffer.Handle != 0)
            return true;

        context.RecordingDeferredReason = "Cannot record a null Vulkan command-buffer handle.";
        return false;
    }

    /// <summary>
    /// Begins recording commands into the specified Vulkan command buffer.
    /// </summary>
    /// <param name="api">The Vulkan API used to begin recording.</param>
    /// <param name="commandBuffer">The Vulkan command buffer to begin recording.</param>
    /// <exception cref="InvalidOperationException">Thrown if beginning the command buffer fails.</exception>
    public void Begin(
        Vk api,
        CommandBuffer commandBuffer,
        CommandBufferUsageFlags flags = 0)
    {
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = flags,
        };

        if (api.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
            throw new InvalidOperationException("Failed to begin recording command buffer.");
    }
}
