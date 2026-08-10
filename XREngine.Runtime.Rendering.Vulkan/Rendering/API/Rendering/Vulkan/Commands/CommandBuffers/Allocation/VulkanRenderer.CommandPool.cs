using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanCommandRuntime
    {
        private CommandPool commandPool => _commandRuntime.Pools.PrimaryGraphics;
        private CommandPool transferCommandPool => _commandRuntime.Pools.PrimaryTransfer;
        private object CommandPoolsGate => _commandRuntime.Pools.Gate;
        private Dictionary<int, CommandPool> ThreadCommandPools => _commandRuntime.Pools.GraphicsByThread;
        private Dictionary<int, CommandPool> ThreadTransferCommandPools => _commandRuntime.Pools.TransferByThread;

        private Result AllocateCommandBuffersHostSynchronized(
            ref CommandBufferAllocateInfo allocateInfo,
            CommandBuffer* commandBuffers)
        {
            if (!DeviceContext.IsOperational)
                return Result.ErrorDeviceLost;

            lock (CommandPoolsGate)
                return Api.AllocateCommandBuffers(DeviceContext.Device, ref allocateInfo, commandBuffers);
        }

        private void FreeCommandBuffersHostSynchronized(
            CommandPool pool,
            uint commandBufferCount,
            CommandBuffer* commandBuffers)
        {
            lock (CommandPoolsGate)
                Api.FreeCommandBuffers(DeviceContext.Device, pool, commandBufferCount, commandBuffers);
        }

        internal void DestroyCommandPoolHostSynchronized(CommandPool pool)
        {
            if (pool.Handle == 0)
                return;

            // Pool destruction is always routed through the resource authority. It
            // captures the command-buffer children atomically and delays the native
            // destroy until their recording/submission pins have completed.
            ResourceRuntime.QueueCommandPoolRetirement(
                pool,
                ResourceRuntime.FramebufferRetirementFrameSlot);
        }

        private void DestroyCommandPoolNativeHostSynchronized(CommandPool pool)
        {
            lock (CommandPoolsGate)
                Api.DestroyCommandPool(DeviceContext.Device, pool, null);
        }

        internal void DestroyCommandPool()
        {
            DestroyCommandChainRecordingWorkers();

            lock (CommandPoolsGate)
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

            lock (CommandPoolsGate)
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
            lock (CommandPoolsGate)
            {
                if (ThreadCommandPools.TryGetValue(threadId, out CommandPool pool) && pool.Handle != 0)
                    return pool;
            }

            var queueFamilyIndices = DeviceContext.QueueFamilies;
            uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");

            CommandPool created = CreateCommandPoolForFamily(graphicsFamily);

            lock (CommandPoolsGate)
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
            lock (CommandPoolsGate)
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

            lock (CommandPoolsGate)
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

        private CommandPool CreateCommandPoolForFamily(uint familyIndex)
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
        internal Result CreateVulkanCommandPoolTracked(
            ref CommandPoolCreateInfo createInfo,
            out CommandPool pool,
            string owner)
        {
            pool = default;
            if (!DeviceContext.IsOperational)
                return Result.ErrorDeviceLost;
            lock (CommandPoolsGate)
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
        internal Result AllocateVulkanCommandBufferTracked(
            ref CommandBufferAllocateInfo allocateInfo,
            out CommandBuffer commandBuffer,
            string owner)
        {
            commandBuffer = default;
            fixed (CommandBuffer* commandBufferPtr = &commandBuffer)
                return AllocateVulkanCommandBuffersTracked(ref allocateInfo, commandBufferPtr, owner);
        }

        private Result AllocateVulkanCommandBuffersTracked(
            ref CommandBufferAllocateInfo allocateInfo,
            CommandBuffer* commandBuffers,
            string owner)
        {
            Result result = AllocateCommandBuffersHostSynchronized(ref allocateInfo, commandBuffers);
            if (result != Result.Success)
                return result;

            for (uint index = 0; index < allocateInfo.CommandBufferCount; index++)
                ResourceRuntime.RegisterSynchronousCommandBuffer(
                    commandBuffers[index],
                    allocateInfo.CommandPool,
                    allocateInfo.Level,
                    owner);
            return result;
        }

        /// <summary>
        /// Resets every child command buffer only after the tracker has proved that
        /// no recording, submission, or cached dependency still owns it.
        /// </summary>
        internal Result ResetVulkanCommandPoolTracked(CommandPool pool, string owner)
        {
            if (pool.Handle == 0)
                return Result.Success;

            lock (CommandPoolsGate)
            {
                VulkanResourceLifetimeKey poolKey = ResourceKey(ObjectType.CommandPool, pool.Handle);
                CommandBuffer[] children;
                lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
                {
                    if (!ResourceRuntime.Lifetime.Tracker.CommandBuffersByPool.TryGetValue(poolKey, out HashSet<ulong>? ownedChildren) ||
                        ownedChildren.Count == 0)
                    {
                        children = [];
                    }
                    else
                    {
                        children = new CommandBuffer[ownedChildren.Count];
                        int index = 0;
                        foreach (ulong childHandle in ownedChildren)
                            children[index++] = new CommandBuffer { Handle = unchecked((nint)childHandle) };
                    }
                }

                for (int i = 0; i < children.Length; i++)
                    if (!ResourceRuntime.CanResetCommandBuffer(children[i]))
                        throw new InvalidOperationException(
                            $"Cannot reset command pool 0x{pool.Handle:X} for {owner}: child command buffer " +
                            $"0x{unchecked((ulong)children[i].Handle):X} is not resettable.");

                Result result = Api.ResetCommandPool(DeviceContext.Device, pool, 0);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResetCommandPoolCall();
                if (result != Result.Success)
                    return result;

                for (int i = 0; i < children.Length; i++)
                    ResourceRuntime.CompleteCommandBufferReset(
                        unchecked((ulong)children[i].Handle));
                return result;
            }
        }
    }
}
