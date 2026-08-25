using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private readonly List<GpuRenderStatsReadbackRequest> _pendingGpuRenderStatsReadbacks = [];
    // Diagnostics-only bookkeeping. Receipt objects are intentionally unique per
    // frame so a cached primary can never masquerade as the current snapshot.
    private readonly Dictionary<XRDataBuffer, GpuDiagnosticSnapshotReceipt> _gpuDiagnosticSnapshotReceipts =
        new(ReferenceEqualityComparer.Instance);
    private ulong _pendingGpuRenderStatsReadbackFrameId;
    private ulong _gpuDiagnosticSnapshotReceiptFrameId;
    private ulong _gpuDiagnosticSnapshotDiscardGeneration;

    internal ulong GpuDiagnosticSnapshotDiscardGeneration
        => _gpuDiagnosticSnapshotDiscardGeneration;

    private static bool IndirectTraceEnabled
        => XREnvironment.IsEnabled(
            XREngineEnvironmentVariables.VulkanIndirectTrace);

    internal void PollGpuRenderStatsReadbacks()
    {
        if (!RuntimeEngine.IsRenderThread)
        {
            RuntimeEngine.EnqueueMainThreadTask(
                PollGpuRenderStatsReadbacks,
                "VulkanRenderer.PollGpuRenderStatsReadbacks",
                RenderThreadJobKind.Readback);
            return;
        }

        GpuRenderStatsReadbackSlot?[] slots = OutputRuntime.Capture.GpuStatsReadbackSlots;
        for (int i = 0; i < slots.Length; ++i)
        {
            GpuRenderStatsReadbackSlot? slot = slots[i];
            if (slot is not null && slot.Active)
                TryConsumeGpuRenderStatsReadback(slot);
        }
    }

    internal bool QueueGpuRenderDrawCountReadback(
        XRDataBuffer drawCountBuffer,
        uint countByteOffset = 0,
        uint countElementCount = 1)
        => QueueGpuRenderStatsReadback(
            drawCountBuffer,
            countByteOffset,
            checked(countElementCount * (uint)sizeof(uint)),
            countElementCount,
            GpuRenderStatsReadbackKind.DrawCountBuffer,
            publishDraws: true,
            publishTriangles: false);

    internal bool QueueGpuRenderStatsBufferReadback(
        XRDataBuffer statsBuffer,
        bool publishDraws,
        bool publishTriangles)
    {
        if (!publishDraws && !publishTriangles)
            return false;

        return QueueGpuRenderStatsReadback(
            statsBuffer,
            0u,
            checked(GpuStatsLayout.FieldCount * (uint)sizeof(uint)),
            GpuStatsLayout.FieldCount,
            GpuRenderStatsReadbackKind.StatsBuffer,
            publishDraws,
            publishTriangles);
    }

    /// <summary>
    /// Captures the mesh-task indirect command only after its submission fence
    /// signals. This is intentionally diagnostics-only; normal meshlet
    /// submission remains fully GPU-resident and does not map or wait.
    /// </summary>
    internal bool QueueGpuMeshletDispatchDiagnosticsReadback(XRDataBuffer dispatchIndirectBuffer)
        => QueueGpuRenderStatsReadback(
            dispatchIndirectBuffer,
            0u,
            checked(GPUMeshletLayout.MeshTaskIndirectDiagnosticsUIntCount * (uint)sizeof(uint)),
            GPUMeshletLayout.MeshTaskIndirectDiagnosticsUIntCount,
            GpuRenderStatsReadbackKind.MeshletDispatchIndirectBuffer,
            publishDraws: false,
            publishTriangles: false);

    private bool QueueGpuRenderStatsReadback(
        XRDataBuffer sourceBuffer,
        uint sourceByteOffset,
        uint byteCount,
        uint elementCount,
        GpuRenderStatsReadbackKind kind,
        bool publishDraws,
        bool publishTriangles)
    {
        if (_deviceLost || !RuntimeEngine.Rendering.Stats.EnableTracking || byteCount == 0u || elementCount == 0u)
            return false;

        if (!RuntimeEngine.IsRenderThread)
        {
            RuntimeEngine.EnqueueMainThreadTask(
                () => QueueGpuRenderStatsReadback(
                    sourceBuffer,
                    sourceByteOffset,
                    byteCount,
                    elementCount,
                    kind,
                    publishDraws,
                    publishTriangles),
                "VulkanRenderer.QueueGpuRenderStatsReadback",
                RenderThreadJobKind.Readback);
            return false;
        }

        ulong requestedEnd = (ulong)sourceByteOffset + byteCount;
        if (requestedEnd > sourceBuffer.Length)
            return false;

        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_pendingGpuRenderStatsReadbackFrameId != frameId)
        {
            _pendingGpuRenderStatsReadbacks.Clear();
            _pendingGpuRenderStatsReadbackFrameId = frameId;
        }

        GpuDiagnosticSnapshotReceipt? diagnosticReceipt = null;
        if (_gpuDiagnosticSnapshotReceiptFrameId == frameId)
            _gpuDiagnosticSnapshotReceipts.TryGetValue(sourceBuffer, out diagnosticReceipt);

        var request = new GpuRenderStatsReadbackRequest(
            frameId,
            sourceBuffer,
            sourceByteOffset,
            byteCount,
            elementCount,
            kind,
            publishDraws,
            publishTriangles,
            diagnosticReceipt);
        if (!_pendingGpuRenderStatsReadbacks.Contains(request))
            _pendingGpuRenderStatsReadbacks.Add(request);
        return true;
    }

    /// <summary>
    /// Submits diagnostics copies only after the frame that produced their source
    /// buffers has been accepted by the graphics queue. Same-queue ordering keeps
    /// the copies behind the meshlet expansion and dispatch commands without a CPU
    /// wait, while coalescing prevents repeated pass visits from flooding the queue.
    /// </summary>
    internal void FlushPendingGpuRenderStatsReadbacks()
    {
        if (_pendingGpuRenderStatsReadbacks.Count == 0)
            return;

        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_pendingGpuRenderStatsReadbackFrameId != frameId)
        {
            _pendingGpuRenderStatsReadbacks.Clear();
            return;
        }

        try
        {
            for (int i = 0; i < _pendingGpuRenderStatsReadbacks.Count; i++)
            {
                GpuRenderStatsReadbackRequest request = _pendingGpuRenderStatsReadbacks[i];
                if (request.DiagnosticReceipt is { IsRecorded: false })
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.GpuStatsReadback.UnrecordedSnapshot.{request.DiagnosticReceipt.Sequence}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Dropped diagnostics readback because snapshot receipt {0} was not recorded into the accepted command buffer.",
                        request.DiagnosticReceipt.Sequence);
                    continue;
                }
                _ = SubmitGpuRenderStatsReadback(
                    request.SourceBuffer,
                    request.FrameId,
                    request.SourceByteOffset,
                    request.ByteCount,
                    request.ElementCount,
                    request.Kind,
                    request.PublishDraws,
                    request.PublishTriangles);
            }
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.GpuStatsReadback.Flush.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Deferred GPU statistics readback submission failed: {0}",
                ex.Message);
        }
        finally
        {
            _pendingGpuRenderStatsReadbacks.Clear();
        }
    }

    /// <summary>
    /// Drops diagnostics that were recorded for a submission attempt which was
    /// not accepted. Requests must never leak into another attempt that happens
    /// to share the same engine render-frame identifier.
    /// </summary>
    internal void DiscardPendingGpuRenderStatsReadbacks()
    {
        bool discardedQueuedReadback = _pendingGpuRenderStatsReadbacks.Count > 0;
        _pendingGpuRenderStatsReadbacks.Clear();
        _pendingGpuRenderStatsReadbackFrameId = 0UL;
        _gpuDiagnosticSnapshotReceipts.Clear();
        _gpuDiagnosticSnapshotReceiptFrameId = 0UL;
        if (discardedQueuedReadback)
            _gpuDiagnosticSnapshotDiscardGeneration++;
    }

    private GpuDiagnosticSnapshotReceipt GetOrCreateGpuDiagnosticSnapshotReceipt(
        XRDataBuffer destination)
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_gpuDiagnosticSnapshotReceiptFrameId != frameId)
        {
            _gpuDiagnosticSnapshotReceipts.Clear();
            _gpuDiagnosticSnapshotReceiptFrameId = frameId;
        }

        if (!_gpuDiagnosticSnapshotReceipts.TryGetValue(destination, out GpuDiagnosticSnapshotReceipt? receipt))
        {
            receipt = new GpuDiagnosticSnapshotReceipt(frameId);
            _gpuDiagnosticSnapshotReceipts.Add(destination, receipt);
        }

        return receipt;
    }

    private unsafe bool SubmitGpuRenderStatsReadback(
        XRDataBuffer sourceBuffer,
        ulong sourceFrameId,
        uint sourceByteOffset,
        uint byteCount,
        uint elementCount,
        GpuRenderStatsReadbackKind kind,
        bool publishDraws,
        bool publishTriangles)
    {
        if (_deviceLost || !RuntimeEngine.Rendering.Stats.EnableTracking || byteCount == 0u || elementCount == 0u)
            return false;

        PollGpuRenderStatsReadbacks();

        if (GenericToAPI<VkDataBuffer>(sourceBuffer) is not { } sourceVkBuffer ||
            !sourceVkBuffer.TryEnsureReadyForRendering(allowSynchronousUpload: false) ||
            sourceVkBuffer.BufferHandle is not { } sourceHandle ||
            sourceHandle.Handle == 0 ||
            !sourceVkBuffer.LastUsageFlags.HasFlag(BufferUsageFlags.TransferSrcBit))
        {
            return false;
        }

        GpuRenderStatsReadbackSlot? slot = AcquireGpuRenderStatsReadbackSlot();
        if (slot is null ||
            !EnsureGpuRenderStatsReadbackResources(slot) ||
            !ReadbackOutputResources.TryAcquireGpuStatsSlice(
                slot.ArenaSlot,
                byteCount,
                out slot.DataSlice))
            return false;

        bool arenaSubmissionAccepted = false;
        try
        {
            Result resetFenceResult = Api!.ResetFences(_deviceContext.Device, 1, in slot.Fence);
            Result resetCommandResult = _commandRuntime.ResetTrackedCommandBuffer(slot.CommandBuffer);
            if (resetFenceResult != Result.Success || resetCommandResult != Result.Success)
            {
                ReadbackOutputResources.CancelGpuStatsSliceSubmission(slot.DataSlice);
                slot.DataSlice = default;
                return false;
            }

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            _deviceContext.ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.GpuStatsReadback");
            if (_commandRuntime.BeginTrackedCommandBuffer(
                    slot.CommandBuffer,
                    ref beginInfo,
                    "GpuStatsReadback") != Result.Success)
            {
                ReadbackOutputResources.CancelGpuStatsSliceSubmission(slot.DataSlice);
                slot.DataSlice = default;
                return false;
            }

            BufferMemoryBarrier sourceBarrier = new()
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit | AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = sourceHandle,
                Offset = sourceByteOffset,
                Size = byteCount,
            };
            _commandRuntime.CmdPipelineBarrierTracked(
                slot.CommandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                1,
                &sourceBarrier,
                0,
                null);

            BufferCopy copy = new()
            {
                SrcOffset = sourceByteOffset,
                DstOffset = slot.DataSlice.Offset,
                Size = byteCount,
            };
            _commandRuntime.CmdCopyBufferTracked(
                slot.CommandBuffer,
                sourceHandle,
                slot.DataSlice.Buffer,
                1,
                ref copy);

            if (_commandRuntime.EndCommandBufferTracked(slot.CommandBuffer) != Result.Success ||
                !ReadbackOutputResources.TryPrepareGpuStatsSlice(slot.DataSlice))
            {
                ReadbackOutputResources.CancelGpuStatsSliceSubmission(slot.DataSlice);
                slot.DataSlice = default;
                return false;
            }

            CommandBuffer readbackCommandBuffer = slot.CommandBuffer;
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &readbackCommandBuffer,
            };

            VulkanSubmissionDiagnosticContext diagnosticContext = new()
            {
                SubmissionKind = "GpuStatsReadback",
                FrameId = sourceFrameId,
                FrameSlot = slot.ArenaSlot,
                CommandBufferCount = 1,
                FirstCommandBufferHandle = unchecked((ulong)readbackCommandBuffer.Handle),
                FenceHandle = unchecked((ulong)slot.Fence.Handle),
                QueueKind = "Graphics",
            };
            VulkanSubmissionReceipt submitReceipt = _commandRuntime.SubmitToQueueTrackedWithDisposition(
                _deviceContext.GraphicsQueue,
                ref submitInfo,
                slot.Fence,
                in diagnosticContext,
                out _,
                out _,
                "GpuStatsReadback");
            Result submitResult = submitReceipt.Result;

            if (!submitReceipt.SubmissionAccepted)
            {
                ReadbackOutputResources.CancelGpuStatsSliceSubmission(slot.DataSlice);
                slot.DataSlice = default;
                if (submitResult == Result.ErrorDeviceLost)
                    MarkDeviceLost(
                        "GPU statistics readback submit returned ErrorDeviceLost",
                        "vkQueueSubmit.GpuStatsReadback",
                        submitResult);
                return false;
            }

            ReadbackOutputResources.MarkGpuStatsSliceSubmitted(slot.DataSlice);
            arenaSubmissionAccepted = true;

            slot.ByteCount = byteCount;
            slot.ElementCount = elementCount;
            slot.Kind = kind;
            slot.PublishDraws = publishDraws;
            slot.PublishTriangles = publishTriangles;
            slot.SourceName = sourceBuffer.AttributeName ?? sourceBuffer.Target.ToString();
            slot.SourceHandle = sourceHandle.Handle;
            slot.SourceFrameId = sourceFrameId;
            slot.Active = true;
            return true;
        }
        finally
        {
            if (!arenaSubmissionAccepted && slot.DataSlice.IsValid)
            {
                ReadbackOutputResources.CancelGpuStatsSliceSubmission(slot.DataSlice);
                slot.DataSlice = default;
            }
        }
    }

    private GpuRenderStatsReadbackSlot? AcquireGpuRenderStatsReadbackSlot()
    {
        GpuRenderStatsReadbackSlot?[] slots = OutputRuntime.Capture.GpuStatsReadbackSlots;
        for (int i = 0; i < slots.Length; ++i)
        {
            int index = (OutputRuntime.Capture.GpuStatsReadbackCursor + i) % slots.Length;
            GpuRenderStatsReadbackSlot slot = slots[index] ??= new GpuRenderStatsReadbackSlot();
            if (slot.Active && !TryConsumeGpuRenderStatsReadback(slot))
                continue;

            OutputRuntime.Capture.GpuStatsReadbackCursor = (index + 1) % slots.Length;
            slot.ArenaSlot = index;
            return slot;
        }

        return null;
    }

    private bool EnsureGpuRenderStatsReadbackResources(GpuRenderStatsReadbackSlot slot)
    {
        if (slot.CommandBuffer.Handle == 0)
        {
            slot.CommandPool = GetThreadCommandPool();
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = slot.CommandPool,
                CommandBufferCount = 1,
            };
            if (_commandRuntime.AllocateCommandBufferWithLifetime(
                    ref allocateInfo,
                    out slot.CommandBuffer,
                    "GpuStatsReadback") != Result.Success)
            {
                return false;
            }
        }

        if (slot.Fence.Handle == 0)
        {
            if (ReadbackOutputResources.EnsureFence(
                    ref slot.Fence,
                    "GpuStatsReadback") != Result.Success)
                return false;

            _deviceContext.SetDebugObjectName(ObjectType.Fence, slot.Fence.Handle, "GpuStatsReadback.Fence");
        }

        return true;
    }

    private unsafe bool TryConsumeGpuRenderStatsReadback(GpuRenderStatsReadbackSlot slot)
    {
        if (!slot.Active)
            return true;

        Result fenceResult = Api!.GetFenceStatus(_deviceContext.Device, slot.Fence);
        if (fenceResult is Result.NotReady or Result.Timeout)
            return false;
        if (fenceResult != Result.Success)
        {
            if (fenceResult == Result.ErrorDeviceLost)
                MarkDeviceLost(
                    "GPU statistics readback fence status returned ErrorDeviceLost",
                    "vkGetFenceStatus.GpuStatsReadback",
                    fenceResult);
            return false;
        }

        _commandRuntime.CompleteTrackedFence(slot.Fence);

        if (!ReadbackOutputResources.TryCompleteGpuStatsSlice(slot.DataSlice) ||
            !ReadbackOutputResources.TryBeginGpuStatsRead(
                slot.DataSlice,
                out VulkanFrameDataReadScope readScope))
        {
            return false;
        }

        uint[] rented = ArrayPool<uint>.Shared.Rent(checked((int)slot.ElementCount));
        Span<uint> values = rented.AsSpan(0, checked((int)slot.ElementCount));

        try
        {
            using (readScope)
                MemoryMarshal.Cast<byte, uint>(readScope.Bytes).Slice(
                    0,
                    checked((int)slot.ElementCount)).CopyTo(values);

            PublishGpuRenderStatsReadback(slot, values);
            RuntimeEngine.Rendering.Stats.GpuDriven.RecordDelayedDiagnosticReadback(slot.ByteCount);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(rented);

            slot.Active = false;
            slot.ByteCount = 0u;
            slot.ElementCount = 0u;
            slot.PublishDraws = false;
            slot.PublishTriangles = false;
            slot.SourceFrameId = 0UL;
            slot.DataSlice = default;
        }

        return true;
    }

    private void PublishGpuRenderStatsReadback(
        GpuRenderStatsReadbackSlot slot,
        ReadOnlySpan<uint> values)
    {
        switch (slot.Kind)
        {
            case GpuRenderStatsReadbackKind.DrawCountBuffer:
                {
                    ulong drawCount = 0ul;
                    for (int i = 0; i < values.Length; ++i)
                        drawCount += values[i];

                    if (slot.PublishDraws && drawCount > 0ul)
                        RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(VulkanGpuStatsReadbackTelemetry.SaturateToInt(drawCount));
                    RuntimeEngine.Rendering.Stats.GpuDriven.RecordCommandCompaction(
                        culledCommands: 0,
                        delayedDrawCountValue: drawCount > long.MaxValue
                            ? long.MaxValue
                            : (long)drawCount);

                    if (IndirectTraceEnabled)
                    {
                        Debug.Vulkan("[VulkanIndirect] delayed draw counts source={0} elements={1} sum={2}", slot.SourceName, values.Length, drawCount);
                        WriteGpuRenderStatsTraceIfChanged(slot.SourceName, slot.SourceHandle, "draw-counts", values);
                    }
                    break;
                }
            case GpuRenderStatsReadbackKind.StatsBuffer:
                {
                    uint draws = values.Length > (int)GpuStatsLayout.StatsDrawCount
                        ? values[(int)GpuStatsLayout.StatsDrawCount]
                        : 0u;
                    uint triangles = values.Length > (int)GpuStatsLayout.StatsTriangleCount
                        ? values[(int)GpuStatsLayout.StatsTriangleCount]
                        : 0u;

                    if (slot.PublishDraws && draws > 0u)
                        RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(VulkanGpuStatsReadbackTelemetry.SaturateToInt(draws));
                    if (slot.PublishTriangles && triangles > 0u)
                        RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered(VulkanGpuStatsReadbackTelemetry.SaturateToInt(triangles));

                    if (values.Length > (int)GpuStatsLayout.MeshletTaskRecordsHiZCulled)
                    {
                        RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletTaskStats(
                            values[(int)GpuStatsLayout.MeshletTaskRecordsEmitted],
                            values[(int)GpuStatsLayout.MeshletTaskRecordsFrustumCulled],
                            values[(int)GpuStatsLayout.MeshletTaskRecordsConeCulled],
                            values[(int)GpuStatsLayout.MeshletTaskRecordsHiZCulled]);
                    }

                    if (IndirectTraceEnabled && values.Length > (int)GpuStatsLayout.StatsRejectedDistance)
                    {
                        Debug.Vulkan(
                            "[VulkanIndirect] delayed stats input={0} culled={1} draws={2} triangles={3} frustumRejected={4} distanceRejected={5}",
                            values[(int)GpuStatsLayout.StatsInputCount],
                            values[(int)GpuStatsLayout.StatsCulledCount],
                            draws,
                            triangles,
                            values[(int)GpuStatsLayout.StatsRejectedFrustum],
                            values[(int)GpuStatsLayout.StatsRejectedDistance]);
                        WriteGpuRenderStatsTraceIfChanged(slot.SourceName, slot.SourceHandle, "stats", values);
                    }
                    break;
                }
            case GpuRenderStatsReadbackKind.MeshletDispatchIndirectBuffer:
                {
                    // The snapshot stores VkDrawMeshTasksIndirectCommandEXT's
                    // groupCountX/Y/Z followed by the separate draw-count word.
                    // Only the complete, executable command is production proof.
                    uint dispatchX = values.Length > 0 ? values[0] : 0u;
                    uint dispatchY = values.Length > 1 ? values[1] : 0u;
                    uint dispatchZ = values.Length > 2 ? values[2] : 0u;
                    uint drawCount = values.Length > 3 ? values[3] : 0u;
                    bool executable = drawCount == 1u && dispatchX > 0u && dispatchY == 1u && dispatchZ == 1u;
                    RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletDelayedDiagnostics(
                        executable ? dispatchX : 0u,
                        slot.ByteCount);
                    if ((dispatchX > 0u || drawCount > 0u) && !executable)
                    {
                        Debug.VulkanWarningEvery(
                            $"Vulkan.MeshletDiagnostics.InvalidCommand.{slot.SourceHandle:X}",
                            TimeSpan.FromSeconds(2),
                            "[Vulkan] Rejected incomplete mesh-task diagnostics command X={0} Y={1} Z={2} drawCount={3}.",
                            dispatchX,
                            dispatchY,
                            dispatchZ,
                            drawCount);
                    }
                    if (IndirectTraceEnabled)
                        WriteGpuRenderStatsTraceIfChanged(slot.SourceName, slot.SourceHandle, "meshlet-dispatch", values);
                    break;
                }
        }
    }

    private void WriteGpuRenderStatsTraceIfChanged(
        string sourceName,
        ulong sourceHandle,
        string kind,
        ReadOnlySpan<uint> values)
    {
        ulong hash = 1469598103934665603ul;
        for (int i = 0; i < values.Length; ++i)
        {
            hash ^= values[i];
            hash *= 1099511628211ul;
        }

        string key = $"{kind}:{sourceName}:0x{sourceHandle:X}";
        if (_frameTelemetry._gpuRenderStatsTraceHashes.TryGetValue(key, out ulong previousHash) && previousHash == hash)
            return;

        _frameTelemetry._gpuRenderStatsTraceHashes[key] = hash;

        StringBuilder line = new(128 + values.Length * 12);
        line.Append(DateTime.UtcNow.ToString("O"));
        line.Append(" kind=").Append(kind);
        line.Append(" source=").Append(sourceName);
        line.Append(" handle=0x").Append(sourceHandle.ToString("X"));
        line.Append(" values=[");
        for (int i = 0; i < values.Length; ++i)
        {
            if (i > 0)
                line.Append(',');
            line.Append(values[i]);
        }
        line.AppendLine("]");

        try
        {
            string logDirectory = Path.Combine(Environment.CurrentDirectory, "Build", "Logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, "vulkan-indirect-delayed-readback.log"), line.ToString());
        }
        catch
        {
            // Diagnostic logging must never affect rendering.
        }
    }


    internal void DisposeGpuRenderStatsReadbacks()
    {
        _pendingGpuRenderStatsReadbacks.Clear();
        _pendingGpuRenderStatsReadbackFrameId = 0UL;
        _gpuDiagnosticSnapshotReceipts.Clear();
        _gpuDiagnosticSnapshotReceiptFrameId = 0UL;

        GpuRenderStatsReadbackSlot?[] slots = OutputRuntime.Capture.GpuStatsReadbackSlots;
        for (int i = 0; i < slots.Length; ++i)
        {
            GpuRenderStatsReadbackSlot? slot = slots[i];
            if (slot is null)
                continue;

            ReadbackOutputResources.DestroyFence(ref slot.Fence);
            if (slot.CommandBuffer.Handle != 0)
            {
                CommandBuffer commandBuffer = slot.CommandBuffer;
                _commandRuntime.FreeCommandBufferWithLifetime(CurrentFrameSlot, slot.CommandPool, ref commandBuffer, "GpuStatsReadback.Dispose");
                _commandRuntime.RemoveCommandBufferBindState(slot.CommandBuffer);
            }
            slot.Active = false;
            slot.Fence = default;
            slot.CommandBuffer = default;
            slot.DataSlice = default;
        }

        _frameTelemetry._gpuRenderStatsTraceHashes.Clear();
    }

    private readonly record struct GpuRenderStatsReadbackRequest(
        ulong FrameId,
        XRDataBuffer SourceBuffer,
        uint SourceByteOffset,
        uint ByteCount,
        uint ElementCount,
        GpuRenderStatsReadbackKind Kind,
        bool PublishDraws,
        bool PublishTriangles,
        GpuDiagnosticSnapshotReceipt? DiagnosticReceipt);
}

