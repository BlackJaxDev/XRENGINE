using System;
using XREngine.Data.Rendering;
using static XREngine.Data.Core.XRMath;

namespace XREngine.Rendering.Compute;

// SSBO ownership and sizing for GpuBvhTree.
//
// Owns the node / range / morton / counters buffers and a borrowed reference
// to the caller-owned AABB buffer. The C-GPU-4 capacity-slack policy that
// keeps `OverflowBvhBit` unambiguous lives in EnsureBuffers.
public sealed partial class GpuBvhTree
{
    // Owned buffers. Lifetimes match the GpuBvhTree itself.
    private XRDataBuffer? _nodeBuffer;
    private XRDataBuffer? _rangeBuffer;
    private XRDataBuffer? _mortonBuffer;
    private XRDataBuffer? _counterBuffer;

    // Borrowed: caller-owned AABB buffer. See the lifetime contract documented
    // on <see cref="Build"/>. Never disposed here; nulled in Dispose.
    private XRDataBuffer? _aabbBuffer;

    /// <summary>
    /// GPU buffer containing BVH nodes.
    /// Layout: [nodeCount, rootIndex, nodeStrideScalars, maxLeafPrimitives, ...nodes].
    /// </summary>
    public XRDataBuffer? NodeBuffer => _nodeBuffer;

    /// <summary>
    /// GPU buffer containing primitive ranges for each leaf node.
    /// Layout: [start, count] pairs.
    /// </summary>
    public XRDataBuffer? RangeBuffer => _rangeBuffer;

    /// <summary>
    /// GPU buffer containing Morton codes and object IDs.
    /// Layout: [mortonCode, objectId] pairs.
    /// </summary>
    public XRDataBuffer? MortonBuffer => _mortonBuffer;

    private void EnsureBuffers(uint primitiveCount)
    {
        uint leafCount = (primitiveCount + _maxLeafPrimitives - 1u) / _maxLeafPrimitives;
        uint nodeCount = leafCount > 0 ? (leafCount * 2u) - 1u : 0u;
        _lastNodeCount = nodeCount;

        // C-GPU-4 capacity headroom: bvh_build.comp computes
        //     maxNodesByBuffer = (nodeScalarCapacity - header) / stride
        // and trips OverflowBvhBit when `totalNodes > maxNodesByBuffer`.
        // Allocating exactly 2N-1 ties the boundary at zero slack, so any
        // rounding, stale count, or stage-3 malformed-tree detection
        // (parent-pointer cycle from duplicate Morton codes) becomes
        // indistinguishable from real capacity exhaustion. 8 extra node slots
        // (~640 bytes) give the shader unambiguous headroom and let BvhBit
        // mean exclusively "malformed tree".
        const uint NodeCapacitySlack = 8u;
        uint nodeCapacity = Math.Max(nodeCount, 1u) + NodeCapacitySlack;

        uint nodeScalars = GpuBvhLayout.NodeScalarCapacity(nodeCapacity);
        uint rangeScalars = GpuBvhLayout.RangeScalarCapacity(nodeCapacity);
        uint mortonScalars = Math.Max(1u, NextPowerOfTwo(primitiveCount)) * 2u;

        EnsureBuffer(ref _nodeBuffer, "GpuBvhTree.Nodes", nodeScalars, 6);
        EnsureBuffer(ref _rangeBuffer, "GpuBvhTree.Ranges", rangeScalars, 7);
        EnsureBuffer(ref _mortonBuffer, "GpuBvhTree.Morton", mortonScalars, null);
        EnsureBuffer(ref _counterBuffer, "GpuBvhTree.Counters", nodeCapacity, 11);
        EnsureOverflowFlagBuffer();
    }

    private static void EnsureBuffer(ref XRDataBuffer? buffer, string name, uint scalarCount, uint? bindingIndex)
    {
        if (buffer is null)
        {
            buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, scalarCount, EComponentType.UInt, 1, false, true)
            {
                Usage = EBufferUsage.DynamicDraw,
                Resizable = true,
                DisposeOnPush = false,
                PadEndingToVec4 = true,
                ShouldMap = false
            };
            if (bindingIndex.HasValue)
                buffer.SetBlockIndex(bindingIndex.Value);
            buffer.Generate();
        }
        else if (buffer.ElementCount < scalarCount)
        {
            if (buffer.Resize(scalarCount, false, true))
                buffer.PushData();
        }
    }

    /// <summary>
    /// Zeroes a buffer in place without allocating a transient managed array.
    /// Called from <see cref="ClearCore"/> on invalidation paths, which can
    /// be hit per-frame.
    /// </summary>
    private static void ClearBuffer(XRDataBuffer? buffer)
    {
        if (buffer is null)
            return;

        uint count = buffer.ElementCount;
        if (count == 0)
            return;

        for (uint i = 0; i < count; i++)
            buffer.SetDataRawAtIndex(i, 0u);
        buffer.PushSubData();
    }

    private uint GetMortonCapacity()
        => _mortonBuffer is null ? 0u : _mortonBuffer.ElementCount / 2u;

    private void DisposeBuffersCore()
    {
        _nodeBuffer?.Dispose();
        _rangeBuffer?.Dispose();
        _mortonBuffer?.Dispose();
        _counterBuffer?.Dispose();

        _nodeBuffer = null;
        _rangeBuffer = null;
        _mortonBuffer = null;
        _counterBuffer = null;
        // _aabbBuffer is borrowed; only drop our reference.
        _aabbBuffer = null;
    }
}
