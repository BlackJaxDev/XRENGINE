using Silk.NET.Vulkan;
using System.Runtime.ExceptionServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow, generation-owned command entry point for backend wrappers. It owns no
/// renderer, output, or frame-planner reference; wrappers record directly through
/// its tracked encoder and wait before retiring transient staging resources.
/// </summary>
internal unsafe sealed class VulkanResourceCommandWrapperPort(
    VulkanBackendObjectContext context,
    VulkanCommandRuntime commandRuntime,
    VulkanResourceRuntime resourceRuntime,
    VulkanFrameTelemetry telemetry)
{
    private readonly VulkanBackendObjectContext _context = context;
    private readonly VulkanResourceRuntime _resourceRuntime = resourceRuntime;
    private readonly VulkanFrameTelemetry _telemetry = telemetry;

    internal VulkanCommandRuntime CommandRuntime { get; } = commandRuntime;

    internal VulkanSynchronousResourceCommandSession Begin(string owner)
        => new(_context, CommandRuntime, _resourceRuntime, _telemetry, owner);

    internal void CopyBuffer(
        Silk.NET.Vulkan.Buffer source,
        Silk.NET.Vulkan.Buffer destination,
        ulong size,
        ulong sourceOffset,
        ulong destinationOffset,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        BufferCopy copy = new()
        {
            SrcOffset = sourceOffset,
            DstOffset = destinationOffset,
            Size = size,
        };
        session.Encoder.CopyBuffer(session.CommandBuffer, source, destination, 1, &copy);
        session.CompleteAndWait();
    }

    internal void CopyBuffer(
        in VulkanFrameDataSlice sourceSlice,
        Silk.NET.Vulkan.Buffer destination,
        ulong destinationOffset,
        in VulkanSynchronousFrameDataArenaLease lease,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        BufferCopy copy = new()
        {
            SrcOffset = sourceSlice.Offset,
            DstOffset = destinationOffset,
            Size = sourceSlice.Length,
        };
        session.Encoder.CopyBuffer(
            session.CommandBuffer,
            sourceSlice.Buffer,
            destination,
            1,
            &copy);
        session.CompleteAndWait(lease.Arena, sourceSlice);
    }

    internal void CopyBufferToImage(
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout layout,
        ref BufferImageCopy region,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        fixed (BufferImageCopy* regionPtr = &region)
            session.Encoder.CopyBufferToImage(
                session.CommandBuffer,
                source,
                destination,
                layout,
                1,
                regionPtr);
        session.CompleteAndWait();
    }

    internal void CopyBufferToImage(
        in VulkanFrameDataSlice sourceSlice,
        Image destination,
        ImageLayout layout,
        ref BufferImageCopy region,
        in VulkanSynchronousFrameDataArenaLease lease,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        fixed (BufferImageCopy* regionPtr = &region)
            session.Encoder.CopyBufferToImage(
                session.CommandBuffer,
                sourceSlice.Buffer,
                destination,
                layout,
                1,
                regionPtr);
        session.CompleteAndWait(lease.Arena, sourceSlice);
    }

    internal void PipelineBarrier(
        PipelineStageFlags sourceStages,
        PipelineStageFlags destinationStages,
        uint imageBarrierCount,
        ImageMemoryBarrier* imageBarriers,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        session.Encoder.PipelineBarrier(
            session.CommandBuffer,
            sourceStages,
            destinationStages,
            0,
            0,
            null,
            0,
            null,
            imageBarrierCount,
            imageBarriers);
        session.CompleteAndWait();
    }

    internal void ClearColorImage(
        Image image,
        ImageLayout layout,
        ref ClearColorValue color,
        ref ImageSubresourceRange range,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        session.Encoder.ClearColorImage(session.CommandBuffer, image, layout, ref color, 1, ref range);
        session.CompleteAndWait();
    }

    internal void ClearDepthStencilImage(
        Image image,
        ImageLayout layout,
        ref ClearDepthStencilValue value,
        ref ImageSubresourceRange range,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        session.Encoder.Track(session.CommandBuffer, ObjectType.Image, image.Handle);
        _context.Api.CmdClearDepthStencilImage(session.CommandBuffer, image, layout, ref value, 1, ref range);
        session.CompleteAndWait();
    }

    internal void GenerateMipmaps(
        Image image,
        uint mipLevels,
        uint arrayLayers,
        ImageAspectFlags aspectMask,
        Extent3D extent,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = image,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspectMask,
                BaseArrayLayer = 0,
                LayerCount = arrayLayers,
                LevelCount = 1,
            },
        };
        int width = (int)extent.Width;
        int height = (int)extent.Height;
        for (uint level = 1; level < mipLevels; level++)
        {
            barrier.SubresourceRange.BaseMipLevel = level - 1;
            barrier.OldLayout = ImageLayout.TransferDstOptimal;
            barrier.NewLayout = ImageLayout.TransferSrcOptimal;
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.TransferReadBit;
            session.Encoder.PipelineBarrier(session.CommandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &barrier);

            int destinationWidth = Math.Max(width / 2, 1);
            int destinationHeight = Math.Max(height / 2, 1);
            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers { AspectMask = aspectMask, MipLevel = level - 1, LayerCount = arrayLayers },
                DstSubresource = new ImageSubresourceLayers { AspectMask = aspectMask, MipLevel = level, LayerCount = arrayLayers },
            };
            blit.SrcOffsets.Element1 = new Offset3D(width, height, 1);
            blit.DstOffsets.Element1 = new Offset3D(destinationWidth, destinationHeight, 1);
            session.Encoder.BlitImage(session.CommandBuffer, image, ImageLayout.TransferSrcOptimal, image, ImageLayout.TransferDstOptimal, ref blit, Filter.Linear);

            barrier.OldLayout = ImageLayout.TransferSrcOptimal;
            barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
            barrier.SrcAccessMask = AccessFlags.TransferReadBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            session.Encoder.PipelineBarrier(session.CommandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &barrier);
            width = destinationWidth;
            height = destinationHeight;
        }

        barrier.SubresourceRange.BaseMipLevel = mipLevels - 1;
        barrier.OldLayout = ImageLayout.TransferDstOptimal;
        barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        barrier.DstAccessMask = AccessFlags.ShaderReadBit;
        session.Encoder.PipelineBarrier(session.CommandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &barrier);
        session.CompleteAndWait();
    }

    internal bool TryDecompressBufferGDeflate(
        Silk.NET.Vulkan.Buffer source,
        ulong sourceOffset,
        ulong compressedSize,
        Silk.NET.Vulkan.Buffer destination,
        ulong destinationOffset,
        ulong decompressedSize,
        string owner)
    {
        if (!_context.Supports(EVulkanDeviceCapability.NvMemoryDecompression) ||
            !_context.Supports(EVulkanDeviceCapability.BufferDeviceAddress) ||
            compressedSize == 0 || decompressedSize == 0 ||
            _context.DeviceContext.ExtensionFunctions.NvMemoryDecompression is not { } decompression)
        {
            return false;
        }

        ulong methods = (ulong)_context.DeviceContext.MutableCapabilities._nvMemoryDecompressionMethods;
        if (methods == 0)
            return false;
        ulong sourceAddress = _context.Resources.Buffers.GetDeviceAddress(_context, source);
        ulong destinationAddress = _context.Resources.Buffers.GetDeviceAddress(_context, destination);
        if (sourceAddress == 0 || destinationAddress == 0)
            return false;

        DecompressMemoryRegionNV region = new()
        {
            SrcAddress = sourceAddress + sourceOffset,
            DstAddress = destinationAddress + destinationOffset,
            CompressedSize = compressedSize,
            DecompressedSize = decompressedSize,
            DecompressionMethod = (MemoryDecompressionMethodFlagsNV)(methods & (~methods + 1)),
        };
        try
        {
            using VulkanSynchronousResourceCommandSession session = Begin(owner);
            session.Encoder.Track(session.CommandBuffer, ObjectType.Buffer, source.Handle);
            session.Encoder.Track(session.CommandBuffer, ObjectType.Buffer, destination.Handle);
            decompression.CmdDecompressMemory(session.CommandBuffer, new ReadOnlySpan<DecompressMemoryRegionNV>(in region));
            session.CompleteAndWait();
            return true;
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning($"[Vulkan] VK_NV_memory_decompression upload failed: {exception.Message}");
            return false;
        }
    }
}

