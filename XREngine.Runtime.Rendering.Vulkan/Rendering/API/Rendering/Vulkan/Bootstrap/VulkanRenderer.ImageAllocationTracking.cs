using XREngine.Extensions;
using Silk.NET.Vulkan;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        internal void TrackImageAllocation(
            Image image,
            VulkanMemoryAllocation allocation,
            string? name,
            string source,
            uint width,
            uint height,
            uint depth,
            uint layers,
            uint mipLevels,
            Format format,
            ImageUsageFlags usage,
            SampleCountFlags samples)
        {
            if (image.Handle == 0)
                return;

            RegisterVulkanResource(
                ObjectType.Image,
                image.Handle,
                $"{source}:{(string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name)}");

            ResolveImageAllocationDiagnosticFields(
                allocation,
                out uint heapIndex,
                out ulong heapSize,
                out MemoryHeapFlags heapFlags,
                out MemoryPropertyFlags memoryTypeFlags);
            string allocationClass = ClassifyVulkanAllocation(allocation.Properties, allocation.Properties);
            ResourceRuntime.Allocations.Images.DebugInfo[image.Handle] = new VulkanImageAllocationDebugInfo(
                image.Handle,
                string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name!,
                source,
                allocation.Size > long.MaxValue ? long.MaxValue : (long)allocation.Size,
                width,
                height,
                depth,
                layers,
                Math.Max(1u, mipLevels),
                format.ToString(),
                usage.ToString(),
                samples.ToString(),
                allocationClass,
                allocation.MemoryTypeIndex,
                memoryTypeFlags.ToString(),
                heapIndex,
                heapSize,
                heapFlags.ToString());

            RecordImageAllocationDiagnostics(
                image,
                allocation,
                string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name!,
                source,
                width,
                height,
                depth,
                layers,
                mipLevels,
                format,
                usage,
                samples,
                allocationClass,
                heapIndex,
                heapSize,
                heapFlags,
                memoryTypeFlags);
        }

        internal void UntrackImageAllocation(Image image)
        {
            if (image.Handle != 0)
                ResourceRuntime.Allocations.Images.DebugInfo.TryRemove(image.Handle, out _);
        }

        public object GetLiveImageAllocationDiagnostics(int limit)
        {
            int clampedLimit = Math.Clamp(limit, 1, 512);
            var entries = ResourceRuntime.Allocations.Images.DebugInfo.Values.ToArray();
            long knownBytes = 0L;
            for (int i = 0; i < entries.Length; i++)
            {
                long size = entries[i].SizeBytes;
                knownBytes = knownBytes > long.MaxValue - size ? long.MaxValue : knownBytes + size;
            }

            var largest = entries
                .OrderByDescending(static entry => entry.SizeBytes)
                .Take(clampedLimit)
                .Select(static entry => new
                {
                    handle = $"0x{entry.Handle:X}",
                    entry.Name,
                    entry.Source,
                    entry.SizeBytes,
                    sizeMiB = entry.SizeBytes / (1024.0 * 1024.0),
                    entry.Width,
                    entry.Height,
                    entry.Depth,
                    entry.Layers,
                    entry.MipLevels,
                    entry.Format,
                    entry.Usage,
                    entry.Samples,
                    entry.AllocationClass,
                    entry.MemoryTypeIndex,
                    entry.MemoryTypeFlags,
                    entry.MemoryHeapIndex,
                    entry.MemoryHeapSize,
                    entry.MemoryHeapFlags
                })
                .ToArray();

            return new
            {
                allocatorActiveVkAllocations = MemoryAllocator.ActiveVkAllocationCount,
                allocatorTotalAllocatedBytes = MemoryAllocator.TotalAllocatedBytes,
                knownImageAllocationCount = entries.Length,
                knownImageAllocationBytes = knownBytes,
                knownImageAllocationMiB = knownBytes / (1024.0 * 1024.0),
                largest
            };
        }

        private void ResolveImageAllocationDiagnosticFields(
            VulkanMemoryAllocation allocation,
            out uint heapIndex,
            out ulong heapSize,
            out MemoryHeapFlags heapFlags,
            out MemoryPropertyFlags memoryTypeFlags)
        {
            heapIndex = uint.MaxValue;
            heapSize = 0UL;
            heapFlags = 0;
            memoryTypeFlags = 0;

            if (Api is null || _deviceContext.PhysicalDevice.Handle == 0)
                return;

            Api.GetPhysicalDeviceMemoryProperties(_deviceContext.PhysicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
            if (allocation.MemoryTypeIndex >= memoryProperties.MemoryTypeCount)
                return;

            MemoryType memoryType = memoryProperties.MemoryTypes[(int)allocation.MemoryTypeIndex];
            heapIndex = memoryType.HeapIndex;
            memoryTypeFlags = memoryType.PropertyFlags;
            if (heapIndex >= memoryProperties.MemoryHeapCount)
                return;

            MemoryHeap heap = memoryProperties.MemoryHeaps[(int)heapIndex];
            heapSize = heap.Size;
            heapFlags = heap.Flags;
        }

        private void RecordImageAllocationDiagnostics(
            Image image,
            VulkanMemoryAllocation allocation,
            string name,
            string source,
            uint width,
            uint height,
            uint depth,
            uint layers,
            uint mipLevels,
            Format format,
            ImageUsageFlags usage,
            SampleCountFlags samples,
            string allocationClass,
            uint heapIndex,
            ulong heapSize,
            MemoryHeapFlags heapFlags,
            MemoryPropertyFlags memoryTypeFlags)
        {
            if (!RenderDiagnosticsFlags.UploadStageLogging &&
                !RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging)
            {
                return;
            }

            Debug.Vulkan(
                "[VkImageAllocation] image=0x{0:X} name='{1}' source={2} allocationClass={3} memoryHeap={4} heapSize={5} heapFlags={6} memoryType={7} memoryTypeFlags={8} size={9} extent={10}x{11}x{12} layers={13} mips={14} format={15} usage={16} samples={17} activeVkAllocations={18} allocatorBytes={19}.",
                image.Handle,
                name,
                source,
                allocationClass,
                heapIndex,
                heapSize,
                heapFlags,
                allocation.MemoryTypeIndex,
                memoryTypeFlags,
                allocation.Size,
                width,
                height,
                depth,
                layers,
                Math.Max(1u, mipLevels),
                format,
                usage,
                samples,
                MemoryAllocator.ActiveVkAllocationCount,
                MemoryAllocator.TotalAllocatedBytes);
        }

        /// <summary>
        /// Returns the suballocation offset for a tracked buffer, or 0 if untracked (legacy).
        /// Use when mapping memory for a buffer that was allocated through the allocator.
        /// </summary>
    }
}
