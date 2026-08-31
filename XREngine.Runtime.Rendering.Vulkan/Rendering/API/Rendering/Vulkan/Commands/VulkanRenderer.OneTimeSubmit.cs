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
    internal sealed partial class VulkanCommandRuntime
    {
        public class CommandScope : IDisposable
        {
            private readonly VulkanCommandRuntime _runtime;
            private readonly bool _useTransferQueue;
            private readonly VulkanFrameDataArena? _synchronousArena;
            private readonly VulkanFrameDataSlice _synchronousSlice;

            public CommandScope(VulkanCommandRuntime runtime, CommandBuffer cmd, bool useTransferQueue)
                : this(runtime, cmd, useTransferQueue, null, default)
            {
            }

            internal CommandScope(
                VulkanCommandRuntime runtime,
                CommandBuffer cmd,
                bool useTransferQueue,
                VulkanFrameDataArena? synchronousArena,
                in VulkanFrameDataSlice synchronousSlice)
            {
                _runtime = runtime;
                CommandBuffer = cmd;
                _useTransferQueue = useTransferQueue;
                _synchronousArena = synchronousArena;
                _synchronousSlice = synchronousSlice;
            }

            public CommandBuffer CommandBuffer { get; }

            public void Dispose()
            {
                _runtime.CommandsStop(CommandBuffer, _useTransferQueue, _synchronousArena, _synchronousSlice);
                GC.SuppressFinalize(this);
            }
        }

        internal CommandScope NewCommandScope()
            => new(this, CommandsStart(useTransferQueue: false), useTransferQueue: false);

        internal CommandScope NewSynchronousFrameDataCommandScope(in VulkanFrameDataSlice slice)
            => new(
                this,
                CommandsStart(useTransferQueue: false),
                useTransferQueue: false,
                ResourceRuntime.SynchronousFrameDataArena ??
                    throw new InvalidOperationException("The synchronous frame-data arena is unavailable."),
                slice);

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

            Result beginResult = BeginTrackedCommandBuffer(
                commandBuffer,
                ref beginInfo,
                "OneTimeSubmit");
            DeviceContext.ObserveNativeResult("vkBeginCommandBuffer.OneTimeSubmit", beginResult);
            if (beginResult != Result.Success)
            {
                FreeVulkanCommandBufferTracked(pool, ref commandBuffer, "OneTimeSubmit.BeginFailure");
                throw new InvalidOperationException($"Failed to begin a Vulkan one-shot command buffer ({beginResult}).");
            }
            lock (_oneTimeCommandPoolsLock)
                _oneTimeCommandPools[commandBuffer.Handle] = new OneTimeCommandOwner(pool, useTransferQueue);

            return commandBuffer;
        }

        private unsafe void CommandsStop(
            CommandBuffer commandBuffer,
            bool useTransferQueue,
            VulkanFrameDataArena? synchronousArena,
            in VulkanFrameDataSlice synchronousSlice)
        {
            if (!DeviceContext.IsOperational)
            {
                RemoveCommandBufferBindState(commandBuffer);
                if (synchronousArena is not null)
                    throw new InvalidOperationException(
                        $"Cannot submit synchronous frame-data while device state is {DeviceContext.State}.");
                return;
            }

            if (synchronousArena is not null && synchronousSlice.Lane == EVulkanFrameDataLane.Readback)
            {
                // The fence proves execution completion, not the transfer-to-host
                // memory dependency. Publish this exact copied range before the
                // caller invalidates non-coherent memory and reads the mapped bytes.
                BufferMemoryBarrier hostReadBarrier = new()
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.HostReadBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = synchronousSlice.Buffer,
                    Offset = synchronousSlice.Offset,
                    Size = synchronousSlice.Length,
                };
                CmdPipelineBarrierTracked(
                    commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.HostBit,
                    0, 0, null, 1, &hostReadBarrier, 0, null);
            }

            Result endResult = EndCommandBufferTracked(commandBuffer);
            if (endResult != Result.Success)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Failed to end one-shot command buffer 0x{0:X} (result={1}).",
                    commandBuffer.Handle,
                    endResult);
                RemoveCommandBufferBindState(commandBuffer);
                if (synchronousArena is not null)
                    throw new InvalidOperationException(
                        $"Failed to end synchronous frame-data command buffer ({endResult}).");
                return;
            }

            bool frameDataPrepared = synchronousArena is not null;
            if (frameDataPrepared && !synchronousArena!.TryPrepareFrameSlotForSubmission(0, synchronousSlice.Generation))
            {
                Debug.VulkanWarning("[Vulkan] Failed to prepare synchronous frame-data before one-shot submission.");
                RemoveCommandBufferBindState(commandBuffer);
                throw new InvalidOperationException(
                    "Failed to prepare synchronous frame-data before one-shot submission.");
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
                if (synchronousArena is not null)
                    _ = synchronousArena.TryCancelFrameSlotSubmission(0, synchronousSlice.Generation);
                RemoveCommandBufferBindState(commandBuffer);
                ReleaseUnsubmittedOneTimeCommandBuffer(
                    ref commandBuffer,
                    useTransferQueue,
                    "OneTimeSubmit.FenceCreateRejected");
                throw new InvalidOperationException(
                    $"Failed to create a completion fence for one-shot submission ({fenceResult}); the command buffer was not submitted.");
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

            CommandPool owningPool = useTransferQueue
                ? GetThreadTransferCommandPool()
                : GetThreadCommandPool();
            lock (_oneTimeCommandPoolsLock)
            {
                if (_oneTimeCommandPools.TryGetValue(commandBuffer.Handle, out OneTimeCommandOwner owner) &&
                    owner.Pool.Handle != 0)
                {
                    owningPool = owner.Pool;
                }
            }

            bool waitSucceeded;
            // The tracked submit gateway owns the native queue lease. Fence and
            // idle waits below deliberately run after that narrow lease exits.
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
                bool submissionAccepted = receipt.SubmissionAccepted;
                if (submissionAccepted)
                    synchronousArena?.MarkFrameSlotSubmitted(0, synchronousSlice.Generation);
                if (!submissionAccepted)
                {
                    if (frameDataPrepared)
                        _ = synchronousArena!.TryCancelFrameSlotSubmission(0, synchronousSlice.Generation);
                    if (submitResult == Result.ErrorDeviceLost)
                        Debug.VulkanWarning($"[Vulkan] One-shot QueueSubmit failed (result={submitResult}). Skipping command buffer free.");
                    if (submitFence.Handle != 0 && submitResult != Result.ErrorDeviceLost)
                        Api.DestroyFence(DeviceContext.Device, submitFence, null);
                    RemoveCommandBufferBindState(commandBuffer);
                    ReleaseUnsubmittedOneTimeCommandBuffer(
                        ref commandBuffer,
                        useTransferQueue,
                        "OneTimeSubmit.SubmitRejected");
                    if (synchronousArena is not null)
                        throw new InvalidOperationException(
                            $"Synchronous frame-data QueueSubmit was rejected ({submitResult}).");
                    return;
                }

                if (submitFence.Handle != 0)
                {
                    if (!DeviceContext.IsOperational)
                    {
                        if (synchronousArena is not null)
                        {
                            RetireIncompleteSynchronousSubmission(
                                commandBuffer,
                                owningPool,
                                submitFence,
                                synchronousArena,
                                in synchronousSlice,
                                removeOneTimeOwner: true,
                                "OneTimeSubmit");
                        }
                        RemoveCommandBufferBindState(commandBuffer);
                        if (synchronousArena is not null)
                            throw new InvalidOperationException(
                                $"Synchronous frame-data completion became unavailable in device state {DeviceContext.State}.");
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
                    // An accepted submission without a completion owner cannot
                    // be recovered by idling unrelated queue work.
                    throw new InvalidOperationException(
                        "An accepted one-shot submission has no completion fence.");
                }
            }

            if (!waitSucceeded)
            {
                if (synchronousArena is not null)
                {
                    RetireIncompleteSynchronousSubmission(
                        commandBuffer,
                        owningPool,
                        submitFence,
                        synchronousArena,
                        in synchronousSlice,
                        removeOneTimeOwner: true,
                        "OneTimeSubmit");
                }
                // Do not free the command buffer â€” it may still be in flight.
                RemoveCommandBufferBindState(commandBuffer);
                if (synchronousArena is not null)
                    throw new InvalidOperationException(
                        "Synchronous frame-data submission completion could not be proven.");
                return;
            }


            if (synchronousArena is not null &&
                !synchronousArena.TryResetFrameSlot(0, synchronousSlice.Generation, submissionCompletionProven: true))
            {
                Debug.VulkanWarning("[Vulkan] Synchronous frame-data remained unavailable after one-shot completion.");
                RetireIncompleteSynchronousSubmission(
                    commandBuffer,
                    owningPool,
                    submitFence,
                    synchronousArena,
                    in synchronousSlice,
                    removeOneTimeOwner: true,
                    "OneTimeSubmit");
                RemoveCommandBufferBindState(commandBuffer);
                throw new InvalidOperationException(
                    "Synchronous frame-data remained unavailable after one-shot completion.");
            }

            if (submitFence.Handle != 0)
                Api.DestroyFence(DeviceContext.Device, submitFence, null);

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

        private void ReleaseUnsubmittedOneTimeCommandBuffer(
            ref CommandBuffer commandBuffer,
            bool useTransferQueue,
            string reason)
        {
            CommandPool pool = useTransferQueue
                ? GetThreadTransferCommandPool()
                : GetThreadCommandPool();
            lock (_oneTimeCommandPoolsLock)
            {
                if (_oneTimeCommandPools.Remove(commandBuffer.Handle, out OneTimeCommandOwner owner) &&
                    owner.Pool.Handle != 0)
                {
                    pool = owner.Pool;
                }
            }

            if (DeviceContext.IsOperational)
                FreeVulkanCommandBufferTracked(pool, ref commandBuffer, reason);
        }

        private Queue SelectOneTimeSubmitQueue(bool useTransferQueue)
        {
            if (useTransferQueue)
                return DeviceContext.TransferQueue;

            return DeviceContext.GraphicsQueue;
        }

        private unsafe void NameOneTimeSubmissionFence(Fence fence, bool transfer)
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
