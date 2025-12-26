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
    public const uint NodeStrideScalars = 20;
    public const uint RangeStrideScalars = 2;
    public const uint LeafFlag = 1u;
    public const uint InvalidIndex = uint.MaxValue;

    public static uint NodeScalarCapacity(uint nodeCount)
        => NodeHeaderScalarCount + nodeCount * NodeStrideScalars;

    public static uint RangeScalarCapacity(uint nodeCount)
        => nodeCount * RangeStrideScalars;
}

/// <summary>
/// CPU mirror of the BVH node layout used by GPU traversal shaders.
/// Offsets and total size are fixed to match std430 packing (80 bytes / 20 uint scalars).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = (int)(GpuBvhLayout.NodeStrideScalars * sizeof(uint)))]
internal struct GpuBvhNode
{
    [FieldOffset(0)] public Vector3 MinBounds;
    [FieldOffset(16)] public uint LeftChild;

    [FieldOffset(32)] public Vector3 MaxBounds;
    [FieldOffset(48)] public uint RightChild;

    [FieldOffset(56)] public uint PrimitiveStart;
    [FieldOffset(60)] public uint PrimitiveCount;

    [FieldOffset(64)] public uint ParentIndex;
    [FieldOffset(68)] public uint Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
internal struct GpuBvhPrimitiveRange
{
    public uint Start;
    public uint Count;
}

/// <summary>
/// Helper to allocate SSBO-friendly buffers for BVH nodes and primitive ranges.
/// Buffers are sized in uint scalars so their strides remain compatible with
/// the GPU-side BVH traversal shaders.
/// </summary>
internal sealed class GpuBvhBufferSet : IDisposable
{
    public XRDataBuffer Nodes { get; }
    public XRDataBuffer PrimitiveRanges { get; }

    public GpuBvhBufferSet(uint nodeCount, bool mapBuffers = false)
    {
        Nodes = CreateBuffer("BvhNodes", GpuBvhLayout.NodeScalarCapacity(nodeCount), mapBuffers);
        PrimitiveRanges = CreateBuffer("PrimitiveRanges", GpuBvhLayout.RangeScalarCapacity(nodeCount), mapBuffers);
    }

    public void Dispose()
    {
        Nodes.Dispose();
        PrimitiveRanges.Dispose();
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
