using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Native Vulkan Memory Allocator backend accessed through the XRE VMA bridge.
/// </summary>
internal sealed unsafe class VulkanVmaAllocator : IVulkanMemoryAllocator
{
    private const uint AllocatorCreateBufferDeviceAddressBit = 0x00000020;
    private const int VmaBlockId = -2;
    private const int MaxMemoryHeaps = 16;

    private nint _allocator;
    private int _activeAllocationCount;
    private long _totalAllocatedBytes;
    private bool _disposed;
    private readonly ConcurrentDictionary<nint, int> _mapCounts = new();
    private readonly object _mapCountsGate = new();
    private readonly VulkanVmaNative.Budget[] _heapBudgetScratch = new VulkanVmaNative.Budget[MaxMemoryHeaps];

    public VulkanVmaAllocator(
        Instance instance,
        PhysicalDevice physicalDevice,
        Device device,
        uint vulkanApiVersion,
        bool enableBufferDeviceAddress)
    {
        uint allocatorFlags = enableBufferDeviceAddress
            ? AllocatorCreateBufferDeviceAddressBit
            : 0u;

        VulkanVmaNative.AllocatorCreateInfo createInfo = new()
        {
            Instance = ToUInt64(instance.Handle),
            PhysicalDevice = ToUInt64(physicalDevice.Handle),
            Device = ToUInt64(device.Handle),
            VulkanApiVersion = vulkanApiVersion,
            AllocatorFlags = allocatorFlags
        };

        try
        {
            uint bridgeVersion = VulkanVmaNative.GetVersion();
            Result result = VulkanVmaNative.CreateAllocator(ref createInfo, out _allocator);
            if (result != Result.Success || _allocator == 0)
                throw new InvalidOperationException($"VMA allocator creation failed ({result}).");

            string expectedDllPath = Path.Combine(AppContext.BaseDirectory, $"{VulkanVmaNative.LibraryName}.dll");
            Debug.Vulkan(
                $"[Vulkan] VMA allocator initialized: version={FormatVersion(bridgeVersion)} flags=0x{allocatorFlags:X8} " +
                $"bufferDeviceAddress={enableBufferDeviceAddress} dll='{expectedDllPath}'.");
        }
        catch (Exception ex) when (IsNativeBridgeException(ex))
        {
            throw CreateBridgeUnavailableException(ex);
        }
    }

    public int ActiveVkAllocationCount => _activeAllocationCount;
    public long TotalAllocatedBytes => _totalAllocatedBytes;

    public VulkanMemoryAllocation AllocateForBuffer(
        Vk api, Device device, Buffer buffer, MemoryPropertyFlags requiredProperties)
    {
        if (!TryAllocateForBuffer(api, device, buffer, requiredProperties, out VulkanMemoryAllocation allocation))
            throw new VulkanOutOfMemoryException("Failed to allocate Vulkan buffer memory through VMA.", requiredProperties);
        return allocation;
    }

    public VulkanMemoryAllocation AllocateForImage(
        Vk api, Device device, Image image, MemoryPropertyFlags requiredProperties)
    {
        if (!TryAllocateForImage(api, device, image, requiredProperties, out VulkanMemoryAllocation allocation))
            throw new VulkanOutOfMemoryException("Failed to allocate Vulkan image memory through VMA.", requiredProperties);
        return allocation;
    }

    public bool TryAllocateForBuffer(
        Vk api, Device device, Buffer buffer,
        MemoryPropertyFlags requiredProperties,
        out VulkanMemoryAllocation allocation)
    {
        ThrowIfDisposed();
        allocation = VulkanMemoryAllocation.Null;

        try
        {
            Result result = VulkanVmaNative.AllocateForBuffer(
                _allocator,
                ToUInt64(buffer.Handle),
                (uint)requiredProperties,
                out VulkanVmaNative.AllocationInfo nativeAllocation);

            return TryCreateManagedAllocation(result, requiredProperties, nativeAllocation, out allocation);
        }
        catch (Exception ex) when (IsNativeBridgeException(ex))
        {
            throw CreateBridgeUnavailableException(ex);
        }
    }

