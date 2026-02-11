using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private CommandPool commandPool;
        private readonly object _commandPoolsLock = new();
        private readonly Dictionary<int, CommandPool> _threadCommandPools = new();

        private void DestroyCommandPool()
        {
            lock (_commandPoolsLock)
            {
                HashSet<ulong> destroyed = [];
                foreach (CommandPool pool in _threadCommandPools.Values)
                {
                    if (pool.Handle == 0 || !destroyed.Add(pool.Handle))
                        continue;

                    Api!.DestroyCommandPool(device, pool, null);
                }

                _threadCommandPools.Clear();
                commandPool = default;
            }
        }

        private void CreateCommandPool()
        {
            var queueFamilyIndices = FamilyQueueIndices;
            uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");

            CommandPool primaryPool = CreateCommandPoolForFamily(graphicsFamily);

            lock (_commandPoolsLock)
            {
                commandPool = primaryPool;
                _threadCommandPools[Environment.CurrentManagedThreadId] = primaryPool;
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
                    Api!.DestroyCommandPool(device, created, null);
                    return existing;
                }

                _threadCommandPools[threadId] = created;
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

            if (Api!.CreateCommandPool(device, ref poolInfo, null, out CommandPool pool) != Result.Success)
                throw new Exception("Failed to create Vulkan command pool.");

            return pool;
        }
    }
}
