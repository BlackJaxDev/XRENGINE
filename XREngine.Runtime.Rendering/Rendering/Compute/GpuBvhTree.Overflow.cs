using System;
using System.Collections.Generic;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

// GPU overflow detection and asynchronous CPU readback for GpuBvhTree.
//
// All BVH compute kernels write into a single-uint overflow-flag SSBO when
// they detect capacity exhaustion or a malformed-tree condition (see
// LogOverflowTrace for the full disambiguation). This partial owns the
// overflow flag buffer, the asynchronous fence used to defer the readback
// off the build path, and the consumer that interprets the flag bits.
public sealed partial class GpuBvhTree
{
    // Overflow flag bits set by the shaders. Keep in sync with the
    // OverflowFlags binding declarations in bvh_build.comp / morton_codes.comp.
    private const uint OverflowMortonBit = 1u;
    private const uint OverflowNodeBit = 1u << 1;
    private const uint OverflowQueueBit = 1u << 2;
    private const uint OverflowBvhBit = 1u << 3;

    // C-GPU-3 diagnostic flag (XRE_HIZ_CULL_TRACE=1).
    // When set, the BVH dumps every overflow with full capacity context
    // (primitive count, node count, computed node/range/morton capacities,
    // build mode). This lets us distinguish real capacity exhaustion from the
    // stage-3 malformed-tree detection in bvh_build.comp; both set
    // OverflowBvhBit but mean very different things. Default off; read once
    // at type init.
    private static readonly bool s_traceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("XRE_HIZ_CULL_TRACE"), "1", StringComparison.Ordinal);

    private const uint PendingOverflowFenceDiagnosticFrames = 3u;

    private XRDataBuffer? _overflowFlagBuffer;
    private XRGpuFence? _pendingOverflowFence;
    private uint _pendingOverflowPrimitiveCount;
    private uint _pendingOverflowNodeCount;
    private uint _pendingOverflowAgeFrames;
    private bool _warnedPendingOverflowFenceDelay;

    /// <summary>
    /// Polls the pending asynchronous overflow readback, if any, without
    /// waiting for the GPU. Returns <c>true</c> when an overflow was observed
    /// and the BVH was cleared.
    /// </summary>
    public bool PollPendingOverflow()
    {
        lock (_syncRoot)
            return PollPendingOverflowCore();
    }

    private void EnsureOverflowFlagBuffer()
    {
        if (_overflowFlagBuffer is not null)
            return;

        _overflowFlagBuffer = new XRDataBuffer("GpuBvhTree.OverflowFlag", EBufferTarget.ShaderStorageBuffer, 1, EComponentType.UInt, 1, false, true)
        {
            Usage = EBufferUsage.DynamicDraw,
            Resizable = false,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
            ShouldMap = false,
            // Allow temporary mapping for GPU→CPU readback of the overflow flag.
            RangeFlags = EBufferMapRangeFlags.Read
        };
        _overflowFlagBuffer.SetBlockIndex(Bindings.OverflowFlag);
        _overflowFlagBuffer.SetDataRaw(new uint[] { 0u }, 1);
        _overflowFlagBuffer.PushSubData();
    }

    private void ResetOverflowFlagBuffer()
    {
        EnsureOverflowFlagBuffer();
        if (_overflowFlagBuffer is null)
            return;

        // EnsureOverflowFlagBuffer always initializes ClientSideSource on first
        // creation, so SetDataRawAtIndex is safe here without an extra alloc.
        _overflowFlagBuffer.SetDataRawAtIndex(0, 0u);
        _overflowFlagBuffer.PushSubData();
    }

    /// <summary>
    /// Inserts a GPU fence after the build path so the overflow flag can be
    /// read back asynchronously on a later frame. Returns <c>true</c> only
    /// when overflow was observed and consumed synchronously (no fence
    /// support); the caller must abandon the in-flight build in that case.
    /// </summary>
    private bool EnqueueOverflowFlagReadback(uint primitiveCount, uint nodeCount)
    {
        if (_overflowFlagBuffer is null)
            return false;

        // Strict zero-readback profiling cannot enqueue or consume even a
        // 4-byte diagnostic readback.
        if (RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy() == EMeshSubmissionStrategy.GpuIndirectZeroReadback)
            return false;

        XRGpuFence? fence = AbstractRenderer.Current?.InsertGpuFence();
        if (fence is null)
            return ConsumeOverflowFlag(primitiveCount, nodeCount);

        _pendingOverflowFence = fence;
        _pendingOverflowPrimitiveCount = primitiveCount;
        _pendingOverflowNodeCount = nodeCount;
        _pendingOverflowAgeFrames = 0u;
        _warnedPendingOverflowFenceDelay = false;
        return false;
    }

    private bool PollPendingOverflowCore()
    {
        if (_pendingOverflowFence is null)
            return false;

        if (RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy() == EMeshSubmissionStrategy.GpuIndirectZeroReadback)
        {
            DropPendingOverflowFence("zero-readback strategy is active", warnIfOld: false);
            return false;
        }

        EGpuFenceStatus status = _pendingOverflowFence.Poll();
        if (status == EGpuFenceStatus.Pending)
        {
            _pendingOverflowAgeFrames++;
            if (_pendingOverflowAgeFrames >= PendingOverflowFenceDiagnosticFrames && !_warnedPendingOverflowFenceDelay)
            {
                _warnedPendingOverflowFenceDelay = true;
                Debug.LogWarning($"[GpuBvhTree] Overflow readback fence still pending after {_pendingOverflowAgeFrames} non-blocking polls.");
            }
            return false;
        }

        uint primitiveCount = _pendingOverflowPrimitiveCount;
        uint nodeCount = _pendingOverflowNodeCount;
        DropPendingOverflowFence(status == EGpuFenceStatus.Failed ? "fence wait failed" : "fence signaled", warnIfOld: false);

        if (status == EGpuFenceStatus.Failed)
        {
            Debug.LogWarning("[GpuBvhTree] Overflow readback fence wait failed; dropping the diagnostic flag for this build.");
            return false;
        }

        return ConsumeOverflowFlag(primitiveCount, nodeCount);
    }

    private void DropPendingOverflowFence(string reason, bool warnIfOld)
    {
        if (_pendingOverflowFence is null)
            return;

        if (warnIfOld && _pendingOverflowAgeFrames >= PendingOverflowFenceDiagnosticFrames && !_warnedPendingOverflowFenceDelay)
        {
            Debug.LogWarning($"[GpuBvhTree] Dropping overflow readback fence after {_pendingOverflowAgeFrames} non-blocking polls ({reason}).");
            _warnedPendingOverflowFenceDelay = true;
        }

        _pendingOverflowFence.Dispose();
        _pendingOverflowFence = null;
        _pendingOverflowPrimitiveCount = 0u;
        _pendingOverflowNodeCount = 0u;
        _pendingOverflowAgeFrames = 0u;
        _warnedPendingOverflowFenceDelay = false;
    }

    private bool ConsumeOverflowFlag(uint primitiveCount, uint nodeCount)
    {
        if (_overflowFlagBuffer is null)
            return false;

        // Strict zero-readback profiling cannot map even a 4-byte overflow
        // flag. Capacity is sized conservatively up front; keep the BVH
        // GPU-resident.
        if (RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy() == EMeshSubmissionStrategy.GpuIndirectZeroReadback)
            return false;

        // Map only after a queued GPU fence has already signaled. Backends
        // that cannot expose a fence fall back to the old synchronous read.
        uint flags = ReadOverflowFlagFromGpu();
        if (flags == 0u)
            return false;

        Debug.LogWarning($"[GpuBvhTree] Overflow detected while building BVH ({DescribeOverflow(flags, primitiveCount, nodeCount)}). Falling back to non-BVH culling.");
        if (s_traceEnabled)
            LogOverflowTrace(flags, primitiveCount, nodeCount);
        _lastNodeCount = 0;
        _lastPrimitiveCount = 0;
        _isDirty = true;
        return true;
    }

    /// <summary>
    /// Reads the overflow flag by temporarily mapping the GPU buffer. The
    /// buffer was configured with <see cref="EBufferMapRangeFlags.Read"/> so
    /// <c>glMapNamedBufferRange</c> succeeds with <c>GL_MAP_READ_BIT</c>.
    /// <para/>
    /// Note: <see cref="XRDataBuffer.ClientSideSource"/> (<c>Address</c>) only
    /// reflects the last CPU-side upload — GPU compute writes are NOT synced
    /// back to it. A real map/unmap (or <c>glGetBufferSubData</c>) is required
    /// to read data written by the GPU.
    /// </summary>
    private uint ReadOverflowFlagFromGpu()
    {
        if (_overflowFlagBuffer is null)
            return 0u;

        _overflowFlagBuffer.MapBufferData();
        RuntimeEngine.Rendering.Stats.RecordGpuBufferMapped();
        try
        {
            foreach (var addr in _overflowFlagBuffer.GetMappedAddresses())
            {
                if (addr.IsValid)
                {
                    unsafe
                    {
                        // Phase B invariant: every GPU->CPU readback must be
                        // recorded in Engine.Rendering.Stats so
                        // GpuReadbackBytes stays honest under the instrumented
                        // strategy. This is a 4-byte read, but it counts.
                        RuntimeEngine.Rendering.Stats.RecordGpuReadbackBytes(sizeof(uint));
                        return *((uint*)addr.Pointer);
                    }
                }
            }
        }
        finally
        {
            _overflowFlagBuffer.UnmapBufferData();
        }

        return 0u;
    }

    private static string DescribeOverflow(uint flags, uint primitiveCount, uint nodeCount)
    {
        List<string> reasons = new();
        if ((flags & OverflowMortonBit) != 0)
            reasons.Add($"morton capacity exceeded (primitives={primitiveCount})");
        if ((flags & OverflowNodeBit) != 0)
            reasons.Add($"node capacity exceeded (nodes={nodeCount})");
        if ((flags & OverflowQueueBit) != 0)
            reasons.Add("queue capacity exceeded");
        if ((flags & OverflowBvhBit) != 0)
            reasons.Add($"BVH build overflow (primitives={primitiveCount}, nodes={nodeCount})");

        return reasons.Count == 0
            ? $"unknown overflow (flags=0x{flags:X})"
            : string.Join(", ", reasons);
    }

    /// <summary>
    /// C-GPU-3 trace: dump capacity-vs-required figures so the operator can
    /// tell whether the overflow is real capacity exhaustion or stage-3
    /// malformed-tree detection (parent-pointer cycle from duplicate Morton
    /// codes or stage-2 race). Both conditions set
    /// <see cref="OverflowBvhBit"/> in bvh_build.comp; only this trace can
    /// distinguish them.
    /// </summary>
    private void LogOverflowTrace(uint flags, uint primitiveCount, uint nodeCount)
    {
        uint nodeScalars = _nodeBuffer?.ElementCount ?? 0u;
        uint rangeScalars = _rangeBuffer?.ElementCount ?? 0u;
        uint mortonScalars = _mortonBuffer?.ElementCount ?? 0u;
        uint nodesByBuffer = nodeScalars > GpuBvhLayout.NodeStrideScalars
            ? (nodeScalars - GpuBvhLayout.NodeHeaderScalarCount) / GpuBvhLayout.NodeStrideScalars
            : 0u;
        uint rangesByBuffer = rangeScalars / GpuBvhLayout.RangeStrideScalars;
        uint mortonCapacity = mortonScalars / 2u;

        bool nodeCapacityHit = (flags & OverflowNodeBit) != 0 || nodeCount > nodesByBuffer || nodeCount > rangesByBuffer;
        bool mortonCapacityHit = (flags & OverflowMortonBit) != 0 || primitiveCount > mortonCapacity;
        bool bvhBit = (flags & OverflowBvhBit) != 0;
        string suspect = (bvhBit && !nodeCapacityHit && !mortonCapacityHit)
            ? "stage-3 malformed tree (parent-pointer cycle; check for duplicate Morton codes or stage-2 race)"
            : "real capacity exhaustion";

        Debug.LogWarning(
            $"[GpuBvhTree][trace] flags=0x{flags:X} buildMode={_buildMode} maxLeaf={_maxLeafPrimitives} " +
            $"primitives={primitiveCount} nodes={nodeCount} " +
            $"nodeCapacity={nodesByBuffer} rangeCapacity={rangesByBuffer} mortonCapacity={mortonCapacity} " +
            $"=> {suspect}");
    }

    private void DisposeOverflowCore()
    {
        _overflowFlagBuffer?.Dispose();
        _pendingOverflowFence?.Dispose();
        _overflowFlagBuffer = null;
        _pendingOverflowFence = null;
    }
}
