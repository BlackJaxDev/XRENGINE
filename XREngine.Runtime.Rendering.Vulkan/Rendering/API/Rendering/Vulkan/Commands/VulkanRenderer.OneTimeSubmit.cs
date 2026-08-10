using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanCommandRuntime
    {
        public class CommandScope : IDisposable
        {
            private readonly VulkanCommandRuntime _runtime;
            private readonly bool _useTransferQueue;

            public CommandScope(VulkanCommandRuntime runtime, CommandBuffer cmd, bool useTransferQueue)
            {
                _runtime = runtime;
                CommandBuffer = cmd;
                _useTransferQueue = useTransferQueue;
            }

            public CommandBuffer CommandBuffer { get; }

            public void Dispose()
            {
                _runtime.CommandsStop(CommandBuffer, _useTransferQueue);
                GC.SuppressFinalize(this);
            }
        }

        internal CommandScope NewCommandScope()
            => new(this, CommandsStart(useTransferQueue: false), useTransferQueue: false);

        private CommandScope NewTransferCommandScope()
            => new(this, CommandsStart(useTransferQueue: true), useTransferQueue: true);

        private CommandBuffer CommandsStart(bool useTransferQueue)
        {
            if (!DeviceContext.IsOperational)
                throw new InvalidOperationException(
                    $"Cannot start a one-time command while device state is {DeviceContext.State}.");

            CommandPool pool = useTransferQueue
                ? GetThreadTransferCommandPool()
                : GetThreadCommandPool();

            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = pool,
                CommandBufferCount = 1,
            };

            Result allocateResult = AllocateVulkanCommandBufferTracked(
                ref allocateInfo,
                out CommandBuffer commandBuffer,
                "OneTimeSubmit");
            if (allocateResult != Result.Success || commandBuffer.Handle == 0)
                throw new InvalidOperationException($"Failed to allocate a Vulkan one-shot command buffer ({allocateResult}).");

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            Result beginResult = Api.BeginCommandBuffer(commandBuffer, ref beginInfo);
            DeviceContext.ObserveNativeResult("vkBeginCommandBuffer.OneTimeSubmit", beginResult);
            if (beginResult != Result.Success)
            {
                FreeVulkanCommandBufferTracked(pool, ref commandBuffer, "OneTimeSubmit.BeginFailure");
                throw new InvalidOperationException($"Failed to begin a Vulkan one-shot command buffer ({beginResult}).");
            }
            ResetCommandBufferBindState(commandBuffer);

            lock (_oneTimeCommandPoolsLock)
                _oneTimeCommandPools[commandBuffer.Handle] = new OneTimeCommandOwner(pool, useTransferQueue);

            return commandBuffer;
        }

        private void CommandsStop(CommandBuffer commandBuffer, bool useTransferQueue)
        {
            if (!DeviceContext.IsOperational)
            {
                RemoveCommandBufferBindState(commandBuffer);
                return;
            }

            Result endResult = EndCommandBufferTracked(commandBuffer);
            if (endResult != Result.Success)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Failed to end one-shot command buffer 0x{0:X} (result={1}).",
                    commandBuffer.Handle,
                    endResult);
                RemoveCommandBufferBindState(commandBuffer);
                return;
            }

            // Use a per-submission fence instead of QueueWaitIdle so we wait only
            // on this specific submission and avoid stalling unrelated GPU work on
            // the same queue.  Also allows correct error handling â€” if the fence
            // wait fails (e.g. device lost) we skip freeing the still-pending CB.
            FenceCreateInfo fenceCreateInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
                Flags = 0,
            };
            Fence submitFence;
            Result fenceResult = Api.CreateFence(DeviceContext.Device, ref fenceCreateInfo, null, &submitFence);
            DeviceContext.ObserveNativeResult("vkCreateFence.OneTimeSubmit", fenceResult);
            if (fenceResult != Result.Success)
            {
                Debug.VulkanWarning($"[Vulkan] Failed to create one-shot submit fence (result={fenceResult}). Falling back to QueueWaitIdle.");
                submitFence = default;
            }
            else
            {
                NameOneTimeSubmissionFence(submitFence, useTransferQueue);
            }

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };


            bool waitSucceeded;
            lock (_oneTimeSubmitLock)
            {
                Queue submitQueue = SelectOneTimeSubmitQueue(useTransferQueue);
                VulkanSubmissionReceipt receipt = SubmitToQueueTrackedWithDisposition(
                    submitQueue,
                    ref submitInfo,
                    submitFence,
                    default,
                    out _,
                    out _,
                    "OneTimeSubmit");
                Result submitResult = receipt.Result;
                if (submitResult != Result.Success)
                {
                    if (submitResult == Result.ErrorDeviceLost)
                    Debug.VulkanWarning($"[Vulkan] One-shot QueueSubmit failed (result={submitResult}). Skipping command buffer free.");
                    if (submitFence.Handle != 0 && submitResult != Result.ErrorDeviceLost)
                        Api.DestroyFence(DeviceContext.Device, submitFence, null);
                    RemoveCommandBufferBindState(commandBuffer);
                    return;
                }

                if (submitFence.Handle != 0)
                {
                    if (!DeviceContext.IsOperational)
                    {
                        RemoveCommandBufferBindState(commandBuffer);
                        return;
                    }

                    Result waitResult;
                    using (VulkanCpuStageScope fenceWaitStage =
                            new(FrameTelemetry, EVulkanCpuStage.AuxiliaryFenceWait))
                    {
                        waitResult = Api.WaitForFences(
                            DeviceContext.Device,
                            1,
                            &submitFence,
                            true,
                            ulong.MaxValue);
                    }
                    waitSucceeded = waitResult == Result.Success;
                    DeviceContext.ObserveNativeResult("vkWaitForFences.OneTimeSubmit", waitResult);
                    if (waitSucceeded)
                        CompleteTrackedFence(submitFence);
                    if (!waitSucceeded)
                        Debug.VulkanWarning($"[Vulkan] WaitForFences for one-shot submit failed (result={waitResult}). Command buffer will not be freed to avoid use-after-free.");
                }
                else
                {
                    // Fence creation failed â€” fall back to QueueWaitIdle.
                    Result waitResult = Api.QueueWaitIdle(submitQueue);
                    DeviceContext.ObserveNativeResult("vkQueueWaitIdle.OneTimeSubmit", waitResult);
                    waitSucceeded = waitResult == Result.Success;
                    if (waitSucceeded)
                        CompleteTrackedQueue(submitQueue);
                    if (!waitSucceeded)
                        Debug.VulkanWarning($"[Vulkan] QueueWaitIdle fallback failed (result={waitResult}). Command buffer will not be freed.");
                }
            }

            if (submitFence.Handle != 0 && waitSucceeded)
                Api.DestroyFence(DeviceContext.Device, submitFence, null);

            if (!waitSucceeded)
            {
                // Do not free the command buffer â€” it may still be in flight.
                RemoveCommandBufferBindState(commandBuffer);
                return;
            }

            CommandPool pool = useTransferQueue ? GetThreadTransferCommandPool() : GetThreadCommandPool();
            lock (_oneTimeCommandPoolsLock)
            {
                if (_oneTimeCommandPools.Remove(commandBuffer.Handle, out OneTimeCommandOwner owner) && owner.Pool.Handle != 0)
                {
                    pool = owner.Pool;
                    useTransferQueue = owner.UseTransferQueue;
                }
            }

            RemoveCommandBufferBindState(commandBuffer);
            FreeVulkanCommandBufferTracked(pool, ref commandBuffer, "OneTimeSubmit.Completed");
        }

        private Queue SelectOneTimeSubmitQueue(bool useTransferQueue)
        {
            if (useTransferQueue)
                return DeviceContext.TransferQueue;

            return DeviceContext.GraphicsQueue;
        }

        private void NameOneTimeSubmissionFence(Fence fence, bool transfer)
        {
            if (fence.Handle == 0 || DeviceContext.DebugUtils is null || !FrameTelemetry._diagnosticOptions.EnableDebugUtils)
                return;

            string label = transfer ? "OneShot.TransferFence" : "OneShot.GraphicsFence";
            ReadOnlySpan<byte> utf8 = System.Text.Encoding.UTF8.GetBytes(label + '\0');
            fixed (byte* name = utf8)
            {
                DebugUtilsObjectNameInfoEXT info = new()
                {
                    SType = StructureType.DebugUtilsObjectNameInfoExt,
                    ObjectType = ObjectType.Fence,
                    ObjectHandle = fence.Handle,
                    PObjectName = name,
                };
                _ = DeviceContext.DebugUtils.SetDebugUtilsObjectName(DeviceContext.Device, in info);
            }
        }

    }
}
