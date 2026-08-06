using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private CommandPool commandPool;
        private CommandPool transferCommandPool;
        private readonly object _commandPoolsLock = new();
        private readonly Dictionary<int, CommandPool> _threadCommandPools = new();
        private readonly Dictionary<int, CommandPool> _threadTransferCommandPools = new();

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

            lock (_commandPoolsLock)
                return Api!.AllocateCommandBuffers(device, ref allocateInfo, commandBuffers);
        }

        private void FreeCommandBuffersHostSynchronized(
            CommandPool pool,
            uint commandBufferCount,
            CommandBuffer* commandBuffers)
        {
            lock (_commandPoolsLock)
                Api!.FreeCommandBuffers(device, pool, commandBufferCount, commandBuffers);
        }

        internal void DestroyCommandPoolHostSynchronized(CommandPool pool)
        {
            if (pool.Handle == 0)
                return;

            // Allocation, explicit free, and pool retirement share this lock. The
            // pool-child ownership registration must be atomic with pool retirement
            // so a just-allocated cached command buffer cannot escape the retire set.
            lock (_commandPoolsLock)
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
            lock (_commandPoolsLock)
                Api!.DestroyCommandPool(device, pool, null);
        }

        private void DestroyCommandPool()
        {
            DestroyCommandChainRecordingWorkers();

            lock (_commandPoolsLock)
            {
                HashSet<ulong> destroyed = [];
                foreach (CommandPool pool in _threadCommandPools.Values)
                    if (pool.Handle != 0 && destroyed.Add(pool.Handle))
                        DestroyCommandPoolHostSynchronized(pool);

                foreach (CommandPool pool in _threadTransferCommandPools.Values)
                    if (pool.Handle != 0 && destroyed.Add(pool.Handle))
                        DestroyCommandPoolHostSynchronized(pool);

                _threadCommandPools.Clear();
                _threadTransferCommandPools.Clear();
                commandPool = default;
                transferCommandPool = default;
            }
        }

        private void CreateCommandPool()
        {
            var queueFamilyIndices = FamilyQueueIndices;
            uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");
            uint transferFamily = queueFamilyIndices.TransferFamilyIndex ?? graphicsFamily;

            CommandPool primaryPool = CreateCommandPoolForFamily(graphicsFamily);
            CommandPool primaryTransferPool = transferFamily == graphicsFamily
                ? primaryPool
                : CreateCommandPoolForFamily(transferFamily);

            lock (_commandPoolsLock)
            {
                commandPool = primaryPool;
                transferCommandPool = primaryTransferPool;
                _threadCommandPools[Environment.CurrentManagedThreadId] = primaryPool;
                _threadTransferCommandPools[Environment.CurrentManagedThreadId] = primaryTransferPool;
            }
        }

        private CommandPool GetThreadCommandPool()
        {
            int threadId = Environment.CurrentManagedThreadId;
            lock (_commandPoolsLock)
            {
                if (_threadCommandPools.TryGetValue(threadId, out CommandPool pool) && pool.Handle != 0)
                    return pool;
            }

            var queueFamilyIndices = FamilyQueueIndices;
            uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");

            CommandPool created = CreateCommandPoolForFamily(graphicsFamily);

            lock (_commandPoolsLock)
            {
                if (_threadCommandPools.TryGetValue(threadId, out CommandPool existing) && existing.Handle != 0)
                {
                    // Another thread raced to create for this id; keep existing and dispose duplicate.
                    DestroyCommandPoolHostSynchronized(created);
                    return existing;
                }

                _threadCommandPools[threadId] = created;
                return created;
            }
        }

        private CommandPool GetThreadTransferCommandPool()
        {
            int threadId = Environment.CurrentManagedThreadId;
            lock (_commandPoolsLock)
            {
                if (_threadTransferCommandPools.TryGetValue(threadId, out CommandPool pool) && pool.Handle != 0)
                    return pool;
            }

            var queueFamilyIndices = FamilyQueueIndices;
            uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");
            uint transferFamily = queueFamilyIndices.TransferFamilyIndex ?? graphicsFamily;

            CommandPool created = transferFamily == graphicsFamily
                ? GetThreadCommandPool()
                : CreateCommandPoolForFamily(transferFamily);

            lock (_commandPoolsLock)
            {
                if (_threadTransferCommandPools.TryGetValue(threadId, out CommandPool existing) && existing.Handle != 0)
                {
                    if (transferFamily != graphicsFamily && created.Handle != existing.Handle)
                        DestroyCommandPoolHostSynchronized(created);
                    return existing;
                }

                _threadTransferCommandPools[threadId] = created;
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
            if (Api!.CreateCommandPool(device, ref poolInfo, null, out CommandPool pool) != Result.Success)
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
            lock (_commandPoolsLock)
            {
                Result result = Api!.CreateCommandPool(device, ref createInfo, null, out pool);
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

            lock (_commandPoolsLock)
            {
                VulkanResourceLifetimeKey poolKey = ResourceKey(ObjectType.CommandPool, pool.Handle);
                CommandBuffer[] children;
                lock (_resourceLifetimeTracker.SyncRoot)
                {
                    if (!_resourceLifetimeTracker.CommandBuffersByPool.TryGetValue(poolKey, out HashSet<ulong>? ownedChildren) ||
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

                Result result = Api!.ResetCommandPool(device, pool, 0);
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
