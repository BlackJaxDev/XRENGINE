using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan.DeviceBootstrap;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow native resource owner used by <see cref="VulkanMappedFrameArena"/>. It owns only
/// dedicated host-visible Vulkan allocations and does not retain a renderer reference.
/// </summary>
internal unsafe sealed class VulkanMappedFrameArenaBackend(
    Vk api,
    PhysicalDevice physicalDevice,
    Device device,
    VulkanDeviceContext deviceContext,
    VulkanBufferResourceManager resourceManager,
    ulong nonCoherentAtomSize)
{
    private readonly Vk _api = api;
    private readonly PhysicalDevice _physicalDevice = physicalDevice;
    private readonly Device _device = device;
    private readonly VulkanDeviceContext _deviceContext = deviceContext;
    private readonly VulkanBufferResourceManager _resourceManager = resourceManager;

    internal ulong NonCoherentAtomSize { get; } = Math.Max(nonCoherentAtomSize, 1UL);
    internal bool IsOperational => _deviceContext.IsOperational;

    internal bool TryCreateChunk(
        ulong capacity,
        out Buffer buffer,
        out DeviceMemory memory,
        out void* mappedPtr,
        out bool isHostCoherent,
        out ulong allocationLength)
    {
        buffer = default;
        memory = default;
        mappedPtr = null;
        isHostCoherent = false;
        allocationLength = 0;
        if (!_deviceContext.IsOperational)
            return false;
        bool registered = false;

        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = Math.Max(capacity, 1UL),
            Usage = BufferUsageFlags.UniformBufferBit,
            SharingMode = SharingMode.Exclusive,
        };
        if (!_deviceContext.IsOperational)
            return false;
        Result createBufferResult = _api.CreateBuffer(
            _device,
            ref bufferInfo,
            null,
            out buffer);
        ObserveResult("vkCreateBuffer.MappedFrameArena", createBufferResult);
        if (createBufferResult != Result.Success)
            return false;

        try
        {
            MemoryRequirements requirements = _api.GetBufferMemoryRequirements(_device, buffer);
            if (!TryResolveMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out uint memoryTypeIndex,
                    out MemoryPropertyFlags properties) &&
                !TryResolveMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.HostVisibleBit,
                    out memoryTypeIndex,
                    out properties))
            {
                return false;
            }

            MemoryAllocateInfo allocateInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
            };
            if (!_deviceContext.IsOperational)
                return false;
            Result allocateResult = _api.AllocateMemory(
                _device,
                ref allocateInfo,
                null,
                out memory);
            ObserveResult("vkAllocateMemory.MappedFrameArena", allocateResult);
            if (allocateResult != Result.Success)
                return false;

            if (!_deviceContext.IsOperational)
                return false;
            Result bindResult = _api.BindBufferMemory(
                _device,
                buffer,
                memory,
                0);
            ObserveResult("vkBindBufferMemory.MappedFrameArena", bindResult);
            if (bindResult != Result.Success)
                return false;

            void* localMappedPtr = null;
            if (!_deviceContext.IsOperational)
                return false;
            Result mapResult = _api.MapMemory(
                    _device,
                    memory,
                    0,
                    allocateInfo.AllocationSize,
                    0,
                    &localMappedPtr);
            ObserveResult("vkMapMemory.MappedFrameArena", mapResult);
            if (mapResult != Result.Success || localMappedPtr is null)
                return false;

            mappedPtr = localMappedPtr;
            isHostCoherent = (properties & MemoryPropertyFlags.HostCoherentBit) != 0;
            allocationLength = allocateInfo.AllocationSize;
            _resourceManager.RegisterMappedFrameArenaChunk(
                buffer,
                new VulkanMemoryAllocation(
                    memory,
                    0,
                    allocationLength,
                    memoryTypeIndex,
                    properties,
                    BlockId: -1,
                    MappedData: (nint)mappedPtr));
            registered = true;
            return true;
        }
        finally
        {
            if (!registered)
            {
                if (mappedPtr is not null && memory.Handle != 0 && _deviceContext.IsOperational)
                    _api.UnmapMemory(_device, memory);
                if (memory.Handle != 0)
                {
                    if (_deviceContext.IsOperational)
                        _api.FreeMemory(_device, memory, null);
                }
                if (buffer.Handle != 0 && _deviceContext.IsOperational)
                    _api.DestroyBuffer(_device, buffer, null);
                buffer = default;
                memory = default;
                mappedPtr = null;
                allocationLength = 0;
            }
        }
    }

    /// <summary>
    /// Establishes the sole supported destruction proof: every submitted generation is complete
    /// because the logical device is idle. Device-loss teardown intentionally skips individual
    /// native destroy calls; logical-device destruction reclaims those handles.
    /// </summary>
    internal bool TryEnterIdleTeardown()
    {
        if (!_deviceContext.IsOperational)
            return false;

        Result result = _api.DeviceWaitIdle(_device);
        ObserveResult("vkDeviceWaitIdle.MappedFrameArena", result);
        return result == Result.Success;
    }

    internal void DestroyChunk(
        Buffer buffer,
        DeviceMemory memory,
        void* mappedPtr,
        bool nativeDestroyAllowed)
    {
        _resourceManager.TryUnregisterMappedFrameArenaChunk(buffer, out _);
        if (!nativeDestroyAllowed || !_deviceContext.IsOperational)
            return;

        if (mappedPtr is not null && memory.Handle != 0)
            _api.UnmapMemory(_device, memory);
        if (buffer.Handle != 0)
            _api.DestroyBuffer(_device, buffer, null);
        if (memory.Handle != 0)
            _api.FreeMemory(_device, memory, null);
    }

    internal void Flush(DeviceMemory memory, ulong offset, ulong length)
    {
        if (!_deviceContext.IsOperational)
            throw new InvalidOperationException("Cannot flush mapped frame-arena memory after Vulkan device admission closed.");

        MappedMemoryRange range = new()
        {
            SType = StructureType.MappedMemoryRange,
            Memory = memory,
            Offset = offset,
            Size = length,
        };
        Result result = _api.FlushMappedMemoryRanges(_device, 1, ref range);
        ObserveResult("vkFlushMappedMemoryRanges.MappedFrameArena", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to flush a non-coherent mapped frame-arena range ({result}).");
    }

    internal void Invalidate(DeviceMemory memory, ulong offset, ulong length)
    {
        if (!_deviceContext.IsOperational)
            throw new InvalidOperationException("Cannot invalidate mapped frame-arena memory after Vulkan device admission closed.");

        MappedMemoryRange range = new()
        {
            SType = StructureType.MappedMemoryRange,
            Memory = memory,
            Offset = offset,
            Size = length,
        };
        Result result = _api.InvalidateMappedMemoryRanges(_device, 1, ref range);
        ObserveResult("vkInvalidateMappedMemoryRanges.MappedFrameArena", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to invalidate a non-coherent mapped frame-arena range ({result}).");
    }

    private void ObserveResult(string operation, Result result)
        => _deviceContext.ObserveNativeResult(operation, result);

    private bool TryResolveMemoryType(
        uint typeBits,
        MemoryPropertyFlags requiredProperties,
        out uint memoryTypeIndex,
        out MemoryPropertyFlags properties)
    {
        _api.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
        for (uint index = 0; index < memoryProperties.MemoryTypeCount; index++)
        {
            if ((typeBits & (1u << (int)index)) == 0)
                continue;
            MemoryPropertyFlags candidate = memoryProperties.MemoryTypes[(int)index].PropertyFlags;
            if ((candidate & requiredProperties) != requiredProperties)
                continue;

            memoryTypeIndex = index;
            properties = candidate;
            return true;
        }

        memoryTypeIndex = 0;
        properties = default;
        return false;
    }
}
