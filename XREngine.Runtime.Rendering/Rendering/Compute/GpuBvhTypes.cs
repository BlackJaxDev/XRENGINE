using System;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.Compute;

internal static class GpuBvhLayout
{
    // Header scalars are written before the node array in the SSBO:
    // nodeCount, rootIndex, nodeStrideScalars, maxLeafPrimitives.
    public const uint NodeHeaderScalarCount = 4;
    public const uint NodeStrideScalars = 12;
    public const uint LeafFlag = 1u;
    public const uint InvalidIndex = uint.MaxValue;

    public static uint NodeScalarCapacity(uint nodeCount)
        => NodeHeaderScalarCount + nodeCount * NodeStrideScalars;

}

/// <summary>
/// CPU mirror of the BVH node layout used by GPU traversal shaders.
/// Offsets and total size are fixed to match std430 packing (48 bytes / 12 uint scalars).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = (int)(GpuBvhLayout.NodeStrideScalars * sizeof(uint)))]
internal struct GpuBvhNode
{
    [FieldOffset(0)] public Vector3 MinBounds;
    [FieldOffset(12)] public uint LeftChild;

    [FieldOffset(16)] public Vector3 MaxBounds;
    [FieldOffset(28)] public uint RightChild;

    [FieldOffset(32)] public uint PrimitiveStart;
    [FieldOffset(36)] public uint PrimitiveCount;

    [FieldOffset(40)] public uint ParentIndex;
    [FieldOffset(44)] public uint Flags;
}

/// <summary>
/// Helper to allocate an SSBO-friendly compact BVH node buffer.
/// Buffers are sized in uint scalars so their strides remain compatible with
/// the GPU-side BVH traversal shaders.
/// </summary>
internal sealed class GpuBvhBufferSet : IDisposable
{
    public XRDataBuffer Nodes { get; }

    public GpuBvhBufferSet(uint nodeCount, bool mapBuffers = false)
    {
        Nodes = CreateBuffer("BvhNodes", GpuBvhLayout.NodeScalarCapacity(nodeCount), mapBuffers);
    }

    public void Dispose()
    {
        Nodes.Dispose();
    }

    private static XRDataBuffer CreateBuffer(string name, uint scalarCount, bool mapBuffers)
        => new(name, EBufferTarget.ShaderStorageBuffer, scalarCount, EComponentType.UInt, 1, false, true)
        {
            Usage = EBufferUsage.DynamicDraw,
            Resizable = true,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
            ShouldMap = mapBuffers
        };
}