    public bool TryAllocateForImage(
        Vk api, Device device, Image image,
        MemoryPropertyFlags requiredProperties,
        out VulkanMemoryAllocation allocation)
    {
        ThrowIfDisposed();
        allocation = VulkanMemoryAllocation.Null;

        try
        {
            Result result = VulkanVmaNative.AllocateForImage(
                _allocator,
                ToUInt64(image.Handle),
                (uint)requiredProperties,
                out VulkanVmaNative.AllocationInfo nativeAllocation);

            return TryCreateManagedAllocation(result, requiredProperties, nativeAllocation, out allocation);
        }
        catch (Exception ex) when (IsNativeBridgeException(ex))
        {
            throw CreateBridgeUnavailableException(ex);
        }
    }

    public void Free(Vk api, Device device, VulkanMemoryAllocation allocation)
    {
        if (allocation.IsNull)
            return;

        if (allocation.NativeAllocation == 0)
        {
            api.FreeMemory(device, allocation.Memory, null);
            return;
        }

        try
        {
            lock (_mapCountsGate)
            {
                ThrowIfDisposed();
                DrainMappedAllocation_NoLock(allocation.NativeAllocation);
                VulkanVmaNative.Free(_allocator, allocation.NativeAllocation);
            }
        }
        catch (Exception ex) when (IsNativeBridgeException(ex))
        {
            throw CreateBridgeUnavailableException(ex);
        }

        Interlocked.Decrement(ref _activeAllocationCount);
        Interlocked.Add(ref _totalAllocatedBytes, -ClampToLong(allocation.Size));
    }

    public bool TryMap(
        Vk api,
        Device device,
        VulkanMemoryAllocation allocation,
        ulong offset,
        ulong length,
        out void* mappedPtr)
    {
        mappedPtr = null;
        if (allocation.IsNull)
            return false;

        if (allocation.NativeAllocation == 0)
        {
            void* localPtr = null;
            Result rawResult = api.MapMemory(device, allocation.Memory, allocation.Offset + offset, length, 0, &localPtr);
            if (rawResult != Result.Success)
                return false;

            mappedPtr = localPtr;
            return true;
        }

        if (allocation.MappedData != 0)
        {
            mappedPtr = (byte*)allocation.MappedData + offset;
            return true;
        }

        try
        {
            lock (_mapCountsGate)
            {
                ThrowIfDisposed();

                Result result = VulkanVmaNative.MapMemory(_allocator, allocation.NativeAllocation, out nint allocationPtr);
                if (result != Result.Success || allocationPtr == 0)
                {
                    if (result == Result.Success)
                        VulkanVmaNative.UnmapMemory(_allocator, allocation.NativeAllocation);
                    Debug.VulkanWarningEvery(
                        $"Vulkan.VMA.MapFailed.{allocation.NativeAllocation}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] VMA map failed. Result={0} Allocation=0x{1:X} Memory=0x{2:X} AllocationOffset={3} RelativeOffset={4} Length={5} Size={6} Properties={7}.",
                        result,
                        allocation.NativeAllocation,
                        allocation.Memory.Handle,
                        allocation.Offset,
                        offset,
                        length,
                        allocation.Size,
                        allocation.Properties);
                    return false;
                }

                _mapCounts.AddOrUpdate(allocation.NativeAllocation, 1, static (_, count) => count + 1);
                mappedPtr = (byte*)allocationPtr + offset;
                return true;
            }
        }
        catch (Exception ex) when (IsNativeBridgeException(ex))
        {
            throw CreateBridgeUnavailableException(ex);
        }
    }

