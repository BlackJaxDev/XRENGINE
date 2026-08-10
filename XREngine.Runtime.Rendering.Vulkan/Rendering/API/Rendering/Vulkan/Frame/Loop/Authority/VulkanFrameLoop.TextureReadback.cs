using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using XREngine.Rendering.Resources;
using XREngine.Rendering.RenderGraph;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns synchronous native texture readbacks performed during desktop frame policy.</summary>
internal sealed unsafe partial class VulkanFrameLoop
{
    private const ulong AutoExposureByteCount = sizeof(float);

    /// <summary>Reads a bounded transfer-source buffer range for diagnostic consumers.</summary>
    private bool TryReadBufferBytesForDiagnosticsCore(
        VulkanBackendObjectContext backendContext,
        XRDataBuffer? sourceBuffer,
        uint sourceByteOffset,
        Span<byte> destination,
        out string reason)
    {
        reason = "<missing>";
        if (sourceBuffer is null)
            return false;
        if (destination.IsEmpty)
        {
            reason = "<empty>";
            return true;
        }

        ulong offset = sourceByteOffset;
        ulong byteCount = (ulong)destination.Length;
        if (offset >= sourceBuffer.Length || byteCount > sourceBuffer.Length - offset)
        {
            reason = $"<out-of-range:{offset}+{byteCount}/{sourceBuffer.Length}>";
            return false;
        }

        if (_resourceRuntime.WrapperLookup.GetOrCreate(sourceBuffer, generateNow: false) is not VkDataBuffer
            {
                IsGenerated: true,
                BufferHandle: { } sourceHandle,
                LastUsageFlags: var usage,
            } || sourceHandle.Handle == 0)
        {
            reason = "<no-generated-vulkan-buffer>";
            return false;
        }
        if ((usage & BufferUsageFlags.TransferSrcBit) == 0)
        {
            reason = $"<missing-transfer-src:{usage}>";
            return false;
        }

        try
        {
            return _commandRuntime.TryReadBufferBytes(sourceHandle, offset, destination, out reason);
        }
        catch (Exception ex)
        {
            reason = $"<{ex.GetType().Name}>";
            Debug.VulkanWarningEvery(
                $"Vulkan.Readback.BufferDiagnostics.{RuntimeHelpers.GetHashCode(sourceBuffer)}.{sourceByteOffset}.{destination.Length}",
                TimeSpan.FromSeconds(2),
                "[VulkanCounters] failed diagnostic buffer readback buffer='{0}' offset={1} length={2}: {3}: {4}",
                sourceBuffer.AttributeName ?? sourceBuffer.Target.ToString(), sourceByteOffset,
                destination.Length, ex.GetType().Name, ex.Message);
            return false;
        }
    }

    private bool TryReadDesktopAutoExposure(
        in FrameOpContext context,
        out double exposure,
        out string diagnostic)
    {
        exposure = 0.0;
        diagnostic = string.Empty;
        if (!_deviceContext.IsOperational)
        {
            diagnostic = $"Desktop AutoExposureTex readback rejected while device state is {_deviceContext.State}.";
            return false;
        }

        if (context.ResourceRegistry is null ||
            !context.ResourceRegistry.TextureRecords.TryGetValue(
                DefaultRenderPipeline.AutoExposureTextureName,
                out RenderTextureResource? record) ||
            record.Instance is null)
        {
            diagnostic = "Desktop AutoExposureTex is not registered with a live texture instance.";
            return false;
        }

        if (!TryResolvePlannerScopedExposureImage(in context, out VulkanPhysicalImageGroup? exposureGroup, out ImageLayout sourceLayout, out diagnostic))
            return false;

        if (exposureGroup is not { } group ||
            group.Format is not (Format.R16Sfloat or Format.R32Sfloat) ||
            group.Samples != SampleCountFlags.Count1Bit ||
            group.ResolvedExtent.Width != 1 ||
            group.ResolvedExtent.Height != 1 ||
            (group.Usage & ImageUsageFlags.TransferSrcBit) == 0)
        {
            diagnostic = exposureGroup is null
                ? "Desktop AutoExposureTex has no physical image group in the active planner scope."
                : $"Desktop AutoExposureTex requires a single-sample 1x1 R16Sfloat or R32Sfloat image with transfer-source usage; found {exposureGroup.Format}/{exposureGroup.Samples}/{exposureGroup.ResolvedExtent.Width}x{exposureGroup.ResolvedExtent.Height}/{exposureGroup.Usage}.";
            return false;
        }

        if (!TryReadExposureSample(group.Image, group.Format, sourceLayout, out float sample, out diagnostic))
            return false;
        if (!float.IsFinite(sample))
        {
            diagnostic = "Desktop AutoExposureTex returned a non-finite sample.";
            return false;
        }

        exposure = sample;
        diagnostic = "Read 1x1 desktop AutoExposureTex from the active Vulkan planner generation.";
        return true;
    }

