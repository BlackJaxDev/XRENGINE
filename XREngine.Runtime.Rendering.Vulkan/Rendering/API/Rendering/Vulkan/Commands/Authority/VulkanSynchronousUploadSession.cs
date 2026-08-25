using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// A renderer-free, synchronous graphics-upload command session. The caller owns
/// staging allocation and retires it only after <see cref="CompleteAndWait"/>
/// confirms that the native fence completed.
/// </summary>
internal unsafe sealed class VulkanSynchronousUploadSession : IDisposable
{
    private readonly Vk _api;
    private readonly VulkanDeviceContext _deviceContext;
    private readonly VulkanCommandRuntime _commands;
    private readonly CommandPool _pool;
    private bool _completed;

    internal VulkanSynchronousUploadSession(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanCommandRuntime commands,
        VulkanResourceRuntime resources,
        string owner)
    {
        _api = api;
        _deviceContext = deviceContext;
        _commands = commands;
        _pool = commands.GetThreadGraphicsCommandPool(api, deviceContext, resources);
        CommandBuffer = commands.AllocateTrackedCommandBuffer(
            api,
            deviceContext,
            resources,
            _pool,
            CommandBufferLevel.Primary,
            owner);
        Encoder = new VulkanTrackedCommandEncoder(commands);
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Result result = commands.BeginTrackedCommandBuffer(
            CommandBuffer,
            ref beginInfo,
            owner);
        deviceContext.ObserveNativeResult($"vkBeginCommandBuffer.{owner}", result);
        if (result != Result.Success)
        {
            ReleaseCommandBuffer();
            throw new InvalidOperationException($"Failed to begin synchronous upload command buffer ({result}).");
        }
    }

    internal CommandBuffer CommandBuffer { get; }
    internal VulkanTrackedCommandEncoder Encoder { get; }

    internal void CompleteAndWait(Image image, Buffer stagingBuffer, string owner)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (image.Handle == 0 || stagingBuffer.Handle == 0)
            throw new ArgumentException("Synchronous upload requires live image and staging-buffer handles.");

        if (Encoder.End(CommandBuffer) != Result.Success)
            throw new InvalidOperationException("Failed to end synchronous upload command buffer.");

        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        Result result = _api.CreateFence(_deviceContext.Device, ref fenceInfo, null, out Fence fence);
        _deviceContext.ObserveNativeResult($"vkCreateFence.{owner}", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create synchronous upload fence ({result}).");

        try
        {
            CommandBuffer commandBuffer = CommandBuffer;
            SubmitInfo submit = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            VulkanSubmissionDiagnosticContext diagnosticContext = default;
            VulkanSubmissionReceipt receipt = _commands.SubmitToQueueTrackedWithDisposition(
                _deviceContext.GraphicsQueue,
                ref submit,
                fence,
                in diagnosticContext,
                out _,
                out _,
                owner);
            result = receipt.Result;
            if (!receipt.SubmissionAccepted)
                throw new InvalidOperationException($"Failed to submit synchronous upload ({result}).");

            Fence* fencePtr = &fence;
            result = _api.WaitForFences(_deviceContext.Device, 1, fencePtr, true, ulong.MaxValue);
            _deviceContext.ObserveNativeResult($"vkWaitForFences.{owner}", result);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to wait for synchronous upload ({result}).");
            if (!receipt.LifetimePinsTransferred)
                _commands.ReleaseSubmissionResourceLifetimePins(ref submit);
            _commands.CompleteTrackedFence(fence);
            _completed = true;
        }
        finally
        {
            _api.DestroyFence(_deviceContext.Device, fence, null);
            if (_completed)
                ReleaseCommandBuffer();
        }
    }

    public void Dispose()
    {
        if (!_completed)
            throw new InvalidOperationException("A synchronous upload session must complete before disposal.");
    }

    private void ReleaseCommandBuffer()
    {
        CommandBuffer commandBuffer = CommandBuffer;
        _commands.FreeCompletedSynchronousCommandBuffer(
            _pool,
            ref commandBuffer,
            "SynchronousUploadSession");
    }
}
