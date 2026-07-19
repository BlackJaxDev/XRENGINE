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
    private XRDataBuffer? _mortonBuffer;
    private XRDataBuffer? _counterBuffer;
    private XRDataBuffer? _radixScratchBuffer;
    private XRDataBuffer? _radixOffsetsBuffer;
    private XRDataBuffer? _qualityDiagnosticsBuffer;

    // Borrowed: caller-owned AABB buffer. See the lifetime contract documented
    // on <see cref="Build"/>. Never disposed here; nulled in Dispose.
    private XRDataBuffer? _aabbBuffer;

    /// <summary>
    /// GPU buffer containing BVH nodes.
    /// Layout: [nodeCount, rootIndex, nodeStrideScalars, maxLeafPrimitives, ...nodes].
    /// </summary>
    public XRDataBuffer? NodeBuffer => _nodeBuffer;

    /// <summary>
    /// Compatibility alias for consumers that still request a range buffer.
    /// Primitive ranges are embedded in <see cref="NodeBuffer"/>.
    /// </summary>
    public XRDataBuffer? RangeBuffer => _nodeBuffer;

    /// <summary>
    /// GPU buffer containing Morton codes and object IDs.
    /// Layout: [mortonCode, objectId] pairs.
    /// </summary>
    public XRDataBuffer? MortonBuffer => _mortonBuffer;

    /// <summary>
    /// GPU-resident Morton distribution and normalized hierarchy-quality counters.
    /// This buffer is never synchronously read by the tree.
    /// </summary>
    public XRDataBuffer? QualityDiagnosticsBuffer => _qualityDiagnosticsBuffer;

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
        uint nodeCapacity = NextPowerOfTwo(Math.Max(nodeCount, 1u) + NodeCapacitySlack);

        uint nodeScalars = GpuBvhLayout.NodeScalarCapacity(nodeCapacity);
        uint mortonScalars = Math.Max(1u, NextPowerOfTwo(primitiveCount)) * 2u;
        uint internalCapacity = NextPowerOfTwo(Math.Max(leafCount - 1u, 1u));
        uint radixBlockCount = Math.Max(1u, ComputeGroups(primitiveCount, 256u));
        uint radixOffsetScalars = NextPowerOfTwo((radixBlockCount * 256u) + 256u);

        _bufferReallocationCount += EnsureBuffer(ref _nodeBuffer, _nodeBufferName, nodeScalars, 6) ? 1u : 0u;
        _bufferReallocationCount += EnsureBuffer(ref _mortonBuffer, _mortonBufferName, mortonScalars, null) ? 1u : 0u;
        _bufferReallocationCount += EnsureBuffer(ref _counterBuffer, _counterBufferName, internalCapacity, 11) ? 1u : 0u;
        _bufferReallocationCount += EnsureBuffer(ref _radixScratchBuffer, _radixScratchBufferName, mortonScalars, null) ? 1u : 0u;
        _bufferReallocationCount += EnsureBuffer(ref _radixOffsetsBuffer, _radixOffsetsBufferName, radixOffsetScalars, null) ? 1u : 0u;
        _bufferReallocationCount += EnsureBuffer(ref _qualityDiagnosticsBuffer, _qualityDiagnosticsBufferName, 128u, Bindings.QualityDiagnostics) ? 1u : 0u;
        EnsureOverflowFlagBuffer();
    }

    private static bool EnsureBuffer(ref XRDataBuffer? buffer, string name, uint scalarCount, uint? bindingIndex)
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
            return true;
        }
        else if (buffer.ElementCount < scalarCount)
        {
            if (buffer.Resize(scalarCount, false, true))
            {
                buffer.PushData();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clears only the logical header. Retained nodes are unreachable when
    /// nodeCount is zero and do not need a capacity-sized CPU upload.
    /// </summary>
    private static void ClearNodeHeader(XRDataBuffer? buffer)
    {
        if (buffer is null)
            return;

        if (buffer.ElementCount < GpuBvhLayout.NodeHeaderScalarCount)
            return;

        for (uint i = 0; i < GpuBvhLayout.NodeHeaderScalarCount; i++)
            buffer.SetDataRawAtIndex(i, 0u);
        buffer.PushSubData(0, GpuBvhLayout.NodeHeaderScalarCount * sizeof(uint));
    }

    private uint GetMortonCapacity()
        => _mortonBuffer is null ? 0u : _mortonBuffer.ElementCount / 2u;

    private GpuBvhDiagnostics CreateDiagnostics()
    {
        uint nodeScalars = _nodeBuffer?.ElementCount ?? 0u;
        uint nodeCapacity = nodeScalars > GpuBvhLayout.NodeHeaderScalarCount
            ? (nodeScalars - GpuBvhLayout.NodeHeaderScalarCount) / GpuBvhLayout.NodeStrideScalars
            : 0u;
        ulong retainedBytes = BufferBytes(_nodeBuffer) + BufferBytes(_mortonBuffer) +
            BufferBytes(_counterBuffer) + BufferBytes(_radixScratchBuffer) + BufferBytes(_radixOffsetsBuffer);
        return new GpuBvhDiagnostics(
            _buildCount,
            _refitCount,
            _skippedCleanFrameCount,
            _clearCount,
            _bufferReallocationCount,
            _lastPrimitiveCount,
            _lastNodeCount,
            nodeCapacity,
            GetMortonCapacity(),
            retainedBytes + BufferBytes(_qualityDiagnosticsBuffer),
            _initialBuildCount,
            _topologyChangeRebuildCount,
            _normalizationEscapeRebuildCount,
            _periodicQualityRebuildCount,
            _lastDirtyLeafCount,
            _lastAabbUploadBytes,
            _lastAabbCopyBytes,
            _synchronousReadbackBytes,
            _asynchronousReadbackBytes,
            _zeroReadbackSubmissionCount,
            _qualityAnalysisCount,
            QualityAnalysisRefitCadence);
    }

    private static ulong BufferBytes(XRDataBuffer? buffer)
        => buffer is null ? 0u : (ulong)buffer.ElementCount * buffer.ElementSize;

    private void DisposeBuffersCore()
    {
        _nodeBuffer?.Dispose();
        _mortonBuffer?.Dispose();
        _counterBuffer?.Dispose();
        _radixScratchBuffer?.Dispose();
        _radixOffsetsBuffer?.Dispose();
        _qualityDiagnosticsBuffer?.Dispose();

        _nodeBuffer = null;
        _mortonBuffer = null;
        _counterBuffer = null;
        _radixScratchBuffer = null;
        _radixOffsetsBuffer = null;
        _qualityDiagnosticsBuffer = null;
        // _aabbBuffer is borrowed; only drop our reference.
        _aabbBuffer = null;
    }
}