    private bool TryResolvePlannerScopedExposureImage(
        in FrameOpContext context,
        out VulkanPhysicalImageGroup? exposureGroup,
        out ImageLayout layout,
        out string diagnostic)
    {
        exposureGroup = null;
        layout = ImageLayout.Undefined;
        diagnostic = string.Empty;
        ResourcePlannerRuntimeState state;
        try { state = _framePlanner.GetPublishedResourcePlannerGeneration().State; }
        catch (InvalidOperationException ex)
        {
            diagnostic = $"Desktop AutoExposureTex planner state is unavailable: {ex.Message}";
            return false;
        }
        if (state.LastActiveFrameOpContext is not FrameOpContext activeContext ||
            !ReferenceEquals(activeContext.ResourceRegistry, context.ResourceRegistry) ||
            activeContext.PipelineIdentity != context.PipelineIdentity ||
            activeContext.ViewportIdentity != context.ViewportIdentity ||
            activeContext.ResourceGeneration != context.ResourceGeneration)
        {
            diagnostic = "Desktop AutoExposureTex is not owned by the currently published planner scope.";
            return false;
        }
        if (!state.ResourceAllocator.TryGetPhysicalGroupForResource(DefaultRenderPipeline.AutoExposureTextureName, out exposureGroup) ||
            exposureGroup is null || !exposureGroup.IsAllocated || exposureGroup.Image.Handle == 0)
        {
            diagnostic = "Desktop AutoExposureTex has no allocated physical image in the active planner scope.";
            return false;
        }
        layout = exposureGroup.GetKnownLayout(0, 1, 0, 1);
        if (layout == ImageLayout.Undefined)
        {
            diagnostic = "Desktop AutoExposureTex has no tracked subresource layout in the active planner scope.";
            return false;
        }
        return true;
    }

    private bool TryReadExposureSample(Image sourceImage, Format sourceFormat, ImageLayout sourceLayout, out float sample, out string diagnostic)
    {
        sample = 0.0f;
        diagnostic = string.Empty;
        Vk api = _deviceContext.Api;
        CommandBuffer commandBuffer = default;
        Fence fence = default;
        bool nativeResourcesMayBeReleased = true;
        try
        {
            ulong byteCount = sourceFormat == Format.R16Sfloat ? sizeof(ushort) : AutoExposureByteCount;
            if (!_resourceRuntime.TryAcquireSynchronousFrameDataArenaLease(out VulkanSynchronousFrameDataArenaLease arenaLease))
            {
                diagnostic = "Desktop AutoExposureTex could not acquire the synchronous frame-data arena.";
                return false;
            }
            using VulkanSynchronousFrameDataArenaLease ownedArenaLease = arenaLease;
            VulkanFrameDataArena arena = ownedArenaLease.Arena;
            if (!arena.TryAllocate(
                    0,
                    EVulkanFrameDataLane.Readback,
                    byteCount,
                    alignment: 4,
                    out VulkanFrameDataSlice stagingSlice))
            {
                diagnostic = "Desktop AutoExposureTex could not reserve a frame-data readback slice.";
                return false;
            }
            if (!TryRecordExposureCopy(api, sourceImage, sourceLayout, stagingSlice.Buffer, stagingSlice.Offset, out commandBuffer, out diagnostic) ||
                !TrySubmitExposureCopyAndWait(
                    api,
                    commandBuffer,
                    sourceImage,
                    stagingSlice,
                    in ownedArenaLease,
                    out fence,
                    out nativeResourcesMayBeReleased,
                    out diagnostic))
                return false;
            if (!arena.TryBeginRead(stagingSlice, out VulkanFrameDataReadScope readScope))
            {
                diagnostic = "Desktop AutoExposureTex could not invalidate its frame-data readback slice.";
                return false;
            }
            using (readScope)
            fixed (byte* mappedPointer = readScope.Bytes)
                sample = sourceFormat == Format.R16Sfloat ? (float)*(Half*)mappedPointer : *(float*)mappedPointer;
            RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
            RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes((long)byteCount);
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = $"Desktop AutoExposureTex native readback failed: {ex.Message}";
            return false;
        }
        finally
        {
            if (nativeResourcesMayBeReleased && fence.Handle != 0)
                api.DestroyFence(_deviceContext.Device, fence, null);
            if (nativeResourcesMayBeReleased && commandBuffer.Handle != 0)
                DestroyTrackedExposureCommandBuffer(api, ref commandBuffer);
        }
    }

