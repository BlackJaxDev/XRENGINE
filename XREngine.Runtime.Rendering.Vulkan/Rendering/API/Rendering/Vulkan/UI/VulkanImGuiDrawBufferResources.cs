using ImGuiNET;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the per-swapchain-image host-visible ImGui vertex and index buffers.
/// Buffer replacement is retired through <see cref="VulkanBufferResourceService"/>
/// instead of reaching back into the renderer.
/// </summary>
internal unsafe sealed class VulkanImGuiDrawBufferResources(
    VulkanOutputRuntime outputRuntime,
    VulkanResourceRuntime resourceRuntime)
{
    private readonly VulkanOutputRuntime _outputRuntime = outputRuntime;
    private readonly VulkanResourceRuntime _resourceRuntime = resourceRuntime;

    internal ref VulkanImGuiDrawBufferSet Ensure(uint imageIndex, ulong vertexBytes, ulong indexBytes)
    {
        int slot = EnsureSlot(imageIndex);
        ref VulkanImGuiDrawBufferSet buffers = ref _outputRuntime._imguiResources.DrawBuffers[slot];
        VulkanTargetOutputContext target = _outputRuntime.TargetOutputContext;
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
        VulkanTargetOutputContext target = _outputRuntime.TargetOutputContext;
        if (!target.TryMapMemoryAllocation(
                ResolveBufferAllocation(buffers.VertexBuffer),
                0,
                Math.Max(vertexBytes, 1UL),
                out void* vertices))
        {
            throw new InvalidOperationException("Failed to map ImGui vertex-buffer memory.");
        }

        try
        {
            if (!target.TryMapMemoryAllocation(
                    ResolveBufferAllocation(buffers.IndexBuffer),
                    0,
                    Math.Max(indexBytes, 1UL),
                    out void* indices))
            {
                throw new InvalidOperationException("Failed to map ImGui index-buffer memory.");
            }

            try
            {
                CopySnapshot(snapshot, vertices, indices);
            }
            finally
            {
                target.UnmapMemoryAllocation(ResolveBufferAllocation(buffers.IndexBuffer));
            }
        }
        finally
        {
            target.UnmapMemoryAllocation(ResolveBufferAllocation(buffers.VertexBuffer));
        }
    }

    /// <summary>Queues every generation-owned draw buffer for exact retirement.</summary>
    internal void RetireAll()
    {
        VulkanImGuiDrawBufferSet[] buffers = _outputRuntime._imguiResources.DrawBuffers;
        for (int index = 0; index < buffers.Length; index++)
        {
            ref VulkanImGuiDrawBufferSet set = ref buffers[index];
            if (set.VertexBuffer.Handle != 0)
                _resourceRuntime.Buffers.Retire(set.VertexBuffer, set.VertexBufferMemory, "ImGui.Draw.VertexBuffer");
            if (set.IndexBuffer.Handle != 0)
                _resourceRuntime.Buffers.Retire(set.IndexBuffer, set.IndexBufferMemory, "ImGui.Draw.IndexBuffer");
        }
        _outputRuntime._imguiResources.DrawBuffers = [];
    }

    private int EnsureSlot(uint imageIndex)
    {
        int required = Math.Max(
            _resourceRuntime.Lifetime.Retirement.Buffers.Length,
            _outputRuntime.Desktop.Images?.Length ?? 0);
        required = Math.Max(required, checked((int)imageIndex + 1));
        if (_outputRuntime._imguiResources.DrawBuffers.Length < required)
            Array.Resize(ref _outputRuntime._imguiResources.DrawBuffers, required);
        return checked((int)imageIndex);
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
        target.DeviceContext.ObserveNativeResult($"vkCreateBuffer.{owner}", result);
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
            target.DeviceContext.ObserveNativeResult($"vkBindBufferMemory.{owner}", result);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to bind {owner} memory ({result}).");
        }
        catch
        {
            target.DestroyBufferRaw(created, allocation.Memory);
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

    private static void CopySnapshot(
        in VulkanImGuiFrameSnapshot snapshot,
        void* vertexDestination,
        void* indexDestination)
    {
        byte* vertices = (byte*)vertexDestination;
        byte* indices = (byte*)indexDestination;
        for (int listIndex = 0; listIndex < snapshot.CommandListCount; listIndex++)
        {
            VulkanImGuiCommandListSnapshot list = snapshot.CommandLists[listIndex];
            nuint vertexBytes = checked((nuint)list.VertexCount * (nuint)sizeof(ImDrawVert));
            nuint indexBytes = checked((nuint)list.IndexCount * sizeof(ushort));
            fixed (ImDrawVert* sourceVertices = list.Vertices)
                System.Buffer.MemoryCopy(sourceVertices, vertices, (long)vertexBytes, (long)vertexBytes);
            fixed (ushort* sourceIndices = list.Indices)
                System.Buffer.MemoryCopy(sourceIndices, indices, (long)indexBytes, (long)indexBytes);
            vertices += vertexBytes;
            indices += indexBytes;
        }
    }
}
