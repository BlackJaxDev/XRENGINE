using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Execution;
using XREngine.Rendering.Diagnostics;
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
    // Created only after an instrumented diagnostic request is accepted. The
    // sidecar owns the fixed host-visible arena access path, never the frame's
    // producer resources or strategy selection.
    private VulkanGpuDiagnosticReadbackSidecar? _gpuDiagnosticReadbackSidecar;

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

        _gpuDiagnosticReadbackSidecar?.PollPrimaryCompleted(
            value => HasTimelineValueCompleted(
                _commandRuntime.Synchronization._graphicsTimelineSemaphore,
                value),
            ConsumePrimaryGpuDiagnosticReadback);

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
        => QueueGpuRenderStatsReadback(
            sourceBuffer,
            sourceByteOffset,
            byteCount,
            elementCount,
            kind,
            publishDraws,
            publishTriangles,
            RuntimeEngine.Rendering.LastResolvedMeshSubmissionStrategy);

    private bool QueueGpuRenderStatsReadback(
        XRDataBuffer sourceBuffer,
        uint sourceByteOffset,
        uint byteCount,
        uint elementCount,
        GpuRenderStatsReadbackKind kind,
        bool publishDraws,
        bool publishTriangles,
        EMeshSubmissionStrategy capturedStrategy)
    {
        if (_deviceLost || !RuntimeEngine.Rendering.Stats.EnableTracking ||
            byteCount == 0u || elementCount == 0u)
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
                    publishTriangles,
                    capturedStrategy),
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

        EVulkanGpuDiagnosticReadbackPurpose purpose = ResolveGpuDiagnosticReadbackPurpose(
            capturedStrategy,
            kind,
            diagnosticReceipt);
        if (!IsGpuDiagnosticReadbackAllowed(capturedStrategy, purpose))
            return false;

        var request = new GpuRenderStatsReadbackRequest(
            frameId,
            sourceBuffer,
            sourceByteOffset,
            byteCount,
            elementCount,
            kind,
            publishDraws,
            publishTriangles,
            capturedStrategy,
            purpose,
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
                    request.PublishTriangles,
                    request.CapturedStrategy,
                    request.Purpose);
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

    private VulkanGpuDiagnosticReadbackSidecar? GetOrCreateGpuDiagnosticReadbackSidecar()
    {
        if (_gpuDiagnosticReadbackSidecar is not null)
            return _gpuDiagnosticReadbackSidecar;

        int capacity = OutputRuntime.Capture.GpuStatsReadbackSlots.Length;
        if (capacity == 0)
            return null;

        return _gpuDiagnosticReadbackSidecar = new VulkanGpuDiagnosticReadbackSidecar(
            ReadbackOutputResources,
            capacity);
    }

    /// <summary>
    /// Records one bounded set-1 diagnostic copy into the producer primary.
    /// The resulting staging slice is deliberately not submitted here: the
    /// desktop frame's accepted graphics timeline is its sole completion
    /// authority.
    /// </summary>
    internal unsafe bool TryRecordAdvancedVisibilityDiagnosticCopy(
        CommandBuffer commandBuffer,
        in VulkanAdvancedVisibilityResourceState visibilityState,
        in GpuDiagnosticReadbackPlanNode node,
        ulong frameIdentity)
    {
        if (_deviceLost || !node.IsInstrumentedPass || node.ByteCount == 0u ||
            commandBuffer.Handle == 0)
            return false;

        VulkanFrameDataSlice source = node.Decoder switch
        {
            EGpuDiagnosticReadbackDecoder.IndirectDrawCount => visibilityState.RangeCounts,
            EGpuDiagnosticReadbackDecoder.MeshletVisibility => visibilityState.MeshArguments,
            EGpuDiagnosticReadbackDecoder.SubmissionValidation => visibilityState.Counters,
            _ => default,
        };
        if (!source.IsValid ||
            (ulong)node.SourceByteOffset + node.ByteCount > source.Length)
            return false;

        VulkanGpuDiagnosticReadbackSidecar? sidecar =
            GetOrCreateGpuDiagnosticReadbackSidecar();
        VulkanGpuDiagnosticReadbackReservation reservation = default;
        if (sidecar is null || !sidecar.TryReserveNext(
                in node,
                frameIdentity,
                EVulkanGpuDiagnosticReadbackPurpose.Instrumented,
                out reservation) ||
            !sidecar.TryAcquireStagingSlice(
                reservation.SlotIndex, node.ByteCount, out VulkanFrameDataSlice destination))
        {
            if (reservation != default)
                sidecar?.Cancel(in reservation);
            return false;
        }

        bool attached = false;
        try
        {
            BufferMemoryBarrier sourceBarrier = new()
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit |
                    AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = source.Buffer,
                Offset = source.Offset + node.SourceByteOffset,
                Size = node.ByteCount,
            };
            _commandRuntime.CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 1, &sourceBarrier, 0, null);

            BufferCopy copy = new()
            {
                SrcOffset = source.Offset + node.SourceByteOffset,
                DstOffset = destination.Offset,
                Size = node.ByteCount,
            };
            _commandRuntime.CmdCopyBufferTracked(
                commandBuffer, source.Buffer, destination.Buffer, 1, ref copy);
            attached = sidecar.TryAttachPrimaryCopy(
                in reservation, commandBuffer, in destination);
            return attached;
        }
        finally
        {
            if (!attached)
            {
                sidecar.CancelStagingSliceSubmission(destination);
                sidecar.Cancel(in reservation);
            }
        }
    }

    private void ConsumePrimaryGpuDiagnosticReadback(
        in VulkanGpuDiagnosticReadbackReservation reservation,
        in VulkanFrameDataSlice slice,
        ulong frameIdentity,
        in GpuDiagnosticReadbackPlanNode node)
    {
        VulkanGpuDiagnosticReadbackSidecar? sidecar = _gpuDiagnosticReadbackSidecar;
        if (sidecar is null || !sidecar.TryCompleteStagingSlice(slice) ||
            !sidecar.TryBeginStagingRead(slice, out VulkanFrameDataReadScope readScope))
            return;

        try
        {
            if ((node.ByteCount & 3u) != 0u)
                return;

            RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
            RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(node.ByteCount);
            using (readScope)
            {
                uint[] words = MemoryMarshal.Cast<byte, uint>(readScope.Bytes)
                    .Slice(0, checked((int)(node.ByteCount / sizeof(uint))))
                    .ToArray();
                CompletedDiagnosticPayload payload = CompletedDiagnosticPayload.Create(
                    words,
                    frameIdentity,
                    (uint)node.Decoder,
                    DecodeAdvancedVisibilityDiagnostic);
                RuntimeRenderingHostServices.Work.ScheduleCompletedDiagnosticDecode(payload);
            }
            RuntimeEngine.Rendering.Stats.GpuDriven.RecordDelayedDiagnosticReadback(node.ByteCount);
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.AdvancedVisibility.DiagnosticDecode.{node.PassIdentity}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Delayed advanced-visibility diagnostic decode failed: {0}",
                ex.Message);
        }
    }

    /// <summary>
    /// Interprets already-completed set-1 diagnostics on the general worker
    /// domain. This callback observes CPU array data only and publishes
    /// asynchronous telemetry; it cannot affect the sealed render strategy.
    /// </summary>
    private static void DecodeAdvancedVisibilityDiagnostic(
        CompletedDiagnosticPayload payload)
    {
        ReadOnlySpan<uint> words = payload.Words.AsSpan();
        EGpuDiagnosticReadbackDecoder decoder =
            (EGpuDiagnosticReadbackDecoder)payload.DecoderId;
        switch (decoder)
        {
            case EGpuDiagnosticReadbackDecoder.IndirectDrawCount:
                if (!words.IsEmpty)
                    RuntimeEngine.Rendering.Stats.GpuDriven.RecordCommandCompaction(
                        culledCommands: 0,
                        delayedDrawCountValue: words[0]);
                break;
            case EGpuDiagnosticReadbackDecoder.MeshletVisibility:
            {
                ulong tasks = 0u;
                for (int index = 0; index + 2 < words.Length; index += 3)
                {
                    ulong groups = (ulong)words[index] * words[index + 1] *
                        words[index + 2];
                    tasks = ulong.MaxValue - tasks < groups
                        ? ulong.MaxValue
                        : tasks + groups;
                }
                RuntimeEngine.Rendering.Stats.GpuDriven.RecordCommandCompaction(
                    culledCommands: 0,
                    delayedDrawCountValue: tasks > long.MaxValue
                        ? long.MaxValue
                        : checked((long)tasks));
                break;
            }
            case EGpuDiagnosticReadbackDecoder.SubmissionValidation:
                if (words.Length < 16)
                    return;

                long payloadOverflow = words[6];
                long decodeOutOfBounds = words[8];
                long unsupportedDisplacement = words[10];
                RuntimeEngine.Rendering.Stats.GpuDriven.RecordCommandCompaction(
                    culledCommands: 0,
                    gpuCompactionOverflow: payloadOverflow,
                    activeListOverflow: decodeOutOfBounds,
                    meshletOverflow: unsupportedDisplacement);
                if (payloadOverflow != 0 || decodeOutOfBounds != 0 ||
                    unsupportedDisplacement != 0)
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.AdvancedVisibility.AsyncDiagnostic",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Advanced visibility diagnostic frame={0} payloadOverflow={1} decodeOutOfBounds={2} unsupportedDisplacement={3}.",
                        payload.SourceFrameId,
                        payloadOverflow,
                        decodeOutOfBounds,
                        unsupportedDisplacement);
                }
                break;
        }
    }

    private unsafe bool SubmitGpuRenderStatsReadback(
        XRDataBuffer sourceBuffer,
        ulong sourceFrameId,
        uint sourceByteOffset,
        uint byteCount,
        uint elementCount,
        GpuRenderStatsReadbackKind kind,
        bool publishDraws,
        bool publishTriangles,
        EMeshSubmissionStrategy capturedStrategy,
        EVulkanGpuDiagnosticReadbackPurpose purpose)
    {
        if (_deviceLost || !RuntimeEngine.Rendering.Stats.EnableTracking ||
            !IsGpuDiagnosticReadbackAllowed(capturedStrategy, purpose) ||
            byteCount == 0u || elementCount == 0u)
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
        VulkanGpuDiagnosticReadbackSidecar? sidecar = GetOrCreateGpuDiagnosticReadbackSidecar();
        if (slot is null ||
            !EnsureGpuRenderStatsReadbackResources(slot) ||
            sidecar is null ||
            !sidecar.TryAcquireStagingSlice(
                slot.ArenaSlot,
                byteCount,
                out slot.DataSlice))
            return false;

        bool arenaSubmissionAccepted = false;
        GpuDiagnosticReadbackPlanNode planNode = new(
            PassIdentity: sourceHandle.Handle,
            ViewId: 0u,
            SourceByteOffset: sourceByteOffset,
            ByteCount: byteCount,
            Strategy: capturedStrategy,
            Decoder: MapGpuStatsReadbackDecoder(kind));
        if (!sidecar.TryReserve(
                planNode,
                sourceFrameId,
                slot.ArenaSlot,
                purpose,
                out slot.Reservation))
            return false;
        slot.PlanNode = planNode;
        slot.Purpose = purpose;
        try
        {
            Result resetFenceResult = Api!.ResetFences(_deviceContext.Device, 1, in slot.Fence);
            Result resetCommandResult = _commandRuntime.ResetTrackedCommandBuffer(slot.CommandBuffer);
            if (resetFenceResult != Result.Success || resetCommandResult != Result.Success)
            {
                sidecar.CancelStagingSliceSubmission(slot.DataSlice);
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
                sidecar.CancelStagingSliceSubmission(slot.DataSlice);
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
                !sidecar.TryPrepareStagingSlice(slot.DataSlice))
            {
                sidecar.CancelStagingSliceSubmission(slot.DataSlice);
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
                sidecar.CancelStagingSliceSubmission(slot.DataSlice);
                slot.DataSlice = default;
                if (submitResult == Result.ErrorDeviceLost)
                    MarkDeviceLost(
                        "GPU statistics readback submit returned ErrorDeviceLost",
                        "vkQueueSubmit.GpuStatsReadback",
                        submitResult);
                return false;
            }

            sidecar.MarkStagingSliceSubmitted(slot.DataSlice);
            arenaSubmissionAccepted = true;
            if (!sidecar.TryMarkSubmitted(slot.Reservation))
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.GpuDiagnosticReadback.SidecarSubmissionState",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Diagnostic staging copy was submitted but its sidecar state could not transition.");
            }

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
                sidecar.CancelStagingSliceSubmission(slot.DataSlice);
                slot.DataSlice = default;
            }
            if (!arenaSubmissionAccepted)
            {
                sidecar.Cancel(slot.Reservation);
                slot.Reservation = default;
                slot.PlanNode = default;
                slot.Purpose = default;
            }
        }
    }

    private static EVulkanGpuDiagnosticReadbackPurpose ResolveGpuDiagnosticReadbackPurpose(
        EMeshSubmissionStrategy capturedStrategy,
        GpuRenderStatsReadbackKind kind,
        GpuDiagnosticSnapshotReceipt? diagnosticReceipt)
    {
        bool meshletEvidence =
            capturedStrategy == EMeshSubmissionStrategy.GpuMeshletZeroReadback &&
            VulkanFeatureProfile.IsActive &&
            VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics &&
            diagnosticReceipt is not null &&
            kind is GpuRenderStatsReadbackKind.StatsBuffer or
                GpuRenderStatsReadbackKind.MeshletDispatchIndirectBuffer;
        return meshletEvidence
            ? EVulkanGpuDiagnosticReadbackPurpose.MeshletZeroReadbackEvidence
            : EVulkanGpuDiagnosticReadbackPurpose.Instrumented;
    }

    private static bool IsGpuDiagnosticReadbackAllowed(
        EMeshSubmissionStrategy strategy,
        EVulkanGpuDiagnosticReadbackPurpose purpose)
        => purpose switch
        {
            EVulkanGpuDiagnosticReadbackPurpose.Instrumented =>
                GpuDiagnosticReadbackPlan.IsInstrumented(strategy),
            EVulkanGpuDiagnosticReadbackPurpose.MeshletZeroReadbackEvidence =>
                strategy == EMeshSubmissionStrategy.GpuMeshletZeroReadback &&
                VulkanFeatureProfile.IsActive &&
                VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics,
            _ => false,
        };

    private static EGpuDiagnosticReadbackDecoder MapGpuStatsReadbackDecoder(
        GpuRenderStatsReadbackKind kind)
        => kind switch
        {
            GpuRenderStatsReadbackKind.DrawCountBuffer => EGpuDiagnosticReadbackDecoder.IndirectDrawCount,
            GpuRenderStatsReadbackKind.MeshletDispatchIndirectBuffer => EGpuDiagnosticReadbackDecoder.MeshletVisibility,
            GpuRenderStatsReadbackKind.StatsBuffer => EGpuDiagnosticReadbackDecoder.SubmissionValidation,
            _ => EGpuDiagnosticReadbackDecoder.None,
        };

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

        VulkanGpuDiagnosticReadbackSidecar? sidecar = _gpuDiagnosticReadbackSidecar;
        if (sidecar is null ||
            !sidecar.TryCompleteStagingSlice(slot.DataSlice) ||
            !sidecar.TryBeginStagingRead(
                slot.DataSlice,
                out VulkanFrameDataReadScope readScope))
        {
            return false;
        }

        uint[] rented = ArrayPool<uint>.Shared.Rent(checked((int)slot.ElementCount));
        Span<uint> values = rented.AsSpan(0, checked((int)slot.ElementCount));

        try
        {
            // This is the sole host-observation point for the delayed sidecar.
            // Dedicated meshlet evidence is accounted separately so validating
            // a production zero-readback lane does not pollute its generic
            // mapped-buffer or readback-byte invariants.
            if (slot.Purpose != EVulkanGpuDiagnosticReadbackPurpose.MeshletZeroReadbackEvidence)
            {
                RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
                RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(
                    slot.ByteCount);
            }
            using (readScope)
                MemoryMarshal.Cast<byte, uint>(readScope.Bytes).Slice(
                    0,
                    checked((int)slot.ElementCount)).CopyTo(values);

            PublishGpuRenderStatsReadback(slot, values);
            ScheduleCompletedGpuDiagnosticDecode(values, slot.SourceFrameId);
            _ = sidecar.TryComplete(slot.Reservation);
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
            slot.PlanNode = default;
            slot.Purpose = default;
            slot.Reservation = default;
        }

        return true;
    }

    private static void ScheduleCompletedGpuDiagnosticDecode(
        ReadOnlySpan<uint> words,
        ulong sourceFrameId)
    {
        try
        {
            // The copy completes before this allocation. The general-domain
            // payload deliberately cannot carry a fence or GPU callback.
            uint[] completedWords = words.ToArray();
            CompletedDiagnosticPayload payload = CompletedDiagnosticPayload.Create(
                completedWords,
                sourceFrameId);
            RuntimeRenderingHostServices.Work.ScheduleCompletedDiagnosticDecode(payload);
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                "Vulkan.GpuDiagnosticReadback.DecodeSchedule",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Delayed diagnostic decode scheduling failed: {0}",
                ex.Message);
        }
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
        EMeshSubmissionStrategy CapturedStrategy,
        EVulkanGpuDiagnosticReadbackPurpose Purpose,
        GpuDiagnosticSnapshotReceipt? DiagnosticReceipt);
}

