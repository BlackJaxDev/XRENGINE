using System.Diagnostics;
using Silk.NET.Vulkan;
using VulkanSemaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns serialized OpenXR queue submission, timeline-based mapped-frame
/// settlement, and exceptional incomplete-submit retirement without retaining
/// output authority state.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    internal bool SubmitAndWaitOpenXrCommandBuffer(CommandBuffer commandBuffer, out bool completed, VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        VulkanOpenXrSubmissionResult result = SubmitAndWaitOpenXr(new(commandBuffer, default, default, 1, diagnosticContext));
        completed = result.CommandBuffersCompleted;
        return result.Succeeded;
    }

    internal bool SubmitAndWaitOpenXrCommandBuffers(CommandBuffer first, CommandBuffer second, out bool completed, VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        VulkanOpenXrSubmissionResult result = SubmitAndWaitOpenXr(new(first, second, default, 2, diagnosticContext));
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
        if (commandBuffers is null || commandBufferCount is 0 or > 3)
            return false;

        VulkanOpenXrSubmissionResult result = SubmitAndWaitOpenXr(new(
            commandBuffers[0],
            commandBufferCount >= 2 ? commandBuffers[1] : default,
            commandBufferCount == 3 ? commandBuffers[2] : default,
            commandBufferCount,
            diagnosticContext));
        completed = result.CommandBuffersCompleted;
        submissionDisposition = result.SubmissionDisposition;
        injectedFailureStage = result.InjectedFailureStage;
        return result.Succeeded;
    }

    private readonly List<RetiredOpenXrSubmissionTimeline> _retiredOpenXrSubmissions = new(2);
    private readonly object _retiredOpenXrSubmissionsGate = new();
    private OpenXrVulkanSubmissionTracker? _openXrSubmissionTracker;

    internal OpenXrVulkanSubmissionTracker OpenXrSubmissionTracker =>
        _openXrSubmissionTracker ??= new OpenXrVulkanSubmissionTracker(this);

    internal ulong CurrentTimelineValue => Synchronization._graphicsTimelineValue;

    internal static bool IsOpenXrAsyncSubmitEnabled
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanAsyncSubmit);
            if (env is not null)
                return !string.Equals(env, "0", StringComparison.OrdinalIgnoreCase) && !string.Equals(env, "false", StringComparison.OrdinalIgnoreCase);
            return true;
        }
    }

    internal unsafe VulkanOpenXrSubmissionResult SubmitAndWaitOpenXr(
        in VulkanOpenXrSubmissionInput input)
    {
        DrainRetiredOpenXrSubmissions();
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

        CommandBuffer* commandBuffers = stackalloc CommandBuffer[3];
        commandBuffers[0] = input.FirstCommandBuffer;
        commandBuffers[1] = input.SecondCommandBuffer;
        commandBuffers[2] = input.ThirdCommandBuffer;
        VulkanSemaphore timelineSemaphore = Synchronization._graphicsTimelineSemaphore;
        ulong timelineValue = 0UL;
        if (timelineSemaphore.Handle == 0)
            throw new InvalidOperationException(
                "OpenXR Vulkan submission requires the graphics completion timeline.");

        VulkanMappedFrameArena? mappedFrameArena = MappedFrameArena;
        ulong mappedFrameGeneration = mappedFrameArena?.Generation ?? 0UL;
        VulkanFrameDataArena? frameDataArena = ResourceRuntime.FrameDataArena;
        ulong frameDataGeneration = frameDataArena?.Generation ?? 0UL;
        bool mappedFrameSlotsPrepared = false;
        bool frameDataSlotsPrepared = false;
        bool nativeSubmitAccepted = false;
        bool commandBuffersCompleted = false;
        bool arenaSlotsReopened = false;
        try
        {
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
                !TryPrepareOpenXrFrameDataSlotsForSubmission(
                    frameDataArena,
                    frameDataGeneration,
                    commandBuffers,
                    input.CommandBufferCount))
            {
                Debug.VulkanWarning(
                    "[OpenXR] Canonical frame-data slots could not be flushed/sealed before queue submission.");
                return new VulkanOpenXrSubmissionResult(
                    false,
                    false,
                    submissionDisposition,
                    injectedFailureStage,
                    submitReceipt);
            }
            frameDataSlotsPrepared = frameDataArena is not null;

            TimelineSemaphoreSubmitInfo timelineInfo = new()
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                SignalSemaphoreValueCount = 1,
                PSignalSemaphoreValues = &timelineValue,
            };
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineInfo,
                CommandBufferCount = input.CommandBufferCount,
                PCommandBuffers = commandBuffers,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &timelineSemaphore,
            };
            VulkanSubmissionDiagnosticContext diagnosticContext =
                input.DiagnosticContext;
            long submitStart = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "OpenXR.Vulkan.QueueSubmit"))
            {
                submitReceipt = SubmitToGraphicsTimelineTrackedWithDisposition(
                    DeviceContext.GraphicsQueue,
                    ref submitInfo,
                    default,
                    timelineSemaphore,
                    minimumTimelineValue: 1UL,
                    in diagnosticContext,
                    out timelineValue,
                    out bool queueDispatchAttempted,
                    out injectedFailureStage,
                    "OpenXR.SubmitAndWait",
                    input.AdmissionTicket is { } admissionTicket
                        ? new OpenXrVulkanSubmissionTracker.AcceptedSubmissionSink(
                            OpenXrSubmissionTracker,
                            admissionTicket,
                            timelineSemaphore,
                            submitStart)
                        : null);
                submitReceipt = submitReceipt with
                {
                    CompletionSemaphore = timelineSemaphore,
                    CompletionValue = timelineValue,
                };
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
                    if (frameDataArena is not null)
                    {
                        MarkOpenXrFrameDataSlotsSubmitted(
                            frameDataArena,
                            frameDataGeneration,
                            commandBuffers,
                            input.CommandBufferCount);
                    }
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

            TimeSpan submitElapsed = Stopwatch.GetElapsedTime(submitStart, submitEnd);

            if (IsOpenXrAsyncSubmitEnabled && !input.ForceSynchronousCompletion)
            {
                submissionDisposition = EVulkanQueueSubmissionDisposition.SubmittedIncomplete;
                commandBuffersCompleted = false;
                arenaSlotsReopened = true;

                if (IsOpenXrTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] Async submitted commandBuffers={0} queueSubmitMs={1:F3} timelineValue={2}",
                        input.CommandBufferCount,
                        submitElapsed.TotalMilliseconds,
                        timelineValue);
                }

                return new VulkanOpenXrSubmissionResult(
                    true,
                    false,
                    submissionDisposition,
                    injectedFailureStage,
                    submitReceipt);
            }

            long waitStart = Stopwatch.GetTimestamp();
            Result waitResult;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "OpenXR.Vulkan.SubmitTimelineWait"))
            using (VulkanCpuStageScope fenceWaitStage =
                   new(FrameTelemetry, EVulkanCpuStage.AuxiliaryFenceWait))
            {
                waitResult = Synchronization.WaitForTimelineCompletion(
                    Api,
                    DeviceContext,
                    ResourceRuntime.Lifetime.Tracker,
                    timelineSemaphore,
                    timelineValue,
                    ulong.MaxValue);
            }
            long waitEnd = Stopwatch.GetTimestamp();
            TimeSpan completionWaitElapsed = Stopwatch.GetElapsedTime(waitStart, waitEnd);
            RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeCompletionWaitTime(completionWaitElapsed);
            RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeFenceForcedWait();
            if (waitResult != Result.Success)
            {
                Debug.VulkanWarning(
                    "[OpenXR] Vulkan eye timeline completion failed. Result={0} TimelineValue={1}.",
                    waitResult,
                    timelineValue);
                return new VulkanOpenXrSubmissionResult(
                    false,
                    false,
                    submissionDisposition,
                    injectedFailureStage,
                    submitReceipt);
            }

            CompleteTrackedTimeline(timelineSemaphore, timelineValue);
            submissionDisposition = EVulkanQueueSubmissionDisposition.Completed;
            commandBuffersCompleted = true;
            if (input.AdmissionTicket is not null)
            {
                // The admission tracker owns every accepted submission, including
                // the synchronous path. Retire through that one owner so arena
                // slots, uploads, command buffers, and prepared leases settle once.
                OpenXrSubmissionTracker.PollCompletions();
                arenaSlotsReopened = true;
                return new VulkanOpenXrSubmissionResult(
                    true,
                    true,
                    submissionDisposition,
                    injectedFailureStage,
                    submitReceipt);
            }
            if (mappedFrameArena is not null &&
                !TryCompleteOpenXrMappedFrameSlots(
                    mappedFrameArena,
                    mappedFrameGeneration,
                    commandBuffers,
                    input.CommandBufferCount))
            {
                throw new InvalidOperationException(
                    "OpenXR timeline completed, but mapped frame-data slots could not be reopened.");
            }
            if (frameDataArena is not null &&
                !TryCompleteOpenXrFrameDataSlots(
                    frameDataArena,
                    frameDataGeneration,
                    commandBuffers,
                    input.CommandBufferCount))
            {
                throw new InvalidOperationException(
                    "OpenXR timeline completed, but the canonical frame-data slots could not be reopened.");
            }
            arenaSlotsReopened = true;

            if (IsOpenXrTraceEnabled)
            {
                double submitMs = Stopwatch.GetElapsedTime(
                    submitStart,
                    submitEnd).TotalMilliseconds;
                double completionWaitMs = Stopwatch.GetElapsedTime(
                    waitStart,
                    waitEnd).TotalMilliseconds;
                Debug.Vulkan(
                    "[OpenXrVulkan] submitted commandBuffers={0} queueSubmitMs={1:F3} timelineWaitMs={2:F3} timelineValue={3}",
                    input.CommandBufferCount,
                    submitMs,
                    completionWaitMs,
                    timelineValue);
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
            // The common tracked gateway commits an OpenXR ticket in the same
            // serialized transaction as successful vkQueueSubmit. An exception
            // before this method observes its receipt must therefore not cancel
            // the already-submitted arena slots.
            nativeSubmitAccepted |= input.AdmissionTicket is { } admissionTicket &&
                !admissionTicket.Active;
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
            if (frameDataSlotsPrepared &&
                !nativeSubmitAccepted &&
                frameDataArena is not null)
            {
                CancelOpenXrFrameDataSlotsSubmission(
                    frameDataArena,
                    frameDataGeneration,
                    commandBuffers,
                    input.CommandBufferCount);
            }

            if (nativeSubmitAccepted &&
                input.AdmissionTicket is null &&
                !arenaSlotsReopened &&
                DeviceContext.IsOperational)
            {
                RetireOpenXrSubmission(
                    timelineSemaphore,
                    timelineValue,
                    mappedFrameArena,
                    mappedFrameGeneration,
                    commandBuffers,
                    input.CommandBufferCount,
                    frameDataArena,
                    frameDataGeneration,
                    commandBuffersCompleted);
            }
        }
    }

    private unsafe void RetireOpenXrSubmission(
        VulkanSemaphore timelineSemaphore,
        ulong timelineValue,
        VulkanMappedFrameArena? arena,
        ulong generation,
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        VulkanFrameDataArena? frameDataArena,
        ulong frameDataGeneration,
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
                OpenXrFrameSlotAppearedEarlier(
                    commandBuffers,
                    index,
                    frameSlot))
            {
                continue;
            }

            frameSlots[frameSlotCount++] = checked((uint)frameSlot);
        }

        lock (_retiredOpenXrSubmissionsGate)
        {
            _retiredOpenXrSubmissions.Add(
                new RetiredOpenXrSubmissionTimeline(
                    timelineSemaphore,
                    timelineValue,
                    completionProven,
                    arena,
                    generation,
                    frameSlots,
                    frameSlotCount,
                    frameDataArena,
                    frameDataGeneration));
        }
    }

    private unsafe void DrainRetiredOpenXrSubmissions()
    {
        if (!DeviceContext.IsOperational)
            return;

        lock (_retiredOpenXrSubmissionsGate)
        {
            for (int index = _retiredOpenXrSubmissions.Count - 1;
                 index >= 0;
                 index--)
            {
                RetiredOpenXrSubmissionTimeline retired =
                    _retiredOpenXrSubmissions[index];
                if (!retired.CompletionProven)
                {
                    Result result = Synchronization.QueryTimelineCompletion(
                        Api,
                        DeviceContext,
                        ResourceRuntime.Lifetime.Tracker,
                        retired.TimelineSemaphore,
                        retired.TimelineValue,
                        out bool completed);
                    if (result == Result.Success && !completed)
                        continue;
                    if (result != Result.Success)
                    {
                        Debug.VulkanWarning(
                            "[OpenXR] Deferred submission timeline query failed: {0}",
                            result);
                        continue;
                    }

                    CompleteTrackedTimeline(
                        retired.TimelineSemaphore,
                        retired.TimelineValue);
                    retired = retired with
                    {
                        CompletionProven = true,
                    };
                    _retiredOpenXrSubmissions[index] = retired;
                }

                if (!TryCompleteRetiredOpenXrMappedFrameSlots(retired))
                {
                    Debug.VulkanWarning(
                        "[OpenXR] Deferred submission timeline completed, but its mapped frame slots could not be reopened.");
                    continue;
                }
                _retiredOpenXrSubmissions.RemoveAt(index);
            }
        }
    }

    private static bool TryCompleteRetiredOpenXrMappedFrameSlots(
        RetiredOpenXrSubmissionTimeline retired)
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

        if (retired.FrameDataArena is not null)
        {
            for (int index = 0; index < retired.FrameSlotCount; index++)
            {
                if (!retired.FrameDataArena.TryResetFrameSlot(
                        retired.FrameSlots[index],
                        retired.FrameDataGeneration,
                        submissionCompletionProven: true))
                {
                    return false;
                }
            }
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
                OpenXrFrameSlotAppearedEarlier(
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
                OpenXrFrameSlotAppearedEarlier(
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
                OpenXrFrameSlotAppearedEarlier(
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
                OpenXrFrameSlotAppearedEarlier(
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

    private unsafe bool TryPrepareOpenXrFrameDataSlotsForSubmission(
        VulkanFrameDataArena arena,
        ulong generation,
        CommandBuffer* commandBuffers,
        uint commandBufferCount)
    {
        for (uint index = 0; index < commandBufferCount; index++)
        {
            int frameSlot = ResolveCommandBufferImageIndex(commandBuffers[index]);
            if (frameSlot < 0 ||
                OpenXrFrameSlotAppearedEarlier(
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

            CancelOpenXrFrameDataSlotsSubmission(
                arena,
                generation,
                commandBuffers,
                index);
            return false;
        }

        return true;
    }

    private unsafe void MarkOpenXrFrameDataSlotsSubmitted(
        VulkanFrameDataArena arena,
        ulong generation,
        CommandBuffer* commandBuffers,
        uint commandBufferCount)
    {
        for (uint index = 0; index < commandBufferCount; index++)
        {
            int frameSlot = ResolveCommandBufferImageIndex(commandBuffers[index]);
            if (frameSlot < 0 ||
                OpenXrFrameSlotAppearedEarlier(
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

    private unsafe void CancelOpenXrFrameDataSlotsSubmission(
        VulkanFrameDataArena arena,
        ulong generation,
        CommandBuffer* commandBuffers,
        uint commandBufferCount)
    {
        for (uint index = 0; index < commandBufferCount; index++)
        {
            int frameSlot = ResolveCommandBufferImageIndex(commandBuffers[index]);
            if (frameSlot < 0 ||
                OpenXrFrameSlotAppearedEarlier(
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

    private unsafe bool TryCompleteOpenXrFrameDataSlots(
        VulkanFrameDataArena arena,
        ulong generation,
        CommandBuffer* commandBuffers,
        uint commandBufferCount)
    {
        for (uint index = 0; index < commandBufferCount; index++)
        {
            int frameSlot = ResolveCommandBufferImageIndex(commandBuffers[index]);
            if (frameSlot < 0 ||
                OpenXrFrameSlotAppearedEarlier(
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

    private unsafe bool OpenXrFrameSlotAppearedEarlier(
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