    private bool TryRecordExposureCopy(Vk api, Image sourceImage, ImageLayout sourceLayout, Buffer stagingBuffer, ulong stagingBufferOffset, out CommandBuffer commandBuffer, out string diagnostic)
    {
        commandBuffer = default;
        diagnostic = string.Empty;
        CommandPool pool = _commandRuntime.Pools.PrimaryGraphics;
        if (pool.Handle == 0)
        {
            diagnostic = "Desktop AutoExposureTex readback has no graphics command pool.";
            return false;
        }
        CommandBufferAllocateInfo allocateInfo = new() { SType = StructureType.CommandBufferAllocateInfo, CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
        Result result;
        lock (_commandRuntime.Pools.Gate)
            result = api.AllocateCommandBuffers(_deviceContext.Device, ref allocateInfo, out commandBuffer);
        _deviceContext.ObserveNativeResult("vkAllocateCommandBuffers.DesktopAutoExposureReadback", result);
        if (result != Result.Success || commandBuffer.Handle == 0)
        {
            diagnostic = $"vkAllocateCommandBuffers failed ({result}).";
            return false;
        }
        _resourceRuntime.RegisterSynchronousCommandBuffer(commandBuffer, pool, CommandBufferLevel.Primary, "DesktopAutoExposureReadback.CommandBuffer");
        CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        result = api.BeginCommandBuffer(commandBuffer, ref beginInfo);
        _deviceContext.ObserveNativeResult("vkBeginCommandBuffer.DesktopAutoExposureReadback", result);
        if (result != Result.Success)
        {
            diagnostic = $"vkBeginCommandBuffer failed ({result}).";
            return false;
        }
        ImageSubresourceRange range = new() { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 };
        ImageMemoryBarrier toTransfer = new()
        {
            SType = StructureType.ImageMemoryBarrier, OldLayout = sourceLayout, NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Image = sourceImage, SubresourceRange = range,
            SrcAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit | AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
        };
        api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toTransfer);
        BufferImageCopy copy = new()
        {
            BufferOffset = stagingBufferOffset,
            ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            ImageExtent = new Extent3D(1, 1, 1),
        };
        api.CmdCopyImageToBuffer(commandBuffer, sourceImage, ImageLayout.TransferSrcOptimal, stagingBuffer, 1, &copy);
        ImageMemoryBarrier restore = toTransfer;
        restore.OldLayout = ImageLayout.TransferSrcOptimal;
        restore.NewLayout = sourceLayout;
        restore.SrcAccessMask = AccessFlags.TransferReadBit;
        restore.DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit | AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
        api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &restore);
        result = _commandRuntime.EndCommandBufferTracked(commandBuffer);
        _deviceContext.ObserveNativeResult("vkEndCommandBuffer.DesktopAutoExposureReadback", result);
        if (result == Result.Success)
            return true;
        diagnostic = $"vkEndCommandBuffer failed ({result}).";
        return false;
    }

    private bool TrySubmitExposureCopyAndWait(
        Vk api,
        CommandBuffer commandBuffer,
        Image sourceImage,
        in VulkanFrameDataSlice stagingSlice,
        in VulkanSynchronousFrameDataArenaLease arenaLease,
        out Fence fence,
        out bool nativeResourcesMayBeReleased,
        out string diagnostic)
    {
        fence = default;
        nativeResourcesMayBeReleased = true;
        diagnostic = string.Empty;
        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        Result result;
        fixed (Fence* fencePtr = &fence)
            result = api.CreateFence(_deviceContext.Device, ref fenceInfo, null, fencePtr);
        _deviceContext.ObserveNativeResult("vkCreateFence.DesktopAutoExposureReadback", result);
        if (result != Result.Success)
        {
            diagnostic = $"vkCreateFence failed ({result}).";
            return false;
        }
        if (!arenaLease.TryPrepare(stagingSlice))
        {
            diagnostic = "Desktop AutoExposureTex could not prepare its frame-data readback slice.";
            return false;
        }
        SubmitInfo submit = new() { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
        VulkanSubmissionDiagnosticContext diagnosticContext = default;
        VulkanSubmissionReceipt receipt = _commandRuntime.SubmitToQueueTrackedWithDisposition(
            _deviceContext.GraphicsQueue,
            ref submit,
            fence,
            in diagnosticContext,
            out _,
            out _,
            "DesktopAutoExposureReadback");
        result = receipt.Result;
        if (!receipt.SubmissionAccepted)
        {
            _ = arenaLease.Arena.TryCancelFrameSlotSubmission(0, stagingSlice.Generation);
            diagnostic = $"vkQueueSubmit failed ({result}).";
            return false;
        }
        arenaLease.MarkSubmitted(stagingSlice);
        nativeResourcesMayBeReleased = false;
        try { _resourceRuntime.RecordSynchronousGraphicsSubmission(commandBuffer, fence, _deviceContext.GraphicsQueue, sourceImage, stagingSlice.Buffer); }
        catch (Exception ex)
        {
            Result recoveryWait;
            fixed (Fence* fencePtr = &fence)
                recoveryWait = api.WaitForFences(_deviceContext.Device, 1, fencePtr, true, ulong.MaxValue);
            _deviceContext.ObserveNativeResult("vkWaitForFences.DesktopAutoExposureReadbackReceiptRecovery", recoveryWait);
            nativeResourcesMayBeReleased = recoveryWait == Result.Success;
            if (nativeResourcesMayBeReleased)
            {
                _resourceRuntime.CompleteSynchronousFence(fence);
                if (!arenaLease.TryComplete(stagingSlice))
                {
                    _commandRuntime.RetireIncompleteSynchronousSubmission(
                        commandBuffer,
                        _commandRuntime.Pools.PrimaryGraphics,
                        fence,
                        arenaLease.Arena,
                        in stagingSlice,
                        removeOneTimeOwner: false,
                        "DesktopAutoExposureReadback",
                        completeSynchronousLifetime: true);
                    nativeResourcesMayBeReleased = false;
                }
            }
            else
            {
                _commandRuntime.RetireIncompleteSynchronousSubmission(
                    commandBuffer,
                    _commandRuntime.Pools.PrimaryGraphics,
                    fence,
                    arenaLease.Arena,
                    in stagingSlice,
                    removeOneTimeOwner: false,
                    "DesktopAutoExposureReadback",
                    completeSynchronousLifetime: true);
            }
            diagnostic = $"Desktop AutoExposureTex submission receipt failed after native acceptance: {ex.Message}";
            return false;
        }
        using VulkanCpuStageScope waitStage = new(_telemetry, EVulkanCpuStage.AuxiliaryFenceWait);
        fixed (Fence* fencePtr = &fence)
            result = api.WaitForFences(_deviceContext.Device, 1, fencePtr, true, ulong.MaxValue);
        _deviceContext.ObserveNativeResult("vkWaitForFences.DesktopAutoExposureReadback", result);
        if (result == Result.Success)
        {
            _resourceRuntime.CompleteSynchronousFence(fence);
            if (!arenaLease.TryComplete(stagingSlice))
            {
                _commandRuntime.RetireIncompleteSynchronousSubmission(
                    commandBuffer,
                    _commandRuntime.Pools.PrimaryGraphics,
                    fence,
                    arenaLease.Arena,
                    in stagingSlice,
                    removeOneTimeOwner: false,
                    "DesktopAutoExposureReadback",
                    completeSynchronousLifetime: true);
                diagnostic = "Desktop AutoExposureTex could not reopen its completed frame-data slot.";
                return false;
            }
            nativeResourcesMayBeReleased = true;
            return true;
        }
        _commandRuntime.RetireIncompleteSynchronousSubmission(
            commandBuffer,
            _commandRuntime.Pools.PrimaryGraphics,
            fence,
            arenaLease.Arena,
            in stagingSlice,
            removeOneTimeOwner: false,
            "DesktopAutoExposureReadback",
            completeSynchronousLifetime: true);
        diagnostic = $"vkWaitForFences failed ({result}).";
        return false;
    }

    private void DestroyTrackedExposureCommandBuffer(Vk api, ref CommandBuffer commandBuffer)
    {
        CommandPool pool = _commandRuntime.Pools.PrimaryGraphics;
        if (pool.Handle != 0)
        {
            fixed (CommandBuffer* commandBufferPtr = &commandBuffer)
                lock (_commandRuntime.Pools.Gate)
                    api.FreeCommandBuffers(_deviceContext.Device, pool, 1, commandBufferPtr);
        }
        _resourceRuntime.CompleteSynchronousCommandBuffer(commandBuffer);
        commandBuffer = default;
    }

}
