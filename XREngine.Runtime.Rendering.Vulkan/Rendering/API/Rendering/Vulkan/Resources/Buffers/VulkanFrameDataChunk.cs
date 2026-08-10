using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>One persistently mapped native buffer for a stream/chunk/frame-slot tuple.</summary>
internal unsafe sealed class VulkanFrameDataChunk(
    Buffer buffer,
    DeviceMemory memory,
    void* mappedPointer,
    ulong capacity,
    ulong allocationLength,
    bool isHostCoherent)
{
    private long _stateToken;

    internal Buffer Buffer { get; } = buffer;
    internal DeviceMemory Memory { get; } = memory;
    internal void* MappedPointer { get; } = mappedPointer;
    internal ulong Capacity { get; } = capacity;
    internal ulong AllocationLength { get; } = allocationLength;
    internal bool IsHostCoherent { get; } = isHostCoherent;
    internal VulkanFrameDataDirtyRanges DirtyRanges;

    internal void InitializeGeneration(ulong generation)
    {
        DirtyRanges.Clear();
        Volatile.Write(ref _stateToken, EncodeState(generation, VulkanFrameDataArenaSlotState.Writable));
    }

    internal VulkanFrameDataArenaSlotState GetState(ulong generation)
    {
        long token = Volatile.Read(ref _stateToken);
        return DecodeGeneration(token) == generation ? DecodeState(token) : VulkanFrameDataArenaSlotState.Invalid;
    }

    internal bool TryTransition(ulong generation, VulkanFrameDataArenaSlotState from, VulkanFrameDataArenaSlotState to)
    {
        long expected = EncodeState(generation, from);
        return Interlocked.CompareExchange(ref _stateToken, EncodeState(generation, to), expected) == expected;
    }

    internal bool PublishSubmitted(ulong generation)
    {
        while (true)
        {
            long observed = Volatile.Read(ref _stateToken);
            if (DecodeGeneration(observed) != generation)
                return false;
            if (DecodeState(observed) == VulkanFrameDataArenaSlotState.Submitted)
                return true;
            if (Interlocked.CompareExchange(ref _stateToken, EncodeState(generation, VulkanFrameDataArenaSlotState.Submitted), observed) == observed)
                return true;
        }
    }

    internal void Destroy(VulkanMappedFrameArenaBackend backend, bool nativeDestroyAllowed)
        => backend.DestroyChunk(Buffer, Memory, MappedPointer, nativeDestroyAllowed);

    private static long EncodeState(ulong generation, VulkanFrameDataArenaSlotState state)
        => unchecked((long)((generation << 2) | (byte)state));

    private static ulong DecodeGeneration(long token) => unchecked((ulong)token) >> 2;
    private static VulkanFrameDataArenaSlotState DecodeState(long token) => (VulkanFrameDataArenaSlotState)(unchecked((ulong)token) & 3UL);
}