    public void Unmap(Vk api, Device device, VulkanMemoryAllocation allocation)
    {
        if (allocation.IsNull)
            return;

        if (allocation.NativeAllocation == 0)
        {
            api.UnmapMemory(device, allocation.Memory);
            return;
        }

        if (allocation.MappedData != 0)
            return;

        try
        {
            lock (_mapCountsGate)
            {
                ThrowIfDisposed();

                if (_mapCounts.TryGetValue(allocation.NativeAllocation, out int count))
                {
                    if (count <= 1)
                        _mapCounts.TryRemove(allocation.NativeAllocation, out _);
                    else
                        _mapCounts[allocation.NativeAllocation] = count - 1;
                }
                else
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.VMA.UnmapUntracked.{allocation.NativeAllocation}",
                        TimeSpan.FromSeconds(5),
                        "[Vulkan] VMA unmap requested for allocation 0x{0:X} without a tracked map count.",
                        allocation.NativeAllocation);
                    return;
                }

                VulkanVmaNative.UnmapMemory(_allocator, allocation.NativeAllocation);
            }
        }
        catch (Exception ex) when (IsNativeBridgeException(ex))
        {
            throw CreateBridgeUnavailableException(ex);
        }
    }

    public string? BuildStatsString(bool detailedMap)
    {
        ThrowIfDisposed();

        nint statsPtr = VulkanVmaNative.BuildStatsString(_allocator, detailedMap ? 1 : 0);
        if (statsPtr == 0)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(statsPtr);
        }
        finally
        {
            VulkanVmaNative.FreeStatsString(_allocator, statsPtr);
        }
    }

    public bool TryGetDeviceLocalHeapBudgetSnapshot(
        in PhysicalDeviceMemoryProperties memoryProperties,
        double budgetRatio,
        long reserveBytes,
        out long allocatedBytes,
        out long budgetBytes,
        out long largestHeapBytes)
    {
        allocatedBytes = 0L;
        budgetBytes = 0L;
        largestHeapBytes = 0L;

        if (memoryProperties.MemoryHeapCount == 0)
            return false;

        lock (_mapCountsGate)
        {
            ThrowIfDisposed();

            Array.Clear(_heapBudgetScratch, 0, _heapBudgetScratch.Length);
            uint budgetCount = VulkanVmaNative.GetHeapBudgets(
                _allocator,
                _heapBudgetScratch,
                (uint)_heapBudgetScratch.Length);
            if (budgetCount == 0)
                return false;

            uint heapCount = Math.Min(
                Math.Min(budgetCount, memoryProperties.MemoryHeapCount),
                (uint)_heapBudgetScratch.Length);
            if (heapCount == 0)
                return false;

            long selectedUsageBytes = 0L;
            long selectedBudgetBytes = 0L;
            long selectedLargestHeapBytes = 0L;
            bool selectedAnyDeviceLocal = false;

            for (uint i = 0; i < heapCount; i++)
            {
                MemoryHeap heap = memoryProperties.MemoryHeaps[(int)i];
                if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) == 0)
                    continue;

                VulkanVmaNative.Budget budget = _heapBudgetScratch[i];
                selectedAnyDeviceLocal = true;
                selectedLargestHeapBytes = Math.Max(selectedLargestHeapBytes, ClampToLong(heap.Size));
                selectedUsageBytes = SaturatingAdd(selectedUsageBytes, ResolveHeapUsageBytes(budget));
                selectedBudgetBytes = SaturatingAdd(selectedBudgetBytes, ResolveHeapBudgetBytes(budget, heap.Size));
            }

            if (!selectedAnyDeviceLocal)
            {
                for (uint i = 0; i < heapCount; i++)
                {
                    MemoryHeap heap = memoryProperties.MemoryHeaps[(int)i];
                    VulkanVmaNative.Budget budget = _heapBudgetScratch[i];
                    selectedLargestHeapBytes = Math.Max(selectedLargestHeapBytes, ClampToLong(heap.Size));
                    selectedUsageBytes = SaturatingAdd(selectedUsageBytes, ResolveHeapUsageBytes(budget));
                    selectedBudgetBytes = SaturatingAdd(selectedBudgetBytes, ResolveHeapBudgetBytes(budget, heap.Size));
                }
            }

            if (selectedLargestHeapBytes <= 0L)
                return false;

            long rawBudgetBytes = selectedBudgetBytes > 0L ? selectedBudgetBytes : selectedLargestHeapBytes;
            double clampedRatio = Math.Clamp(budgetRatio, 0.1, 1.0);
            long ratioLimitBytes = (long)Math.Floor(rawBudgetBytes * clampedRatio);
            long reserveLimitBytes = rawBudgetBytes > reserveBytes
                ? rawBudgetBytes - Math.Max(0L, reserveBytes)
                : rawBudgetBytes;

            allocatedBytes = selectedUsageBytes;
            budgetBytes = Math.Max(0L, Math.Min(ratioLimitBytes, reserveLimitBytes));
            largestHeapBytes = selectedLargestHeapBytes;
            return budgetBytes > 0L;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_allocator != 0)
        {
            lock (_mapCountsGate)
            {
                DrainAllMappedAllocations_NoLock();
                int activeAllocations = Volatile.Read(ref _activeAllocationCount);
                if (activeAllocations != 0)
                {
                    Debug.VulkanWarning(
                        "[Vulkan] Skipping VMA allocator destruction because {0} allocation(s) remain live ({1} bytes tracked). The process is shutting down after an unrecoverable Vulkan resource leak/device-loss path.",
                        activeAllocations,
                        Volatile.Read(ref _totalAllocatedBytes));
                    _allocator = 0;
                    return;
                }

                VulkanVmaNative.DestroyAllocator(_allocator);
                _allocator = 0;
            }
        }
    }

    private void DrainMappedAllocation(nint allocation)
    {
        lock (_mapCountsGate)
            DrainMappedAllocation_NoLock(allocation);
    }

    private void DrainMappedAllocation_NoLock(nint allocation)
    {
        if (allocation == 0 || !_mapCounts.TryRemove(allocation, out int mapCount))
            return;

        for (int i = 0; i < mapCount; i++)
            VulkanVmaNative.UnmapMemory(_allocator, allocation);

        Debug.VulkanWarning(
            $"[Vulkan] Unmapped VMA allocation 0x{allocation:X} {mapCount} time(s) during free; caller leaked a map scope.");
    }

    private void DrainAllMappedAllocations()
    {
        lock (_mapCountsGate)
            DrainAllMappedAllocations_NoLock();
    }

    private void DrainAllMappedAllocations_NoLock()
    {
        foreach (nint allocation in _mapCounts.Keys)
            DrainMappedAllocation_NoLock(allocation);
    }

    private bool TryCreateManagedAllocation(
        Result result,
        MemoryPropertyFlags requiredProperties,
        VulkanVmaNative.AllocationInfo nativeAllocation,
        out VulkanMemoryAllocation allocation)
    {
        allocation = VulkanMemoryAllocation.Null;

        if (result == Result.ErrorOutOfDeviceMemory ||
            result == Result.ErrorOutOfHostMemory ||
            result == Result.ErrorFeatureNotPresent)
        {
            return false;
        }

        if (result != Result.Success)
            throw new InvalidOperationException($"VMA allocation failed ({result}). Requested={requiredProperties}.");

        if (nativeAllocation.Allocation == 0 || nativeAllocation.Memory == 0)
            throw new InvalidOperationException("VMA allocation succeeded without returning memory handles.");

        MemoryPropertyFlags properties = nativeAllocation.MemoryPropertyFlags == 0
            ? requiredProperties
            : (MemoryPropertyFlags)nativeAllocation.MemoryPropertyFlags;

        allocation = new VulkanMemoryAllocation(
            Memory: new DeviceMemory { Handle = nativeAllocation.Memory },
            Offset: nativeAllocation.Offset,
            Size: nativeAllocation.Size,
            MemoryTypeIndex: nativeAllocation.MemoryTypeIndex,
            Properties: properties,
            BlockId: VmaBlockId,
            NativeAllocation: nativeAllocation.Allocation,
            MappedData: nativeAllocation.MappedData);

        Interlocked.Increment(ref _activeAllocationCount);
        Interlocked.Add(ref _totalAllocatedBytes, ClampToLong(nativeAllocation.Size));
        return true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed || _allocator == 0)
            throw new ObjectDisposedException(nameof(VulkanVmaAllocator));
    }

    private static bool IsNativeBridgeException(Exception ex)
        => ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static NotSupportedException CreateBridgeUnavailableException(Exception innerException)
        => new(
            "The VMA allocator backend requires VulkanMemoryAllocatorBridge.Native.dll in the runtime native output directory. " +
            "Build XREngine.Runtime.Rendering on Windows with VULKAN_SDK set, or select the Managed allocator backend.",
            innerException);

    private static string FormatVersion(uint version)
        => $"{(version >> 16) & 0xFF}.{(version >> 8) & 0xFF}.{version & 0xFF}";

    private static ulong ToUInt64(nint handle)
        => unchecked((ulong)handle);

    private static ulong ToUInt64(ulong handle)
        => handle;

    private static long ClampToLong(ulong value)
        => value > long.MaxValue ? long.MaxValue : (long)value;

    private static long ResolveHeapUsageBytes(VulkanVmaNative.Budget budget)
        => ClampToLong(budget.Usage != 0 ? budget.Usage : budget.BlockBytes);

    private static long ResolveHeapBudgetBytes(VulkanVmaNative.Budget budget, ulong fallbackHeapSize)
        => ClampToLong(budget.BudgetBytes != 0 ? budget.BudgetBytes : fallbackHeapSize);

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0L)
            return left;
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }
}
