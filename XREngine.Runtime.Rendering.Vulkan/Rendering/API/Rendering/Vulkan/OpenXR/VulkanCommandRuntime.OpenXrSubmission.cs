using System.Diagnostics;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns synchronous OpenXR queue submission, mapped-frame settlement, and
/// exceptional incomplete-submit fence retirement without retaining output
/// authority state.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    internal bool SubmitAndWaitOpenXrCommandBuffer(CommandBuffer commandBuffer, out bool completed, VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        VulkanOpenXrSubmissionResult result = SubmitAndWaitOpenXr(new(commandBuffer, default, 1, diagnosticContext));
        completed = result.CommandBuffersCompleted;
        return result.Succeeded;
    }

    internal bool SubmitAndWaitOpenXrCommandBuffers(CommandBuffer first, CommandBuffer second, out bool completed, VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        VulkanOpenXrSubmissionResult result = SubmitAndWaitOpenXr(new(first, second, 2, diagnosticContext));
        completed = result.CommandBuffersCompleted;
        return result.Succeeded;
    }

    internal unsafe bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        out bool completed,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
        => SubmitAndWaitOpenXrCommandBuffers(
            commandBuffers,
            commandBufferCount,
            out completed,
            out _,
            out _,
            diagnosticContext);

    internal unsafe bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        out bool completed,
        out EVulkanQueueSubmissionDisposition submissionDisposition,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        completed = false;
        submissionDisposition = EVulkanQueueSubmissionDisposition.NotSubmitted;
        injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.None;
        if (commandBuffers is null || commandBufferCount is 0 or > 2)
            return false;

        VulkanOpenXrSubmissionResult result = SubmitAndWaitOpenXr(new(
            commandBuffers[0],
            commandBufferCount == 2 ? commandBuffers[1] : default,
            commandBufferCount,
            diagnosticContext));
        completed = result.CommandBuffersCompleted;
        submissionDisposition = result.SubmissionDisposition;
        injectedFailureStage = result.InjectedFailureStage;
        return result.Succeeded;
    }

    private readonly List<RetiredOpenXrSubmissionFence> _retiredOpenXrSubmissionFences = new(2);

    internal unsafe VulkanOpenXrSubmissionResult SubmitAndWaitOpenXr(
        in VulkanOpenXrSubmissionInput input)
    {
        DrainRetiredOpenXrSubmissionFences();
        VulkanSubmissionReceipt submitReceipt =
            VulkanSubmissionReceipt.Rejected(Result.ErrorValidationFailedExt);
        EVulkanQueueSubmissionDisposition submissionDisposition =
            EVulkanQueueSubmissionDisposition.NotSubmitted;
        EOpenXrStrictSpsFaultInjectionStage injectedFailureStage =
            EOpenXrStrictSpsFaultInjectionStage.None;
        if (!input.IsValid)
        {
            return new VulkanOpenXrSubmissionResult(
                false,
                false,
                submissionDisposition,
                injectedFailureStage,
                submitReceipt);
        }
        if (!DeviceContext.IsOperational)
        {
            submitReceipt = VulkanSubmissionReceipt.Rejected(
                Result.ErrorDeviceLost);
            return new VulkanOpenXrSubmissionResult(
                false,
                false,
                submissionDisposition,
                injectedFailureStage,
                submitReceipt);
        }

        CommandBuffer* commandBuffers = stackalloc CommandBuffer[2];
        commandBuffers[0] = input.FirstCommandBuffer;
        commandBuffers[1] = input.SecondCommandBuffer;
        FenceCreateInfo fenceCreateInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
        };
        Result createFenceResult = Api.CreateFence(
            DeviceContext.Device,
            ref fenceCreateInfo,
            null,
            out Fence fence);
        DeviceContext.ObserveNativeResult(
            "vkCreateFence.OpenXR",
            createFenceResult);
        if (createFenceResult != Result.Success)
            throw new InvalidOperationException(
                $"Failed to create OpenXR Vulkan submit fence: {createFenceResult}.");

        VulkanMappedFrameArena? mappedFrameArena = MappedFrameArena;
        ulong mappedFrameGeneration = mappedFrameArena?.Generation ?? 0UL;
        VulkanFrameDataArena? frameDataArena = ResourceRuntime.FrameDataArena;
        ulong frameDataGeneration = frameDataArena?.Generation ?? 0UL;
        uint frameDataSlot = checked((uint)ResourceRuntime.Buffers.CurrentFrameSlot);
        bool mappedFrameSlotsPrepared = false;
        bool frameDataSlotPrepared = false;
        bool nativeSubmitAccepted = false;
        bool commandBuffersCompleted = false;
        bool arenaSlotsReopened = false;
        try
        {
            NameOpenXrSubmissionFence(fence);
            if (mappedFrameArena is not null &&
                !TryPrepareOpenXrMappedFrameSlotsForSubmission(
                    mappedFrameArena,
                    mappedFrameGeneration,
                    commandBuffers,
                    input.CommandBufferCount))
            {
                Debug.VulkanWarning(
                    "[OpenXR] Mapped frame-data slots could not be flushed/sealed before queue submission.");
                return new VulkanOpenXrSubmissionResult(
                    false,
                    false,
                    submissionDisposition,
                    injectedFailureStage,
                    submitReceipt);
            }
            mappedFrameSlotsPrepared = mappedFrameArena is not null;
            if (frameDataArena is not null &&
                !frameDataArena.TryPrepareFrameSlotForSubmission(
                    frameDataSlot,
                    frameDataGeneration))
            {
                Debug.VulkanWarning(
                    "[OpenXR] Canonical frame-data slot could not be flushed/sealed before queue submission.");
                return new VulkanOpenXrSubmissionResult(
                    false,
                    false,
                    submissionDisposition,
                    injectedFailureStage,
                    submitReceipt);
            }
            frameDataSlotPrepared = frameDataArena is not null;

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = input.CommandBufferCount,
                PCommandBuffers = commandBuffers,
            };
            VulkanSubmissionDiagnosticContext diagnosticContext =
                input.DiagnosticContext;
            long submitStart = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "OpenXR.Vulkan.QueueSubmit"))
            {
                submitReceipt = SubmitToQueueTrackedWithDisposition(
                    DeviceContext.GraphicsQueue,
                    ref submitInfo,
                    fence,
                    in diagnosticContext,
                    out bool queueDispatchAttempted,
                    out injectedFailureStage,
                    "OpenXR.SubmitAndWait");
                if (submitReceipt.SubmissionAccepted)
                {
                    nativeSubmitAccepted = true;
                    submissionDisposition =
                        EVulkanQueueSubmissionDisposition.SubmittedIncomplete;
                    if (mappedFrameArena is not null)
                    {
                        MarkOpenXrMappedFrameSlotsSubmitted(
                            mappedFrameArena,
                            mappedFrameGeneration,
                            commandBuffers,
                            input.CommandBufferCount);
                    }
                    frameDataArena?.MarkFrameSlotSubmitted(
                        frameDataSlot,
                        frameDataGeneration);
                }
                else if (queueDispatchAttempted)
                {
                    submissionDisposition =
                        EVulkanQueueSubmissionDisposition.SubmittedIncomplete;
                }
            }
            long submitEnd = Stopwatch.GetTimestamp();

            if (submitReceipt.Result != Result.Success)
            {
                Debug.VulkanWarning(
                    "[OpenXR] Vulkan eye QueueSubmit failed: {0}",
                    submitReceipt.Result);
                return new VulkanOpenXrSubmissionResult(
                    false,
                    false,
                    submissionDisposition,
                    injectedFailureStage,
                    submitReceipt);
            }

            if (!submitReceipt.PostSubmissionPublicationSucceeded)
            {
                Debug.VulkanWarning(
                    "[OpenXR] Vulkan eye submission accepted with deferred publication debt.");
            }

            if (!DeviceContext.IsOperational)
            {
                return new VulkanOpenXrSubmissionResult(
                    false,
                    false,
                    submissionDisposition,
                    injectedFailureStage,
                    submitReceipt);
            }

            long waitStart = Stopwatch.GetTimestamp();
            Result waitResult;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "OpenXR.Vulkan.SubmitFenceWait"))
            using (VulkanCpuStageScope fenceWaitStage =
                   new(FrameTelemetry, EVulkanCpuStage.AuxiliaryFenceWait))
            {
                waitResult = Api.WaitForFences(
                    DeviceContext.Device,
                    1,
                    &fence,
                    true,
                    ulong.MaxValue);
            }
            long waitEnd = Stopwatch.GetTimestamp();
            DeviceContext.ObserveNativeResult(
                "vkWaitForFences.OpenXR",
                waitResult);
            if (waitResult != Result.Success)
            {
                Debug.VulkanWarning(
                    "[OpenXR] Vulkan eye fence wait failed: {0}",
                    waitResult);
                return new VulkanOpenXrSubmissionResult(
                    false,
                    false,
                    submissionDisposition,
                    injectedFailureStage,
                    submitReceipt);
            }

            CompleteTrackedFence(fence);
            submissionDisposition = EVulkanQueueSubmissionDisposition.Completed;
            commandBuffersCompleted = true;
            if (mappedFrameArena is not null &&
                !TryCompleteOpenXrMappedFrameSlots(
                    mappedFrameArena,
                    mappedFrameGeneration,
                    commandBuffers,
                    input.CommandBufferCount))
            {
                throw new InvalidOperationException(
                    "OpenXR fence completed, but mapped frame-data slots could not be reopened.");
            }
            if (frameDataArena is not null &&
                !frameDataArena.TryResetFrameSlot(
                    frameDataSlot,
                    frameDataGeneration,
                    submissionCompletionProven: true))
            {
                throw new InvalidOperationException(
                    "OpenXR fence completed, but the canonical frame-data slot could not be reopened.");
            }
            arenaSlotsReopened = true;

            if (IsOpenXrTraceEnabled)
            {
                double submitMs = Stopwatch.GetElapsedTime(
                    submitStart,
                    submitEnd).TotalMilliseconds;
                double fenceWaitMs = Stopwatch.GetElapsedTime(
                    waitStart,
                    waitEnd).TotalMilliseconds;
                Debug.Vulkan(
                    "[OpenXrVulkan] submitted commandBuffers={0} queueSubmitMs={1:F3} fenceWaitMs={2:F3}",
                    input.CommandBufferCount,
                    submitMs,
                    fenceWaitMs);
            }

            return new VulkanOpenXrSubmissionResult(
                true,
                true,
                submissionDisposition,
                injectedFailureStage,
                submitReceipt);
        }
        finally
        {
            if (mappedFrameSlotsPrepared &&
                !nativeSubmitAccepted &&
                mappedFrameArena is not null)
            {
                CancelOpenXrMappedFrameSlotsSubmission(
                    mappedFrameArena,
                    mappedFrameGeneration,
                    commandBuffers,
                    input.CommandBufferCount);
            }
            if (frameDataSlotPrepared &&
                !nativeSubmitAccepted &&
                frameDataArena is not null)
            {
                _ = frameDataArena.TryCancelFrameSlotSubmission(
                    frameDataSlot,
                    frameDataGeneration);
            }

            if (fence.Handle != 0 &&
                (!nativeSubmitAccepted ||
                 commandBuffersCompleted && arenaSlotsReopened))
            {
                Api.DestroyFence(DeviceContext.Device, fence, null);
            }
            else if (fence.Handle != 0 &&
                     nativeSubmitAccepted &&
                     DeviceContext.IsOperational)
            {
                RetireOpenXrSubmissionFence(
                    fence,
                    mappedFrameArena,
                    mappedFrameGeneration,
                    commandBuffers,
                    input.CommandBufferCount,
                    frameDataArena,
                    frameDataGeneration,
                    frameDataSlot,
                    commandBuffersCompleted);
            }
        }
    }

    private unsafe void NameOpenXrSubmissionFence(Fence fence)
    {
        if (fence.Handle == 0 ||
            DeviceContext.DebugUtils is null ||
            !FrameTelemetry._diagnosticOptions.EnableDebugUtils)
        {
            return;
        }

        ReadOnlySpan<byte> name = "OpenXR.SubmitAndWaitFence\0"u8;
        fixed (byte* namePointer = name)
        {
            DebugUtilsObjectNameInfoEXT nameInfo = new()
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = ObjectType.Fence,
                ObjectHandle = fence.Handle,
                PObjectName = namePointer,
            };
            _ = DeviceContext.DebugUtils.SetDebugUtilsObjectName(
                DeviceContext.Device,
                in nameInfo);
        }
    }

    private unsafe void RetireOpenXrSubmissionFence(
        Fence fence,
        VulkanMappedFrameArena? arena,
        ulong generation,
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        VulkanFrameDataArena? frameDataArena,
        ulong frameDataGeneration,
        uint frameDataSlot,
        bool completionProven)
    {
        // This allocation is confined to the exceptional accepted-but-not-yet-
        // completed path; it prevents retaining the caller's stack buffer.
        uint[] frameSlots = new uint[commandBufferCount];
        int frameSlotCount = 0;
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

            frameSlots[frameSlotCount++] = checked((uint)frameSlot);
        }

        lock (CommandBuffers.OneTimeSubmitGate)
        {
            _retiredOpenXrSubmissionFences.Add(
                new RetiredOpenXrSubmissionFence(
                    fence,
                    completionProven,
                    arena,
                    generation,
                    frameSlots,
                    frameSlotCount,
                    frameDataArena,
                    frameDataGeneration,
                    frameDataSlot));
        }
    }

    private unsafe void DrainRetiredOpenXrSubmissionFences()
    {
        if (!DeviceContext.IsOperational)
            return;

        lock (CommandBuffers.OneTimeSubmitGate)
        {
            for (int index = _retiredOpenXrSubmissionFences.Count - 1;
                 index >= 0;
                 index--)
            {
                RetiredOpenXrSubmissionFence retired =
                    _retiredOpenXrSubmissionFences[index];
                if (!retired.CompletionProven)
                {
                    Fence pendingFence = retired.Fence;
                    Result result = Api.GetFenceStatus(
                        DeviceContext.Device,
                        pendingFence);
                    DeviceContext.ObserveNativeResult(
                        "vkGetFenceStatus.OpenXR",
                        result);
                    if (result == Result.NotReady)
                        continue;
                    if (result != Result.Success)
                    {
                        Debug.VulkanWarning(
                            "[OpenXR] Deferred submission fence status failed: {0}",
                            result);
                        continue;
                    }

                    CompleteTrackedFence(pendingFence);
                    Api.DestroyFence(DeviceContext.Device, pendingFence, null);
                    retired = retired with
                    {
                        Fence = default,
                        CompletionProven = true,
                    };
                    _retiredOpenXrSubmissionFences[index] = retired;
                }

                if (!TryCompleteRetiredOpenXrMappedFrameSlots(retired))
                {
                    Debug.VulkanWarning(
                        "[OpenXR] Deferred submission fence completed, but its mapped frame slots could not be reopened.");
                    continue;
                }
                if (retired.Fence.Handle != 0)
                    Api.DestroyFence(DeviceContext.Device, retired.Fence, null);
                _retiredOpenXrSubmissionFences.RemoveAt(index);
            }
        }
    }

    private static bool TryCompleteRetiredOpenXrMappedFrameSlots(
        RetiredOpenXrSubmissionFence retired)
    {
        if (retired.Arena is not null)
        {
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
        }

        if (retired.FrameDataArena is not null &&
            !retired.FrameDataArena.TryResetFrameSlot(
                retired.FrameDataSlot,
                retired.FrameDataGeneration,
                submissionCompletionProven: true))
        {
            return false;
        }

        return true;
    }

    private unsafe bool TryPrepareOpenXrMappedFrameSlotsForSubmission(
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

            if (arena.TryPrepareFrameSlotForSubmission(
                    checked((uint)frameSlot),
                    generation))
            {
                continue;
            }

            CancelOpenXrMappedFrameSlotsSubmission(
                arena,
                generation,
                commandBuffers,
                index);
            return false;
        }

        return true;
    }

    private unsafe void MarkOpenXrMappedFrameSlotsSubmitted(
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

    private unsafe void CancelOpenXrMappedFrameSlotsSubmission(
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

    private unsafe bool TryCompleteOpenXrMappedFrameSlots(
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

    private unsafe bool OpenXrMappedFrameSlotAppearedEarlier(
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
}
