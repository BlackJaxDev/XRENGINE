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
        Result createFenceResult = Api!.CreateFence(device, ref fenceCreateInfo, null, out Fence fence);
        if (createFenceResult != Result.Success)
        {
            if (createFenceResult == Result.ErrorDeviceLost)
                MarkDeviceLost("OpenXR Vulkan submit fence creation returned ErrorDeviceLost", "vkCreateFence.OpenXR", createFenceResult);
            throw new InvalidOperationException("Failed to create OpenXR Vulkan submit fence.");
        }

        SetDebugObjectName(ObjectType.Fence, fence.Handle, "OpenXR.SubmitAndWaitFence");

        try
        {
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = commandBufferCount,
                PCommandBuffers = commandBuffers,
            };

            Result submitResult;
            long submitStart = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.QueueSubmit"))
            {
                long queueLockWaitStart = Stopwatch.GetTimestamp();
                bool queueLockTaken = false;
                try
                {
                    Monitor.Enter(_oneTimeSubmitLock, ref queueLockTaken);
                    LogOpenXrSerializedCriticalSectionWait("QueueSubmit", queueLockWaitStart, Stopwatch.GetTimestamp());
                    submitResult = SubmitToQueueTrackedWithDisposition(
                        graphicsQueue,
                        ref submitInfo,
                        fence,
                        diagnosticContext,
                        out bool queueDispatchAttempted,
                        out injectedFailureStage);
                    if (queueDispatchAttempted)
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

            if (submitResult != Result.Success)
            {
                if (submitResult == Result.ErrorDeviceLost)
                    MarkDeviceLost("OpenXR Vulkan eye submit returned ErrorDeviceLost", "vkQueueSubmit.OpenXR", submitResult);

                Debug.VulkanWarning($"[OpenXR] Vulkan eye QueueSubmit failed: {submitResult}");
                return false;
            }

            long waitStart = Stopwatch.GetTimestamp();
            Result waitResult;
            if (!TryAdmitVulkanDeviceOperation("vkWaitForFences.OpenXR", out _))
                return false;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.SubmitFenceWait"))
            using (VulkanCpuStageScope fenceWaitStage =
                new(EVulkanCpuStage.AuxiliaryFenceWait))
            {
                waitResult = Api!.WaitForFences(
                    device,
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
            if (fence.Handle != 0)
                Api!.DestroyFence(device, fence, null);
        }
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
