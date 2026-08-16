using ImGuiNET;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the per-slot host-visible ImGui vertex and index buffers for one output.
/// Buffer replacement is retired through <see cref="VulkanBufferResourceService"/>
/// instead of reaching back into the renderer.
/// A resource instance must not be shared by independent WSI outputs: detached
/// viewport frame-slot indices are local and can overlap desktop image indices.
/// </summary>
internal unsafe sealed class VulkanImGuiDrawBufferResources(
    VulkanResourceRuntime resourceRuntime,
    VulkanTargetOutputContext target)
{
    private readonly VulkanResourceRuntime _resourceRuntime = resourceRuntime;
    private readonly VulkanTargetOutputContext _target = target;
    private VulkanImGuiDrawBufferSet[] _drawBuffers = [];

    internal ref VulkanImGuiDrawBufferSet Ensure(uint imageIndex, ulong vertexBytes, ulong indexBytes)
    {
        int slot = EnsureSlot(imageIndex);
        ref VulkanImGuiDrawBufferSet buffers = ref _drawBuffers[slot];
        VulkanTargetOutputContext target = _target;
        EnsureBuffer(
            target,
            ref buffers.VertexBuffer,
            ref buffers.VertexBufferMemory,
            ref buffers.VertexBufferSize,
            Math.Max(vertexBytes, 1UL),
            BufferUsageFlags.VertexBufferBit,
            "ImGui.Draw.VertexBuffer");
        EnsureBuffer(
            target,
            ref buffers.IndexBuffer,
            ref buffers.IndexBufferMemory,
            ref buffers.IndexBufferSize,
            Math.Max(indexBytes, 1UL),
            BufferUsageFlags.IndexBufferBit,
            "ImGui.Draw.IndexBuffer");
        return ref buffers;
    }

    internal void Upload(
        in VulkanImGuiFrameSnapshot snapshot,
        ref VulkanImGuiDrawBufferSet buffers)
    {
        ulong vertexBytes = checked((ulong)snapshot.TotalVertexCount * (ulong)sizeof(ImDrawVert));
        ulong indexBytes = checked((ulong)snapshot.TotalIndexCount * sizeof(ushort));
        if (!_target.TryWriteMappedMemory(
                ResolveBufferAllocation(buffers.VertexBuffer),
                0,
                Math.Max(vertexBytes, 1UL),
                snapshot,
                static (destination, state) => CopySnapshotVertices(in state, destination)))
            throw new InvalidOperationException("Failed to map ImGui vertex-buffer memory.");

        if (!_target.TryWriteMappedMemory(
                ResolveBufferAllocation(buffers.IndexBuffer),
                0,
                Math.Max(indexBytes, 1UL),
                snapshot,
                static (destination, state) => CopySnapshotIndices(in state, destination)))
            throw new InvalidOperationException("Failed to map ImGui index-buffer memory.");
    }

    /// <summary>Queues every generation-owned draw buffer for exact retirement.</summary>
    internal void RetireAll()
    {
        VulkanImGuiDrawBufferSet[] buffers = _drawBuffers;
        for (int index = 0; index < buffers.Length; index++)
        {
            ref VulkanImGuiDrawBufferSet set = ref buffers[index];
            if (set.VertexBuffer.Handle != 0)
                _resourceRuntime.Buffers.Retire(set.VertexBuffer, set.VertexBufferMemory, "ImGui.Draw.VertexBuffer");
            if (set.IndexBuffer.Handle != 0)
                _resourceRuntime.Buffers.Retire(set.IndexBuffer, set.IndexBufferMemory, "ImGui.Draw.IndexBuffer");
        }
        _drawBuffers = [];
    }

    private int EnsureSlot(uint imageIndex)
    {
        int slot = checked((int)imageIndex);
        int required = checked(slot + 1);
        if (_drawBuffers.Length < required)
            Array.Resize(ref _drawBuffers, required);
        return slot;
    }

    private void EnsureBuffer(
        VulkanTargetOutputContext target,
        ref Buffer buffer,
        ref DeviceMemory memory,
        ref ulong currentCapacity,
        ulong requiredCapacity,
        BufferUsageFlags usage,
        string owner)
    {
        if (buffer.Handle != 0 && currentCapacity >= requiredCapacity)
            return;

        if (buffer.Handle != 0)
            _resourceRuntime.Buffers.Retire(buffer, memory, owner);

        ulong capacity = ComputeCapacity(currentCapacity, requiredCapacity);
        BufferCreateInfo createInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = capacity,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Result result = target.VulkanApi.CreateBuffer(target.Device, ref createInfo, null, out Buffer created);
        target.ObserveNativeResult($"vkCreateBuffer.{owner}", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create {owner} ({result}).");

        target.TrackLiveBuffer(created, owner);
        VulkanMemoryAllocation allocation = default;
        try
        {
            allocation = target.AllocateBufferMemoryWithFallback(
                created,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            target.TrackExternalBufferAllocation(created, in allocation);
            result = target.VulkanApi.BindBufferMemory(
                target.Device,
                created,
                allocation.Memory,
                allocation.Offset);
            target.ObserveNativeResult($"vkBindBufferMemory.{owner}", result);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to bind {owner} memory ({result}).");
        }
        catch
        {
            _resourceRuntime.Buffers.Retire(created, allocation.Memory, $"{owner}.CreateFailure");
            throw;
        }

        buffer = created;
        memory = allocation.Memory;
        currentCapacity = capacity;
    }

    private VulkanMemoryAllocation ResolveBufferAllocation(Buffer buffer)
        => _resourceRuntime.Buffers.TryGetAllocation(buffer, out VulkanMemoryAllocation allocation)
            ? allocation
            : throw new InvalidOperationException(
                $"ImGui buffer 0x{buffer.Handle:X} has no tracked memory allocation.");

    private static ulong ComputeCapacity(ulong current, ulong required)
    {
        ulong target = Math.Max(required, 64UL * 1024UL);
        if (current != 0)
            target = Math.Max(target, current <= ulong.MaxValue / 2UL ? current * 2UL : ulong.MaxValue);
        return AlignToPowerOfTwo(target);
    }

    private static ulong AlignToPowerOfTwo(ulong value)
    {
        if (value <= 1)
            return 1;
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        value |= value >> 32;
        return value == ulong.MaxValue ? ulong.MaxValue : value + 1;
    }

    private static void CopySnapshotVertices(
        in VulkanImGuiFrameSnapshot snapshot,
        Span<byte> destination)
    {
        Span<ImDrawVert> vertices = MemoryMarshal.Cast<byte, ImDrawVert>(destination);
        int vertexOffset = 0;
        for (int listIndex = 0; listIndex < snapshot.CommandListCount; listIndex++)
        {
            VulkanImGuiCommandListSnapshot list = snapshot.CommandLists[listIndex];
            list.Vertices.AsSpan(0, list.VertexCount).CopyTo(vertices.Slice(vertexOffset, list.VertexCount));
            vertexOffset += list.VertexCount;
        }
    }

    private static void CopySnapshotIndices(in VulkanImGuiFrameSnapshot snapshot, Span<byte> destination)
    {
        Span<ushort> indices = MemoryMarshal.Cast<byte, ushort>(destination);
        int indexOffset = 0;
        for (int listIndex = 0; listIndex < snapshot.CommandListCount; listIndex++)
        {
            VulkanImGuiCommandListSnapshot list = snapshot.CommandLists[listIndex];
            list.Indices.AsSpan(0, list.IndexCount).CopyTo(indices.Slice(indexOffset, list.IndexCount));
            indexOffset += list.IndexCount;
        }
    }
}
