using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private bool SubmitAndWaitOpenXrCommandBuffer(
        CommandBuffer commandBuffer,
        out bool commandBufferCompleted,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        CommandBuffer* commandBuffers = stackalloc CommandBuffer[1];
        commandBuffers[0] = commandBuffer;
        return SubmitAndWaitOpenXrCommandBuffers(commandBuffers, 1, out commandBufferCompleted, diagnosticContext);
    }

    private bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer firstCommandBuffer,
        CommandBuffer secondCommandBuffer,
        out bool commandBuffersCompleted,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        CommandBuffer* commandBuffers = stackalloc CommandBuffer[2];
        commandBuffers[0] = firstCommandBuffer;
        commandBuffers[1] = secondCommandBuffer;
        return SubmitAndWaitOpenXrCommandBuffers(commandBuffers, 2, out commandBuffersCompleted, diagnosticContext);
    }

    private bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        out bool commandBufferCompleted,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
        => SubmitAndWaitOpenXrCommandBuffers(
            commandBuffers,
            commandBufferCount,
            out commandBufferCompleted,
            out _,
            out _,
            diagnosticContext);

    private bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        out bool commandBufferCompleted,
        out EVulkanQueueSubmissionDisposition submissionDisposition,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        DrainRetiredOpenXrSubmissionFences();
        commandBufferCompleted = false;
        submissionDisposition = EVulkanQueueSubmissionDisposition.NotSubmitted;
        injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.None;
        if (commandBuffers is null || commandBufferCount == 0)
            return false;
        if (!TryAdmitVulkanDeviceOperation("OpenXR.SubmitAndWait", out _))
            return false;

        FenceCreateInfo fenceCreateInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = 0,
        };

        ThrowIfVulkanDeviceOperationNotAdmitted("vkCreateFence.OpenXR");
        Result createFenceResult = Api!.CreateFence(_deviceContext.Device, ref fenceCreateInfo, null, out Fence fence);
        if (createFenceResult != Result.Success)
        {
            if (createFenceResult == Result.ErrorDeviceLost)
                MarkDeviceLost("OpenXR Vulkan submit fence creation returned ErrorDeviceLost", "vkCreateFence.OpenXR", createFenceResult);
            throw new InvalidOperationException("Failed to create OpenXR Vulkan submit fence.");
        }

        SetDebugObjectName(ObjectType.Fence, fence.Handle, "OpenXR.SubmitAndWaitFence");

        VulkanMappedFrameArena? mappedFrameArena = MappedFrameArena;
        ulong mappedFrameGeneration =
            mappedFrameArena?.Generation ?? 0UL;
        bool mappedFrameSlotsPrepared = false;
        bool nativeSubmitAccepted = false;
        try
        {
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = commandBufferCount,
                PCommandBuffers = commandBuffers,
            };

            VulkanSubmissionReceipt submitReceipt;
            bool mappedFramePreparationSucceeded;
            try
            {
                mappedFramePreparationSucceeded = mappedFrameArena is null ||
                    TryPrepareOpenXrMappedFrameSlotsForSubmission(
                        mappedFrameArena,
                        mappedFrameGeneration,
                        commandBuffers,
                        commandBufferCount);
            }
            catch
            {
                CompleteMappedFrameArenaDeviceLossObservation();
                throw;
            }
            if (!mappedFramePreparationSucceeded)
            {
                CompleteMappedFrameArenaDeviceLossObservation();
                Debug.VulkanWarning(
                    "[OpenXR] Mapped frame-data slots could not be flushed/sealed before queue submission.");
                return false;
            }
            mappedFrameSlotsPrepared = mappedFrameArena is not null;

            long submitStart = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.QueueSubmit"))
            {
                long queueLockWaitStart = Stopwatch.GetTimestamp();
                bool queueLockTaken = false;
                try
                {
                    Monitor.Enter(_oneTimeSubmitLock, ref queueLockTaken);
                    LogOpenXrSerializedCriticalSectionWait("QueueSubmit", queueLockWaitStart, Stopwatch.GetTimestamp());
                    submitReceipt = SubmitToQueueTrackedWithDisposition(
                        _deviceContext.GraphicsQueue,
                        ref submitInfo,
                        fence,
                        diagnosticContext,
                        out bool queueDispatchAttempted,
                        out injectedFailureStage);
                    if (submitReceipt.SubmissionAccepted)
                    {
                        nativeSubmitAccepted = true;
                        submissionDisposition =
                            EVulkanQueueSubmissionDisposition.SubmittedIncomplete;
                        if (mappedFrameArena is not null)
                            MarkOpenXrMappedFrameSlotsSubmitted(
                                mappedFrameArena,
                                mappedFrameGeneration,
                                commandBuffers,
                                commandBufferCount);
                    }
                    else if (queueDispatchAttempted)
                    {
                        submissionDisposition =
                            EVulkanQueueSubmissionDisposition.SubmittedIncomplete;
                    }
                }
                finally
                {
                    if (queueLockTaken)
                        Monitor.Exit(_oneTimeSubmitLock);
                }
            }
            long submitEnd = Stopwatch.GetTimestamp();

            if (submitReceipt.Result != Result.Success)
            {
                if (submitReceipt.Result == Result.ErrorDeviceLost)
                    MarkDeviceLost("OpenXR Vulkan eye submit returned ErrorDeviceLost", "vkQueueSubmit.OpenXR", submitReceipt.Result);

                Debug.VulkanWarning($"[OpenXR] Vulkan eye QueueSubmit failed: {submitReceipt.Result}");
                return false;
            }

            if (!submitReceipt.PostSubmissionPublicationSucceeded)
                Debug.VulkanWarning("[OpenXR] Vulkan eye submission accepted with deferred publication debt.");

            long waitStart = Stopwatch.GetTimestamp();
            Result waitResult;
            if (!TryAdmitVulkanDeviceOperation("vkWaitForFences.OpenXR", out _))
                return false;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.SubmitFenceWait"))
            using (VulkanCpuStageScope fenceWaitStage =
                new(_frameTelemetry, EVulkanCpuStage.AuxiliaryFenceWait))
            {
                waitResult = Api!.WaitForFences(
                    _deviceContext.Device,
                    1,
                    &fence,
                    true,
                    ulong.MaxValue);
            }
            long waitEnd = Stopwatch.GetTimestamp();
            if (waitResult != Result.Success)
            {
                if (waitResult == Result.ErrorDeviceLost)
                {
                    MarkDeviceLost("OpenXR Vulkan eye fence wait returned ErrorDeviceLost", "vkWaitForFences.OpenXR", waitResult);
                }

                Debug.VulkanWarning($"[OpenXR] Vulkan eye fence wait failed: {waitResult}");
                return false;
            }

            NotifyVulkanFenceCompleted(fence);
            submissionDisposition = EVulkanQueueSubmissionDisposition.Completed;

            if (mappedFrameArena is not null &&
                !TryCompleteOpenXrMappedFrameSlots(
                    mappedFrameArena,
                    mappedFrameGeneration,
                    commandBuffers,
                    commandBufferCount))
            {
                throw new InvalidOperationException(
                    "OpenXR fence completed, but mapped frame-data slots could not be reopened.");
            }

            if (OpenXrVulkanTraceEnabled)
            {
                double submitMs = (submitEnd - submitStart) * 1000.0 / Stopwatch.Frequency;
                double fenceWaitMs = (waitEnd - waitStart) * 1000.0 / Stopwatch.Frequency;
                Debug.Vulkan(
                    "[OpenXrVulkan] submitted commandBuffers={0} queueSubmitMs={1:F3} fenceWaitMs={2:F3}",
                    commandBufferCount,
                    submitMs,
                    fenceWaitMs);
            }

            commandBufferCompleted = true;
            return true;
        }
        finally
        {
            if (mappedFrameSlotsPrepared && !nativeSubmitAccepted &&
                mappedFrameArena is not null)
            {
                CancelOpenXrMappedFrameSlotsSubmission(
                    mappedFrameArena,
                    mappedFrameGeneration,
                    commandBuffers,
                    commandBufferCount);
            }
            if (fence.Handle != 0 && (!nativeSubmitAccepted || commandBufferCompleted))
                Api!.DestroyFence(_deviceContext.Device, fence, null);
            else if (fence.Handle != 0 && nativeSubmitAccepted && !_deviceLost)
                RetireOpenXrSubmissionFence(
                    fence,
                    mappedFrameArena,
                    mappedFrameGeneration,
                    commandBuffers,
                    commandBufferCount);
        }
    }

    /// <summary>
    /// Retains a fence whose accepted OpenXR submission has not yet proved
    /// completion, avoiding destruction while the queue may still reference it.
    /// </summary>
    private void RetireOpenXrSubmissionFence(
        Fence fence,
        VulkanMappedFrameArena? arena,
        ulong generation,
        CommandBuffer* commandBuffers,
        uint commandBufferCount)
    {
        // This is an exceptional, incomplete-submit path. Copying the slots
        // avoids retaining stack command-buffer memory while keeping normal
        // OpenXR submission allocation-free.
        uint[] frameSlots = new uint[commandBufferCount];
        int frameSlotCount = 0;
        for (uint index = 0; index < commandBufferCount; index++)
        {
            int frameSlot = ResolveCommandBufferImageIndex(commandBuffers[index]);
            if (frameSlot < 0 ||
                OpenXrMappedFrameSlotAppearedEarlier(commandBuffers, index, frameSlot))
            {
                continue;
            }

            frameSlots[frameSlotCount++] = checked((uint)frameSlot);
        }
        lock (_oneTimeSubmitLock)
            _outputRuntime._retiredOpenXrSubmissionFences.Add(
                new RetiredOpenXrSubmissionFence(fence, arena, generation, frameSlots, frameSlotCount));
    }

    private void DrainRetiredOpenXrSubmissionFences()
    {
        if (_deviceLost)
            return;

        lock (_oneTimeSubmitLock)
        {
            for (int index = _outputRuntime._retiredOpenXrSubmissionFences.Count - 1; index >= 0; index--)
            {
                RetiredOpenXrSubmissionFence retired = _outputRuntime._retiredOpenXrSubmissionFences[index];
                Fence fence = retired.Fence;
                Result result = Api!.GetFenceStatus(_deviceContext.Device, fence);
                if (result == Result.NotReady)
                    continue;
                if (result == Result.ErrorDeviceLost)
                {
                    MarkDeviceLost("OpenXR deferred submission fence reported device loss", "vkGetFenceStatus.OpenXR", result);
                    return;
                }
                if (result != Result.Success)
                {
                    Debug.VulkanWarning("[OpenXR] Deferred submission fence status failed: {0}", result);
                    continue;
                }

                NotifyVulkanFenceCompleted(fence);
                if (!TryCompleteRetiredOpenXrMappedFrameSlots(retired))
                    Debug.VulkanWarning("[OpenXR] Deferred submission fence completed, but its mapped frame slots could not be reopened.");
                Api.DestroyFence(_deviceContext.Device, fence, null);
                _outputRuntime._retiredOpenXrSubmissionFences.RemoveAt(index);
            }
        }
    }

    private static bool TryCompleteRetiredOpenXrMappedFrameSlots(
        RetiredOpenXrSubmissionFence retired)
    {
        if (retired.Arena is null)
            return true;

        for (int index = 0; index < retired.FrameSlotCount; index++)
        {
            if (!retired.Arena.TryResetFrameSlot(
                    retired.FrameSlots[index],
                    retired.Generation,
                    submissionCompletionProven: true))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryPrepareOpenXrMappedFrameSlotsForSubmission(
        VulkanMappedFrameArena arena,
        ulong generation,
        CommandBuffer* commandBuffers,
        uint commandBufferCount)
    {
        for (uint index = 0; index < commandBufferCount; index++)
        {
            int frameSlot = ResolveCommandBufferImageIndex(commandBuffers[index]);
            if (frameSlot < 0 ||
                OpenXrMappedFrameSlotAppearedEarlier(
                    commandBuffers,
                    index,
                    frameSlot))
            {
                continue;
            }

            if (!arena.TryPrepareFrameSlotForSubmission(
                    checked((uint)frameSlot),
                    generation))
            {
                CancelOpenXrMappedFrameSlotsSubmission(
                    arena,
                    generation,
                    commandBuffers,
                    index);
                return false;
            }
        }

        return true;
    }

    private void MarkOpenXrMappedFrameSlotsSubmitted(
        VulkanMappedFrameArena arena,
        ulong generation,
        CommandBuffer* commandBuffers,
        uint commandBufferCount)
    {
        for (uint index = 0; index < commandBufferCount; index++)
        {
            int frameSlot = ResolveCommandBufferImageIndex(commandBuffers[index]);
            if (frameSlot < 0 ||
                OpenXrMappedFrameSlotAppearedEarlier(
                    commandBuffers,
                    index,
                    frameSlot))
            {
                continue;
            }

            arena.MarkFrameSlotSubmitted(
                checked((uint)frameSlot),
                generation);
        }
    }

    private void CancelOpenXrMappedFrameSlotsSubmission(
        VulkanMappedFrameArena arena,
        ulong generation,
        CommandBuffer* commandBuffers,
        uint commandBufferCount)
    {
        for (uint index = 0; index < commandBufferCount; index++)
        {
            int frameSlot = ResolveCommandBufferImageIndex(commandBuffers[index]);
            if (frameSlot < 0 ||
                OpenXrMappedFrameSlotAppearedEarlier(
                    commandBuffers,
                    index,
                    frameSlot))
            {
                continue;
            }

            _ = arena.TryCancelFrameSlotSubmission(
                checked((uint)frameSlot),
                generation);
        }
    }

    private bool TryCompleteOpenXrMappedFrameSlots(
        VulkanMappedFrameArena arena,
        ulong generation,
        CommandBuffer* commandBuffers,
        uint commandBufferCount)
    {
        for (uint index = 0; index < commandBufferCount; index++)
        {
            int frameSlot = ResolveCommandBufferImageIndex(commandBuffers[index]);
            if (frameSlot < 0 ||
                OpenXrMappedFrameSlotAppearedEarlier(
                    commandBuffers,
                    index,
                    frameSlot))
            {
                continue;
            }

            if (!arena.TryResetFrameSlot(
                    checked((uint)frameSlot),
                    generation,
                    submissionCompletionProven: true))
            {
                return false;
            }
        }

        return true;
    }

    private bool OpenXrMappedFrameSlotAppearedEarlier(
        CommandBuffer* commandBuffers,
        uint currentIndex,
        int frameSlot)
    {
        for (uint previousIndex = 0;
             previousIndex < currentIndex;
             previousIndex++)
        {
            if (ResolveCommandBufferImageIndex(
                    commandBuffers[previousIndex]) == frameSlot)
            {
                return true;
            }
        }

        return false;
    }

    private static void LogOpenXrSerializedCriticalSectionWait(string sectionName, long waitStart, long waitEnd)
    {
        double waitMs = (waitEnd - waitStart) * 1000.0 / Stopwatch.Frequency;
        if (waitMs < 0.25)
            return;

        Debug.VulkanEvery(
            $"OpenXR.Vulkan.SerializedCriticalSection.{sectionName}",
            TimeSpan.FromSeconds(1),
            "[OpenXrVulkan] serialized critical section={0} waitMs={1:F3}",
            sectionName,
            waitMs);
    }
}
