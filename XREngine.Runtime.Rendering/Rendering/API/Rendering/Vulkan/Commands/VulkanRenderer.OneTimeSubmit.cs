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
    public unsafe partial class VulkanRenderer
    {
        public class CommandScope : IDisposable
        {
            private readonly VulkanRenderer _api;
            private readonly bool _useTransferQueue;

            public CommandScope(VulkanRenderer api, CommandBuffer cmd, bool useTransferQueue)
            {
                _api = api;
                CommandBuffer = cmd;
                _useTransferQueue = useTransferQueue;
            }

            public CommandBuffer CommandBuffer { get; }

            public void Dispose()
            {
                _api.CommandsStop(CommandBuffer, _useTransferQueue);
                GC.SuppressFinalize(this);
            }
        }

        private CommandScope NewCommandScope()
            => new(this, CommandsStart(useTransferQueue: false), useTransferQueue: false);

        private CommandScope NewTransferCommandScope()
            => new(this, CommandsStart(useTransferQueue: true), useTransferQueue: true);

        private CommandBuffer CommandsStart(bool useTransferQueue)
        {
            if (_deviceLost)
                throw new InvalidOperationException("Cannot allocate a Vulkan one-shot command buffer after the device was lost.");

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

            Api!.AllocateCommandBuffers(device, ref allocateInfo, out CommandBuffer commandBuffer);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            Api!.BeginCommandBuffer(commandBuffer, ref beginInfo);
            ResetCommandBufferBindState(commandBuffer);

            lock (_oneTimeCommandPoolsLock)
                _oneTimeCommandPools[commandBuffer.Handle] = new OneTimeCommandOwner(pool, useTransferQueue);

            return commandBuffer;
        }

        private void CommandsStop(CommandBuffer commandBuffer, bool useTransferQueue)
        {
            if (_deviceLost)
            {
                RemoveCommandBufferBindState(commandBuffer);
                return;
            }

            Api!.EndCommandBuffer(commandBuffer);

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
            Result fenceResult = Api!.CreateFence(device, ref fenceCreateInfo, null, &submitFence);
            if (fenceResult != Result.Success)
            {
                Debug.VulkanWarning($"[Vulkan] Failed to create one-shot submit fence (result={fenceResult}). Falling back to QueueWaitIdle.");
                submitFence = default;
            }
            else
            {
                SetDebugObjectName(ObjectType.Fence, submitFence.Handle, useTransferQueue ? "OneShot.TransferFence" : "OneShot.GraphicsFence");
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
                Result submitResult = SubmitToQueueTracked(submitQueue, ref submitInfo, submitFence);
                if (submitResult != Result.Success)
                {
                    if (submitResult == Result.ErrorDeviceLost)
                        MarkDeviceLost();

                    Debug.VulkanWarning($"[Vulkan] One-shot QueueSubmit failed (result={submitResult}). Skipping command buffer free.");
                    if (submitFence.Handle != 0 && submitResult != Result.ErrorDeviceLost)
                        Api!.DestroyFence(device, submitFence, null);
                    RemoveCommandBufferBindState(commandBuffer);
                    return;
                }

                if (submitFence.Handle != 0)
                {
                    Result waitResult = Api!.WaitForFences(device, 1, &submitFence, true, ulong.MaxValue);
                    waitSucceeded = waitResult == Result.Success;
                    if (waitResult == Result.ErrorDeviceLost)
                        MarkDeviceLost();
                    if (!waitSucceeded)
                        Debug.VulkanWarning($"[Vulkan] WaitForFences for one-shot submit failed (result={waitResult}). Command buffer will not be freed to avoid use-after-free.");
                }
                else
                {
                    // Fence creation failed â€” fall back to QueueWaitIdle.
                    Result waitResult = Api!.QueueWaitIdle(submitQueue);
                    waitSucceeded = waitResult == Result.Success;
                    if (waitResult == Result.ErrorDeviceLost)
                        MarkDeviceLost();
                    if (!waitSucceeded)
                        Debug.VulkanWarning($"[Vulkan] QueueWaitIdle fallback failed (result={waitResult}). Command buffer will not be freed.");
                }
            }

            if (submitFence.Handle != 0 && waitSucceeded)
                Api!.DestroyFence(device, submitFence, null);

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

            Api!.FreeCommandBuffers(device, pool, 1, ref commandBuffer);
            RemoveCommandBufferBindState(commandBuffer);
        }

        private Queue SelectOneTimeSubmitQueue(bool useTransferQueue)
        {
            if (useTransferQueue)
                return transferQueue;

            return graphicsQueue;
        }

    }
}
