using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanVmaNative
{
    internal const string LibraryName = "VulkanMemoryAllocatorBridge.Native";

    [StructLayout(LayoutKind.Sequential)]
    internal struct AllocatorCreateInfo
    {
        public ulong Instance;
        public ulong PhysicalDevice;
        public ulong Device;
        public uint VulkanApiVersion;
        public uint AllocatorFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AllocationInfo
    {
        public nint Allocation;
        public ulong Memory;
        public ulong Offset;
        public ulong Size;
        public uint MemoryTypeIndex;
        public uint MemoryPropertyFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Budget
    {
        public ulong BlockCount;
        public ulong AllocationCount;
        public ulong BlockBytes;
        public ulong AllocationBytes;
        public ulong Usage;
        public ulong BudgetBytes;
    }

    [DllImport(LibraryName, EntryPoint = "xre_vma_get_version", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint GetVersion();

    [DllImport(LibraryName, EntryPoint = "xre_vma_create_allocator", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result CreateAllocator(ref AllocatorCreateInfo createInfo, out nint allocator);

    [DllImport(LibraryName, EntryPoint = "xre_vma_destroy_allocator", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DestroyAllocator(nint allocator);

    [DllImport(LibraryName, EntryPoint = "xre_vma_allocate_for_buffer", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result AllocateForBuffer(
        nint allocator,
        ulong buffer,
        uint requiredProperties,
        out AllocationInfo allocationInfo);

    [DllImport(LibraryName, EntryPoint = "xre_vma_allocate_for_image", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result AllocateForImage(
        nint allocator,
        ulong image,
        uint requiredProperties,
        out AllocationInfo allocationInfo);

    [DllImport(LibraryName, EntryPoint = "xre_vma_bind_buffer_memory", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result BindBufferMemory(nint allocator, nint allocation, ulong buffer);

    [DllImport(LibraryName, EntryPoint = "xre_vma_bind_image_memory", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result BindImageMemory(nint allocator, nint allocation, ulong image);

    [DllImport(LibraryName, EntryPoint = "xre_vma_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Free(nint allocator, nint allocation);

    [DllImport(LibraryName, EntryPoint = "xre_vma_map_memory", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result MapMemory(nint allocator, nint allocation, out nint data);

    [DllImport(LibraryName, EntryPoint = "xre_vma_unmap_memory", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void UnmapMemory(nint allocator, nint allocation);

    [DllImport(LibraryName, EntryPoint = "xre_vma_flush_allocation", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result FlushAllocation(nint allocator, nint allocation, ulong offset, ulong size);

    [DllImport(LibraryName, EntryPoint = "xre_vma_invalidate_allocation", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result InvalidateAllocation(nint allocator, nint allocation, ulong offset, ulong size);

    [DllImport(LibraryName, EntryPoint = "xre_vma_get_allocation_info", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void GetAllocationInfo(nint allocator, nint allocation, out AllocationInfo allocationInfo);

    [DllImport(LibraryName, EntryPoint = "xre_vma_get_heap_budgets", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint GetHeapBudgets(nint allocator, [Out] Budget[] budgets, uint capacity);

    [DllImport(LibraryName, EntryPoint = "xre_vma_build_stats_string", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint BuildStatsString(nint allocator, int detailedMap);

    [DllImport(LibraryName, EntryPoint = "xre_vma_free_stats_string", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreeStatsString(nint allocator, nint statsString);
}
