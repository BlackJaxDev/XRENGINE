using System;
using System.Buffers;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanCommandRuntime
    {
        private CommandPool commandPool => _commandRuntime.Pools.PrimaryGraphics;
        private CommandPool transferCommandPool => _commandRuntime.Pools.PrimaryTransfer;
        private object CommandPoolsGate => _commandRuntime.Pools.Gate;
        private Dictionary<int, CommandPool> ThreadCommandPools => _commandRuntime.Pools.GraphicsByThread;
        private Dictionary<int, CommandPool> ThreadTransferCommandPools => _commandRuntime.Pools.TransferByThread;

        internal void DestroyCommandPoolHostSynchronized(CommandPool pool)
        {
            if (pool.Handle == 0)
                return;

            // Pool destruction is always routed through the resource authority. It
            // captures the command-buffer children atomically and delays the native
            // destroy until their recording/submission pins have completed.
            QueueCommandPoolRetirementTracked(
                pool,
                ResourceRuntime.FramebufferRetirementFrameSlot);
        }

        internal void DestroyCommandPool()
        {
            // Full-device teardown can still own non-desktop command-chain
            // caches (for example OpenXR frame slots). Stop recording and
            // retire those artifacts before worker arenas validate that their
            // pools no longer own recorded buffers.
            CancelCommandChainRecordingWorkers();
            DestroyCommandChainCaches();
            DestroyCommandChainRecordingWorkers();

            using (VulkanFrameLockScope.Enter(
                       CommandPoolsGate,
                       EVulkanFrameWaitReason.CommandPool))
            {
                HashSet<ulong> destroyed = [];
                foreach (CommandPool pool in ThreadCommandPools.Values)
                    if (pool.Handle != 0 && destroyed.Add(pool.Handle))
                        DestroyCommandPoolHostSynchronized(pool);

                foreach (CommandPool pool in ThreadTransferCommandPools.Values)
                    if (pool.Handle != 0 && destroyed.Add(pool.Handle))
                        DestroyCommandPoolHostSynchronized(pool);

                ThreadCommandPools.Clear();
                ThreadTransferCommandPools.Clear();
                _commandRuntime.Pools.PrimaryGraphics = default;
                _commandRuntime.Pools.PrimaryTransfer = default;
            }
        }

        internal void CreateCommandPool()
        {
            var queueFamilyIndices = DeviceContext.QueueFamilies;
            uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");
            uint transferFamily = queueFamilyIndices.TransferFamilyIndex ?? graphicsFamily;

            CommandPool primaryPool = CreateCommandPoolForFamily(graphicsFamily);
            CommandPool primaryTransferPool = transferFamily == graphicsFamily
                ? primaryPool
                : CreateCommandPoolForFamily(transferFamily);

            using (VulkanFrameLockScope.Enter(
                       CommandPoolsGate,
                       EVulkanFrameWaitReason.CommandPool))
            {
                _commandRuntime.Pools.PrimaryGraphics = primaryPool;
                _commandRuntime.Pools.PrimaryTransfer = primaryTransferPool;
                ThreadCommandPools[Environment.CurrentManagedThreadId] = primaryPool;
                ThreadTransferCommandPools[Environment.CurrentManagedThreadId] = primaryTransferPool;
            }
        }

        internal CommandPool GetThreadCommandPool()
        {
            int threadId = Environment.CurrentManagedThreadId;
            using (VulkanFrameLockScope.Enter(
                       CommandPoolsGate,
                       EVulkanFrameWaitReason.CommandPool))
            {
                if (ThreadCommandPools.TryGetValue(threadId, out CommandPool pool) && pool.Handle != 0)
                    return pool;
            }

            var queueFamilyIndices = DeviceContext.QueueFamilies;
            uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");

            CommandPool created = CreateCommandPoolForFamily(graphicsFamily);

            using (VulkanFrameLockScope.Enter(
                       CommandPoolsGate,
                       EVulkanFrameWaitReason.CommandPool))
            {
                if (ThreadCommandPools.TryGetValue(threadId, out CommandPool existing) && existing.Handle != 0)
                {
                    // Another thread raced to create for this id; keep existing and dispose duplicate.
                    DestroyCommandPoolHostSynchronized(created);
                    return existing;
                }

                ThreadCommandPools[threadId] = created;
                return created;
            }
        }

        internal CommandPool GetThreadTransferCommandPool()
        {
            int threadId = Environment.CurrentManagedThreadId;
            using (VulkanFrameLockScope.Enter(
                       CommandPoolsGate,
                       EVulkanFrameWaitReason.CommandPool))
            {
                if (ThreadTransferCommandPools.TryGetValue(threadId, out CommandPool pool) && pool.Handle != 0)
                    return pool;
            }

            var queueFamilyIndices = DeviceContext.QueueFamilies;
            uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");
            uint transferFamily = queueFamilyIndices.TransferFamilyIndex ?? graphicsFamily;

            CommandPool created = transferFamily == graphicsFamily
                ? GetThreadCommandPool()
                : CreateCommandPoolForFamily(transferFamily);

            using (VulkanFrameLockScope.Enter(
                       CommandPoolsGate,
                       EVulkanFrameWaitReason.CommandPool))
            {
                if (ThreadTransferCommandPools.TryGetValue(threadId, out CommandPool existing) && existing.Handle != 0)
                {
                    if (transferFamily != graphicsFamily && created.Handle != existing.Handle)
                        DestroyCommandPoolHostSynchronized(created);
                    return existing;
                }

                ThreadTransferCommandPools[threadId] = created;
                return created;
            }
        }

        private unsafe CommandPool CreateCommandPoolForFamily(uint familyIndex)
        {
            CommandPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = familyIndex,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit | CommandPoolCreateFlags.TransientBit,
            };

            if (!DeviceContext.IsOperational)
                throw new InvalidOperationException(
                    $"Cannot create a command pool while device state is {DeviceContext.State}.");
            if (Api.CreateCommandPool(DeviceContext.Device, ref poolInfo, null, out CommandPool pool) != Result.Success)
                throw new Exception("Failed to create Vulkan command pool.");

            ResourceRuntime.Lifetime.Tracker.RegisterResource(
                new VulkanResourceLifetimeKey(ObjectType.CommandPool, pool.Handle),
                $"CommandPool.QueueFamily.{familyIndex}",
                externallyOwned: false);

            return pool;
        }

        /// <summary>Creates a sidecar-owned command pool under the renderer lifetime tracker.</summary>
        internal unsafe Result CreateVulkanCommandPoolTracked(
            ref CommandPoolCreateInfo createInfo,
            out CommandPool pool,
            string owner)
        {
            pool = default;
            if (!DeviceContext.IsOperational)
                return Result.ErrorDeviceLost;
            using (VulkanFrameLockScope.Enter(
                       CommandPoolsGate,
                       EVulkanFrameWaitReason.CommandPool))
            {
                Result result = Api.CreateCommandPool(DeviceContext.Device, ref createInfo, null, out pool);
                if (result == Result.Success)
                {
                    ResourceRuntime.Lifetime.Tracker.RegisterResource(
                        new VulkanResourceLifetimeKey(ObjectType.CommandPool, pool.Handle),
                        owner,
                        externallyOwned: false);
                }
                return result;
            }
        }

        /// <summary>Allocates a sidecar command buffer with persistent pool ownership.</summary>
        internal unsafe Result AllocateVulkanCommandBufferTracked(
            ref CommandBufferAllocateInfo allocateInfo,
            out CommandBuffer commandBuffer,
            string owner)
        {
            commandBuffer = default;
            fixed (CommandBuffer* commandBufferPtr = &commandBuffer)
                return AllocateVulkanCommandBuffersTracked(ref allocateInfo, commandBufferPtr, owner);
        }

        private unsafe Result AllocateVulkanCommandBuffersTracked(
            ref CommandBufferAllocateInfo allocateInfo,
            CommandBuffer* commandBuffers,
            string owner)
        {
            return AllocateCommandBuffersWithLifetime(
                ref allocateInfo,
                commandBuffers,
                owner);
        }

        /// <summary>
        /// Resets every child command buffer only after the tracker has proved that
        /// no recording, submission, or cached dependency still owns it.
        /// </summary>
        internal Result ResetVulkanCommandPoolTracked(CommandPool pool, string owner)
        {
            if (pool.Handle == 0)
                return Result.Success;

            using (VulkanFrameLockScope.Enter(
                       CommandPoolsGate,
                       EVulkanFrameWaitReason.CommandPool))
            {
                VulkanResourceLifetimeKey poolKey = ResourceKey(ObjectType.CommandPool, pool.Handle);
                VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
                CommandBuffer[]? children = null;
                int childCount = 0;
                bool hostUseMarked = false;
                bool nativeResetSucceeded = false;
                try
                {
                    using (VulkanFrameLockScope.Enter(
                               tracker.SyncRoot,
                               EVulkanFrameWaitReason.ResourceLifetimeLock))
                    {
                        if (tracker.ResourceLifetimes.TryGetValue(
                                poolKey,
                                out VulkanResourceLifetimeRecord? poolRecord) &&
                            (poolRecord.State &
                             (EVulkanResourceLifetimeState.PendingRetirement |
                              EVulkanResourceLifetimeState.Destroyed)) != 0)
                        {
                            throw new InvalidOperationException(
                                $"Cannot reset command pool 0x{pool.Handle:X} for {owner} while it is {poolRecord.State}.");
                        }

                        if (tracker.CommandBuffersByPool.TryGetValue(
                                poolKey,
                                out HashSet<ulong>? ownedChildren) &&
                            ownedChildren.Count != 0)
                        {
                            children = ArrayPool<CommandBuffer>.Shared.Rent(
                                ownedChildren.Count);
                            foreach (ulong childHandle in ownedChildren)
                            {
                                if (!ResourceRuntime.CanResetCommandBufferNoLock(
                                        childHandle))
                                {
                                    throw new InvalidOperationException(
                                        $"Cannot reset command pool 0x{pool.Handle:X} for {owner}: child command buffer 0x{childHandle:X} is not resettable.");
                                }

                                if (CommandBuffers.TrackingBatches.TryGetValue(
                                        childHandle,
                                        out VulkanCommandBufferTrackingBatch? batch))
                                {
                                    using (VulkanFrameLockScope.Enter(
                                               batch,
                                               EVulkanFrameWaitReason.CommandPool))
                                    {
                                        if (batch.IsRecording ||
                                            batch.QueuedSubmissionCount != 0)
                                        {
                                            throw new InvalidOperationException(
                                                $"Cannot reset command pool 0x{pool.Handle:X} for {owner}: child command buffer 0x{childHandle:X} is recording or queued.");
                                        }
                                    }
                                }

                                children[childCount++] = new CommandBuffer
                                {
                                    Handle = unchecked((nint)childHandle),
                                };
                            }

                            for (int index = 0; index < childCount; index++)
                            {
                                ulong childHandle = unchecked(
                                    (ulong)children[index].Handle);
                                if (CommandBuffers.TrackingBatches.TryGetValue(
                                        childHandle,
                                        out VulkanCommandBufferTrackingBatch? batch))
                                {
                                    using (VulkanFrameLockScope.Enter(
                                               batch,
                                               EVulkanFrameWaitReason.CommandPool))
                                        batch.IsRecording = true;
                                }
                            }
                            hostUseMarked = true;
                        }
                    }

                    Result result = Api.ResetCommandPool(
                        DeviceContext.Device,
                        pool,
                        0);
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResetCommandPoolCall();
                    nativeResetSucceeded = result == Result.Success;
                    if (!nativeResetSucceeded)
                        return result;

                    for (int index = 0; index < childCount; index++)
                    {
                        CommandBuffer child = children![index];
                        ResourceRuntime.CompleteCommandBufferReset(
                            unchecked((ulong)child.Handle));
                        RemoveCommandBufferState(child);
                    }
                    return result;
                }
                finally
                {
                    if (children is not null)
                    {
                        if (hostUseMarked)
                        {
                            using (VulkanFrameLockScope.Enter(
                                       tracker.SyncRoot,
                                       EVulkanFrameWaitReason.ResourceLifetimeLock))
                            {
                                for (int index = 0; index < childCount; index++)
                                {
                                    ulong childHandle = unchecked(
                                        (ulong)children[index].Handle);
                                    if (!CommandBuffers.TrackingBatches.TryGetValue(
                                            childHandle,
                                            out VulkanCommandBufferTrackingBatch? batch))
                                    {
                                        continue;
                                    }

                                    using (VulkanFrameLockScope.Enter(
                                               batch,
                                               EVulkanFrameWaitReason.CommandPool))
                                    {
                                        batch.IsRecording = false;
                                        if (nativeResetSucceeded)
                                        {
                                            CommandBuffers.TrackingBatches.TryRemove(
                                                childHandle,
                                                out _);
                                        }
                                    }
                                }
                            }
                        }

                        ArrayPool<CommandBuffer>.Shared.Return(
                            children,
                            clearArray: false);
                    }
                }
            }
        }
    }
}