/// <summary>
/// One-shot graphics command buffer with an explicit fence-complete lifetime
/// receipt. It is a scope, not a callback: wrapper code encodes directly through
/// <see cref="Encoder"/>.
/// </summary>
internal unsafe sealed class VulkanSynchronousResourceCommandSession : IDisposable
{
    private readonly VulkanBackendObjectContext _context;
    private readonly VulkanCommandRuntime _commands;
    private readonly VulkanResourceRuntime _resources;
    private readonly VulkanFrameTelemetry _telemetry;
    private readonly CommandPool _pool;
    private readonly string _owner;
    private bool _completed;
    private bool _nativeSubmissionAccepted;
    private bool _commandBufferReleased;

    internal VulkanSynchronousResourceCommandSession(
        VulkanBackendObjectContext context,
        VulkanCommandRuntime commands,
        VulkanResourceRuntime resources,
        VulkanFrameTelemetry telemetry,
        string owner)
    {
        _context = context;
        _commands = commands;
        _resources = resources;
        _telemetry = telemetry;
        _owner = owner;
        _pool = commands.GetThreadGraphicsCommandPool(context.Api, context.DeviceContext, resources);
        CommandBuffer = commands.AllocateTrackedCommandBuffer(
            context.Api,
            context.DeviceContext,
            resources,
            _pool,
            CommandBufferLevel.Primary,
            owner);
        Encoder = new VulkanTrackedCommandEncoder(commands);
        commands.ResetBindState(Encoder, CommandBuffer);
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Result result = context.Api.BeginCommandBuffer(CommandBuffer, ref beginInfo);
        context.DeviceContext.ObserveNativeResult($"vkBeginCommandBuffer.{owner}", result);
        if (result != Result.Success)
        {
            ReleaseCommandBuffer();
            throw new InvalidOperationException($"Failed to begin synchronous resource command buffer ({result}).");
        }
    }

