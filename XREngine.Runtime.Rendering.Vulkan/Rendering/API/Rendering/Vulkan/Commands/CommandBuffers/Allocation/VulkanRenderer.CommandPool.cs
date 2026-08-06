using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
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
            if (!TryAdmitVulkanDeviceOperation(
                    "vkAllocateCommandBuffers",
                    out _))
            {
                return Result.ErrorDeviceLost;
            }

            lock (CommandPoolsGate)
                return Api!.AllocateCommandBuffers(_deviceContext.Device, ref allocateInfo, commandBuffers);
        }

        private void FreeCommandBuffersHostSynchronized(
            CommandPool pool,
            uint commandBufferCount,
            CommandBuffer* commandBuffers)
        {
            lock (CommandPoolsGate)
                Api!.FreeCommandBuffers(_deviceContext.Device, pool, commandBufferCount, commandBuffers);
        }

        internal void DestroyCommandPoolHostSynchronized(CommandPool pool)
        {
            if (pool.Handle == 0)
                return;

            // Allocation, explicit free, and pool retirement share this lock. The
            // pool-child ownership registration must be atomic with pool retirement
            // so a just-allocated cached command buffer cannot escape the retire set.
            lock (CommandPoolsGate)
            {
                VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                    ObjectType.CommandPool,
                    pool.Handle,
                    nameof(DestroyCommandPoolHostSynchronized));
                ticket = CaptureCommandPoolChildRetirementTicket(pool, ticket);
                if (!IsVulkanRetirementReady(ticket) || !AreCommandPoolChildrenRetirementReady(pool))
                {
                    RetireCommandPool(pool, ticket);
                    return;
                }

                DestroyCommandPoolNativeHostSynchronized(pool);
                CompleteCommandPoolChildDestructions(pool);
                CompleteVulkanResourceDestruction(
                    ObjectType.CommandPool,
                    pool.Handle);
            }
        }

        private void DestroyCommandPoolNativeHostSynchronized(CommandPool pool)
        {
            lock (CommandPoolsGate)
                Api!.DestroyCommandPool(_deviceContext.Device, pool, null);
        }

        private void DestroyCommandPool()
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

        private void CreateCommandPool()
        {
            var queueFamilyIndices = _deviceContext.QueueFamilies;
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

        private CommandPool GetThreadCommandPool()
        {
            int threadId = Environment.CurrentManagedThreadId;
            lock (CommandPoolsGate)
            {
                if (ThreadCommandPools.TryGetValue(threadId, out CommandPool pool) && pool.Handle != 0)
                    return pool;
            }

            var queueFamilyIndices = _deviceContext.QueueFamilies;
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

        private CommandPool GetThreadTransferCommandPool()
        {
            int threadId = Environment.CurrentManagedThreadId;
            lock (CommandPoolsGate)
            {
                if (ThreadTransferCommandPools.TryGetValue(threadId, out CommandPool pool) && pool.Handle != 0)
                    return pool;
            }

            var queueFamilyIndices = _deviceContext.QueueFamilies;
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

            ThrowIfVulkanDeviceOperationNotAdmitted("vkCreateCommandPool");
            if (Api!.CreateCommandPool(_deviceContext.Device, ref poolInfo, null, out CommandPool pool) != Result.Success)
                throw new Exception("Failed to create Vulkan command pool.");

            RegisterVulkanResource(
                ObjectType.CommandPool,
                pool.Handle,
                $"CommandPool.QueueFamily.{familyIndex}");

            return pool;
        }

        /// <summary>Creates a sidecar-owned command pool under the renderer lifetime tracker.</summary>
        internal Result CreateVulkanCommandPoolTracked(
            ref CommandPoolCreateInfo createInfo,
            out CommandPool pool,
            string owner)
        {
            pool = default;
            ThrowIfVulkanDeviceOperationNotAdmitted(owner);
            lock (CommandPoolsGate)
            {
                Result result = Api!.CreateCommandPool(_deviceContext.Device, ref createInfo, null, out pool);
                if (result == Result.Success)
                    RegisterVulkanResource(ObjectType.CommandPool, pool.Handle, owner);
                return result;
            }
        }

        /// <summary>Allocates a sidecar command buffer with persistent pool ownership.</summary>
        internal Result AllocateVulkanCommandBufferTracked(
            ref CommandBufferAllocateInfo allocateInfo,
            out CommandBuffer commandBuffer,
            string owner)
            => AllocateVulkanCommandBuffersTracked(ref allocateInfo, out commandBuffer, owner);

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
                    if (!CanResetVulkanCommandBuffer(children[i], out string reason))
                        throw new InvalidOperationException(
                            $"Cannot reset command pool 0x{pool.Handle:X} for {owner}: child command buffer " +
                            $"0x{unchecked((ulong)children[i].Handle):X} is not resettable ({reason}).");

                Result result = Api!.ResetCommandPool(_deviceContext.Device, pool, 0);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResetCommandPoolCall();
                if (result != Result.Success)
                    return result;

                for (int i = 0; i < children.Length; i++)
                    ResetVulkanCommandBufferLifetime(children[i]);
                return result;
            }
        }
    }
}
