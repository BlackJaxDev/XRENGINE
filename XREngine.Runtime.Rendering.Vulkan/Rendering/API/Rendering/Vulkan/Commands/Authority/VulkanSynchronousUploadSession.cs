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
    private readonly VulkanResourceRuntime _resources;
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
        _resources = resources;
        _pool = commands.GetThreadGraphicsCommandPool(api, deviceContext, resources);
        CommandBuffer = commands.AllocateTrackedCommandBuffer(
            api,
            deviceContext,
            resources,
            _pool,
            CommandBufferLevel.Primary,
            owner);
        Encoder = new VulkanTrackedCommandEncoder(api, deviceContext, commands, resources);
        Encoder.BeginTracking(CommandBuffer);
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Result result = api.BeginCommandBuffer(CommandBuffer, ref beginInfo);
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
            lock (_commands.CommandBuffers.OneTimeSubmitGate)
                result = _api.QueueSubmit(_deviceContext.GraphicsQueue, 1, ref submit, fence);
            _deviceContext.ObserveNativeResult($"vkQueueSubmit.{owner}", result);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to submit synchronous upload ({result}).");

            _resources.RecordSynchronousGraphicsSubmission(
                CommandBuffer,
                fence,
                _deviceContext.GraphicsQueue,
                image,
                stagingBuffer);
            Fence* fencePtr = &fence;
            result = _api.WaitForFences(_deviceContext.Device, 1, fencePtr, true, ulong.MaxValue);
            _deviceContext.ObserveNativeResult($"vkWaitForFences.{owner}", result);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to wait for synchronous upload ({result}).");
            _resources.CompleteSynchronousFence(fence);
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
        if (commandBuffer.Handle != 0)
        {
            lock (_commands.Pools.Gate)
                _api.FreeCommandBuffers(_deviceContext.Device, _pool, 1, ref commandBuffer);
        }
        _resources.CompleteSynchronousCommandBuffer(CommandBuffer);
    }
}