    internal CommandBuffer CommandBuffer { get; }
    internal VulkanTrackedCommandEncoder Encoder { get; }

    internal void CompleteAndWait()
        => CompleteAndWait(null, default);

    internal void CompleteAndWait(
        VulkanFrameDataArena? arena,
        in VulkanFrameDataSlice slice)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (Encoder.End(CommandBuffer) != Result.Success)
            throw new InvalidOperationException("Failed to end synchronous resource command buffer.");

        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        Result result = _context.Api.CreateFence(_context.Device, ref fenceInfo, null, out Fence fence);
        _context.DeviceContext.ObserveNativeResult($"vkCreateFence.{_owner}", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create synchronous resource fence ({result}).");

        bool arenaPrepared = false;
        bool arenaSubmitted = false;
        bool debtRetired = false;
        try
        {
            if (arena is not null)
            {
                if (!slice.IsValid || slice.ArenaIdentity != arena.Identity ||
                    !arena.TryPrepareFrameSlotForSubmission(0, slice.Generation))
                {
                    throw new InvalidOperationException(
                        "Failed to prepare the synchronous frame-data slice for submission.");
                }
                arenaPrepared = true;
            }

            CommandBuffer commandBuffer = CommandBuffer;
            SubmitInfo submit = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            VulkanSubmissionDiagnosticContext diagnosticContext = default;
            VulkanSubmissionReceipt receipt =
                _commands.SubmitToQueueTrackedWithDisposition(
                _context.DeviceContext.GraphicsQueue,
                ref submit,
                fence,
                in diagnosticContext,
                out _,
                out _,
                _owner);
            if (!receipt.SubmissionAccepted)
                throw new InvalidOperationException($"Failed to submit synchronous resource command ({receipt.Result}).");
            _nativeSubmissionAccepted = true;

            if (arena is not null)
            {
                arena.MarkFrameSlotSubmitted(0, slice.Generation);
                arenaSubmitted = true;
            }

            Exception? publicationFailure = null;
            try
            {
                _resources.RecordSynchronousGraphicsSubmission(
                    CommandBuffer,
                    fence,
                    _context.DeviceContext.GraphicsQueue);
            }
            catch (Exception failure)
            {
                publicationFailure = failure;
            }
            Fence* fencePtr = &fence;
            result = _context.Api.WaitForFences(_context.Device, 1, fencePtr, true, ulong.MaxValue);
            _context.DeviceContext.ObserveNativeResult($"vkWaitForFences.{_owner}", result);
            if (result != Result.Success)
            {
                debtRetired = true;
                _commands.RetireIncompleteSynchronousSubmission(
                    CommandBuffer,
                    _pool,
                    fence,
                    arena,
                    in slice,
                    removeOneTimeOwner: false,
                    _owner,
                    completeSynchronousLifetime: true);
                throw new InvalidOperationException($"Failed to wait for synchronous resource command ({result}).");
            }
            try
            {
                _commands.CompleteTrackedFence(fence);
                if (arena is not null &&
                    !arena.TryResetFrameSlot(0, slice.Generation, submissionCompletionProven: true))
                {
                    throw new InvalidOperationException(
                        "The synchronous frame-data slot could not be reopened after fence completion.");
                }
            }
            catch
            {
                debtRetired = true;
                _commands.RetireIncompleteSynchronousSubmission(
                    CommandBuffer,
                    _pool,
                    fence,
                    arena,
                    in slice,
                    removeOneTimeOwner: false,
                    _owner,
                    completeSynchronousLifetime: true);
                throw;
            }
            _completed = true;
            if (publicationFailure is not null)
                ExceptionDispatchInfo.Capture(publicationFailure).Throw();
        }
        finally
        {
            if (arenaPrepared && !arenaSubmitted && arena is not null)
                _ = arena.TryCancelFrameSlotSubmission(0, slice.Generation);
            if (!debtRetired)
                _context.Api.DestroyFence(_context.Device, fence, null);
            if (_completed)
                ReleaseCommandBuffer();
        }
    }

    public void Dispose()
    {
        if (!_completed && !_nativeSubmissionAccepted)
            ReleaseCommandBuffer();
    }

    private void ReleaseCommandBuffer()
    {
        if (_commandBufferReleased)
            return;
        _commandBufferReleased = true;
        CommandBuffer commandBuffer = CommandBuffer;
        if (commandBuffer.Handle != 0)
            lock (_commands.Pools.Gate)
                _context.Api.FreeCommandBuffers(_context.Device, _pool, 1, ref commandBuffer);
        _resources.CompleteSynchronousCommandBuffer(CommandBuffer);
    }
}
