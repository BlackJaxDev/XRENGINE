using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using static XREngine.Data.Core.XRMath;

namespace XREngine.Rendering.Compute;

/// <summary>
/// How the GPU BVH hierarchy is constructed.
/// </summary>
public enum BvhBuildMode
{
	/// <summary>Morton-code LBVH only.</summary>
	MortonOnly = 0,
	/// <summary>Morton-code LBVH followed by SAH refinement pass.</summary>
	MortonPlusSah = 1
}

/// <summary>
/// A fully GPU-based BVH tree that can be used for scene-level culling, per-model collision,
/// skinned mesh BVH, and other spatial acceleration needs.
/// </summary>
/// <remarks>
/// This class provides a reusable BVH infrastructure that builds and maintains a binary tree
/// on the GPU using compute shaders. The tree supports:
/// - Morton-code based radix construction
/// - Optional SAH refinement
/// - Incremental refit for animated/skinned meshes
///
/// Traversal (frustum / ray) is performed by consumer shaders against the exposed
/// node/range/morton buffers; no CPU traversal API is provided here.
///
/// <para>
/// <b>AABB buffer lifetime contract:</b> the caller owns <c>aabbBuffer</c> passed to
/// <see cref="Build"/>. This class retains a reference for use by <see cref="Refit"/>
/// and must remain valid (not disposed, not reallocated) until the next <see cref="Build"/>
/// call replaces it, <see cref="Clear"/> is called, or this object is disposed.
/// </para>
/// </remarks>
public sealed class GpuBvhTree : IDisposable
{
    private bool _disposed;
    private readonly object _syncRoot = new();

    /// <summary>
    /// Shader SSBO binding points. These must match the
    /// <c>layout(std430, binding = N)</c> declarations in the BVH compute shaders
    /// (bvh_build.comp, bvh_refit.comp, bvh_sah_refine.comp, morton_codes.comp,
    /// sort_morton*.comp, pad_morton.comp, merge_morton.comp).
    /// </summary>
    internal static class Bindings
    {
        public const uint Aabb = 0u;
        public const uint Morton = 1u;
        public const uint Node = 2u;
        public const uint Range = 3u;
        public const uint OverflowFlag = 8u;
        public const uint Counters = 11u;
    }

    private const uint OverflowMortonBit = 1u;
    private const uint OverflowNodeBit = 1u << 1;
    private const uint OverflowQueueBit = 1u << 2;
    private const uint OverflowBvhBit = 1u << 3;

    // C-GPU-3 diagnostic flag (XRE_HIZ_CULL_TRACE=1).
    // When set, the BVH dumps every overflow with full capacity context (primitive
    // count, node count, computed node/range/morton capacities, build mode). This
    // lets us distinguish a real capacity exhaustion from the stage-3 malformed-tree
    // detection in bvh_build.comp (both set OverflowBvhBit but mean very different
    // things). Default off; reads env once on first use.
    private static readonly bool s_traceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("XRE_HIZ_CULL_TRACE"), "1", StringComparison.Ordinal);

    // Buffer storage
    private XRDataBuffer? _nodeBuffer;
    private XRDataBuffer? _rangeBuffer;
    private XRDataBuffer? _mortonBuffer;
    private XRDataBuffer? _counterBuffer;
    private XRDataBuffer? _aabbBuffer;
    private XRDataBuffer? _overflowFlagBuffer;
    private XRGpuFence? _pendingOverflowFence;
    private uint _pendingOverflowPrimitiveCount;
    private uint _pendingOverflowNodeCount;
    private uint _pendingOverflowAgeFrames;
    private bool _warnedPendingOverflowFenceDelay;

    private const uint PendingOverflowFenceDiagnosticFrames = 3u;

    // Shader programs
    private XRShader? _buildShader;
    private XRShader? _refitShader;
    private XRShader? _refineShader;
    private XRShader? _mortonShader;
    private XRShader? _smallSortShader;
    private XRShader? _padShader;
    private XRShader? _tileSortShader;
    private XRShader? _mergeShader;
    private XRRenderProgram? _buildProgram;
    private XRRenderProgram? _refitProgram;
    private XRRenderProgram? _refineProgram;
    private XRRenderProgram? _mortonProgram;
    private XRRenderProgram? _smallSortProgram;
    private XRRenderProgram? _padProgram;
    private XRRenderProgram? _tileSortProgram;
    private XRRenderProgram? _mergeProgram;

    // State tracking
    private uint _lastNodeCount;
    private uint _lastPrimitiveCount;
    private bool _isDirty = true;
    private BvhBuildMode _buildMode = BvhBuildMode.MortonOnly;
    private uint _maxLeafPrimitives = 1;

    /// <summary>
    /// Gets the GPU buffer containing BVH nodes.
    /// Layout: [nodeCount, rootIndex, nodeStrideScalars, maxLeafPrimitives, ...nodes]
    /// </summary>
    public XRDataBuffer? NodeBuffer => _nodeBuffer;

    /// <summary>
    /// Gets the GPU buffer containing primitive ranges for each leaf node.
    /// Layout: [start, count] pairs for each node.
    /// </summary>
    public XRDataBuffer? RangeBuffer => _rangeBuffer;

    /// <summary>
    /// Gets the GPU buffer containing Morton codes and object IDs.
    /// Layout: [mortonCode, objectId] pairs.
    /// </summary>
    public XRDataBuffer? MortonBuffer => _mortonBuffer;

    /// <summary>
    /// Gets the number of nodes currently in the BVH.
    /// </summary>
    public uint NodeCount => _lastNodeCount;

    /// <summary>
    /// Gets the number of primitives (leaf objects) in the BVH.
    /// </summary>
    public uint PrimitiveCount => _lastPrimitiveCount;

    /// <summary>
    /// Gets or sets the BVH construction mode.
    /// </summary>
    public BvhBuildMode BuildMode
    {
        get => _buildMode;
        set
        {
            if (_buildMode == value)
                return;
            _buildMode = value;
            // BVH_MODE is supplied as a uniform, not a specialization constant,
            // so existing shaders/programs stay valid. Just rebuild.
            MarkDirty();
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of primitives per leaf node.
    /// </summary>
    public uint MaxLeafPrimitives
    {
        get => _maxLeafPrimitives;
        set
        {
            uint clamped = Math.Max(1u, value);
            if (_maxLeafPrimitives == clamped)
                return;
            _maxLeafPrimitives = clamped;
            // MAX_LEAF_PRIMITIVES is a uniform, not a specialization constant,
            // so existing shaders/programs stay valid. Just rebuild.
            MarkDirty();
        }
    }

    /// <summary>
    /// Whether the BVH needs to be rebuilt.
    /// </summary>
    public bool IsDirty => _isDirty;

    /// <summary>
    /// Marks the BVH as needing a full rebuild.
    /// </summary>
    public void MarkDirty()
    {
        lock (_syncRoot)
            _isDirty = true;
    }

    /// <summary>
    /// Ensures the compute programs needed by the requested build path have linked.
    /// </summary>
    public bool EnsureProgramsReady(uint primitiveCount)
    {
        EnsurePrograms();

        bool ready = EnsureProgramReady(_mortonProgram) &&
            EnsureProgramReady(_buildProgram) &&
            EnsureProgramReady(_refitProgram);

        if (primitiveCount > 1u)
        {
            ready &= primitiveCount <= 1024u
                ? EnsureProgramReady(_smallSortProgram)
                : EnsureProgramReady(_padProgram) &&
                    EnsureProgramReady(_tileSortProgram) &&
                    EnsureProgramReady(_mergeProgram);
        }

        if (_buildMode == BvhBuildMode.MortonPlusSah)
            ready &= EnsureProgramReady(_refineProgram);

        return ready;
    }

    /// <summary>
    /// Builds or rebuilds the BVH from the provided AABB data.
    /// </summary>
    /// <param name="aabbBuffer">
    /// Buffer containing AABB data (vec4 min, vec4 max pairs). The caller retains
    /// ownership; this class holds a reference for subsequent <see cref="Refit"/>
    /// calls and the buffer must remain valid until the next <see cref="Build"/>,
    /// <see cref="Clear"/>, or <see cref="Dispose"/>.
    /// </param>
    /// <param name="primitiveCount">Number of primitives (AABBs) to build from.</param>
    /// <param name="sceneBounds">World-space bounds for Morton code normalization.</param>
    public void Build(XRDataBuffer aabbBuffer, uint primitiveCount, AABB sceneBounds)
    {
        lock (_syncRoot)
        {
            if (PollPendingOverflowCore())
                return;

            if (primitiveCount == 0)
            {
                ClearCore();
                return;
            }

            _aabbBuffer = aabbBuffer;
            // Bail before doing any GPU work if a required program failed to
            // link — otherwise individual Dispatch* methods silently skip and
            // we mark the BVH clean over a partial / stale build.
            if (!EnsureProgramsReady(primitiveCount))
                return;
            EnsureBuffers(primitiveCount);
            DropPendingOverflowFence("superseded by a new BVH build", warnIfOld: true);
            ResetOverflowFlagBuffer();

            Vector3 sceneMin = sceneBounds.Min;
            Vector3 sceneMax = sceneBounds.Max;
            // Expand any degenerate axis independently; Vector3 == is an exact
            // all-components compare and would miss e.g. a flat ground plane.
            const float DegenerateAxisEpsilon = 1e-6f;
            if (sceneMax.X - sceneMin.X < DegenerateAxisEpsilon) { sceneMin.X -= 0.5f; sceneMax.X += 0.5f; }
            if (sceneMax.Y - sceneMin.Y < DegenerateAxisEpsilon) { sceneMin.Y -= 0.5f; sceneMax.Y += 0.5f; }
            if (sceneMax.Z - sceneMin.Z < DegenerateAxisEpsilon) { sceneMin.Z -= 0.5f; sceneMax.Z += 0.5f; }

            // Morton code generation
            DispatchMortonCodes(primitiveCount, sceneMin, sceneMax);

            // Sort Morton codes (simple for now, can add radix sort later)
            SortMortonCodes(primitiveCount);

            // Build BVH hierarchy
            DispatchBuild(primitiveCount);

            // Optional SAH refinement
            if (_buildMode == BvhBuildMode.MortonPlusSah)
                DispatchRefine();

            // Refit bounds
            DispatchRefit();

            if (EnqueueOverflowFlagReadback(primitiveCount, _lastNodeCount))
                return;

            _lastPrimitiveCount = primitiveCount;
            _isDirty = false;
        }
    }

    /// <summary>
    /// Refits the BVH bounds without rebuilding the hierarchy.
    /// Use this for animated/skinned meshes where topology doesn't change.
    /// <para>
    /// The AABB buffer originally passed to <see cref="Build"/> must still be valid.
    /// See the class-level remarks for the lifetime contract.
    /// </para>
    /// </summary>
    public void Refit()
    {
        if (_lastNodeCount == 0 || _aabbBuffer is null)
            return;

        lock (_syncRoot)
        {
            // Refit only needs the refit program; don't link the morton / sort /
            // build / sah programs just to bounce bounds up the tree.
            if (!EnsureProgramReady(_refitProgram ??= CreateProgram(ref _refitShader, "Scene3D/RenderPipeline/bvh_refit.comp")))
                return;
            DispatchRefit();
            // bvh_refit.comp does not write to OverflowFlags (binding 8), so a
            // CPU readback here would always return 0 and only cost a pipeline
            // stall. Overflow is exclusively a build-time condition.
        }
    }

    /// <summary>
    /// Clears the BVH and releases GPU resources.
    /// </summary>
    public void Clear()
    {
        lock (_syncRoot)
            ClearCore();
    }

    private void ClearCore()
    {
        DropPendingOverflowFence("BVH cleared", warnIfOld: false);

        _lastNodeCount = 0;
        _lastPrimitiveCount = 0;
        _isDirty = true;

        ClearBuffer(_nodeBuffer);
        ClearBuffer(_rangeBuffer);
        ClearBuffer(_mortonBuffer);
    }

    /// <summary>
    /// Polls the pending asynchronous overflow readback, if any, without waiting
    /// for the GPU. Returns <c>true</c> when an overflow was observed and the BVH
    /// was cleared.
    /// </summary>
    public bool PollPendingOverflow()
    {
        lock (_syncRoot)
            return PollPendingOverflowCore();
    }

    private void EnsurePrograms()
    {
        _buildProgram ??= CreateProgram(ref _buildShader, "Scene3D/RenderPipeline/bvh_build.comp");
        _refitProgram ??= CreateProgram(ref _refitShader, "Scene3D/RenderPipeline/bvh_refit.comp");
        _refineProgram ??= CreateProgram(ref _refineShader, "Scene3D/RenderPipeline/bvh_sah_refine.comp");
        _mortonProgram ??= CreateProgram(ref _mortonShader, "Scene3D/RenderPipeline/OctreeGeneration/morton_codes.comp");
        _smallSortProgram ??= CreateProgram(ref _smallSortShader, "Scene3D/RenderPipeline/OctreeGeneration/sort_morton.comp");
        _padProgram ??= CreateProgram(ref _padShader, "Scene3D/RenderPipeline/OctreeGeneration/pad_morton.comp");
        _tileSortProgram ??= CreateProgram(ref _tileSortShader, "Scene3D/RenderPipeline/OctreeGeneration/sort_morton_tiles.comp");
        _mergeProgram ??= CreateProgram(ref _mergeShader, "Scene3D/RenderPipeline/OctreeGeneration/merge_morton.comp");
    }

    private void ResetPrograms()
    {
        // Destroy first to avoid leaking GPU resources whenever the BVH config
        // changes (BuildMode / MaxLeafPrimitives setters used to call this).
        _buildProgram?.Destroy();
        _refitProgram?.Destroy();
        _refineProgram?.Destroy();
        _mortonProgram?.Destroy();
        _smallSortProgram?.Destroy();
        _padProgram?.Destroy();
        _tileSortProgram?.Destroy();
        _mergeProgram?.Destroy();

        _buildShader?.Destroy();
        _refitShader?.Destroy();
        _refineShader?.Destroy();
        _mortonShader?.Destroy();
        _smallSortShader?.Destroy();
        _padShader?.Destroy();
        _tileSortShader?.Destroy();
        _mergeShader?.Destroy();

        _buildProgram = null;
        _refitProgram = null;
        _refineProgram = null;
        _buildShader = null;
        _refitShader = null;
        _refineShader = null;
        _mortonProgram = null;
        _smallSortProgram = null;
        _padProgram = null;
        _tileSortProgram = null;
        _mergeProgram = null;
        _mortonShader = null;
        _smallSortShader = null;
        _padShader = null;
        _tileSortShader = null;
        _mergeShader = null;
    }

    private void EnsureBuffers(uint primitiveCount)
    {
        uint leafCount = (primitiveCount + _maxLeafPrimitives - 1u) / _maxLeafPrimitives;
        uint nodeCount = leafCount > 0 ? (leafCount * 2u) - 1u : 0u;
        _lastNodeCount = nodeCount;

        // C-GPU-4 capacity headroom: the shader (bvh_build.comp) computes
        // `maxNodesByBuffer = (nodeScalarCapacity - header) / stride` and trips
        // OverflowBvhBit when `totalNodes > maxNodesByBuffer`. Allocating
        // exactly `2N-1` ties the boundary at zero slack, so any rounding,
        // stale count, or stage-3 malformed-tree detection (parent-pointer
        // cycle from duplicate Morton codes) is indistinguishable from real
        // capacity exhaustion. 8 extra node slots (~640 bytes) gives the
        // shader unambiguous headroom and frees the BvhBit signal to mean
        // exclusively "malformed tree".
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
        }
        else if (buffer.ElementCount < scalarCount)
        {
            buffer.Resize(scalarCount, false, true);
        }
    }

    private static void ClearBuffer(XRDataBuffer? buffer)
    {
        if (buffer is null)
            return;

        // Zero out the buffer without allocating a `new uint[count]` per call —
        // Clear() can be hit on the hot path whenever the BVH is invalidated.
        uint count = buffer.ElementCount;
        if (count == 0)
            return;

        for (uint i = 0; i < count; i++)
            buffer.SetDataRawAtIndex(i, 0u);
        buffer.PushSubData();
    }

    private void DispatchMortonCodes(uint primitiveCount, Vector3 sceneMin, Vector3 sceneMax)
    {
        if (_mortonProgram is null || _mortonBuffer is null || _aabbBuffer is null)
            return;

        var program = _mortonProgram;
        program.BindBuffer(_aabbBuffer, Bindings.Aabb);
        program.BindBuffer(_mortonBuffer, Bindings.Morton);
        program.Uniform("sceneMin", sceneMin);
        program.Uniform("sceneMax", sceneMax);
        program.Uniform("numObjects", primitiveCount);
        program.Uniform("mortonCapacity", GetMortonCapacity());
        if (_overflowFlagBuffer is not null)
            program.BindBuffer(_overflowFlagBuffer, Bindings.OverflowFlag);
        program.DispatchCompute(ComputeGroups(primitiveCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private void SortMortonCodes(uint primitiveCount)
    {
        if (primitiveCount <= 1 || _mortonBuffer is null)
            return;

        if (primitiveCount <= 1024)
        {
            var program = _smallSortProgram;
            if (program is null)
                return;
            program.BindBuffer(_mortonBuffer, Bindings.Morton);
            program.Uniform("numObjects", primitiveCount);
            program.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
            return;
        }

        uint paddedCount = Math.Max(1024u, NextPowerOfTwo(primitiveCount));
        var padProgram = _padProgram;
        if (padProgram is null)
            return;
        padProgram.BindBuffer(_mortonBuffer, Bindings.Morton);
        padProgram.Uniform("numObjects", primitiveCount);
        padProgram.Uniform("paddedCount", paddedCount);
        padProgram.DispatchCompute(ComputeGroups(paddedCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        var tileProgram = _tileSortProgram;
        if (tileProgram is null)
            return;
        tileProgram.BindBuffer(_mortonBuffer, Bindings.Morton);
        tileProgram.Uniform("paddedCount", paddedCount);
        tileProgram.DispatchCompute(Math.Max(1u, paddedCount / 1024u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        var mergeProgram = _mergeProgram;
        if (mergeProgram is null)
            return;
        mergeProgram.BindBuffer(_mortonBuffer, Bindings.Morton);
        mergeProgram.Uniform("paddedCount", paddedCount);
        for (uint k = 2048u; k <= paddedCount; k <<= 1)
        {
            mergeProgram.Uniform("K", k);
            for (uint j = k >> 1; j > 0; j >>= 1)
            {
                mergeProgram.Uniform("J", j);
                mergeProgram.DispatchCompute(ComputeGroups(paddedCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
            }
        }
    }

    private uint GetMortonCapacity()
        => _mortonBuffer is null ? 0u : _mortonBuffer.ElementCount / 2u;

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

        // Strict zero-readback profiling cannot map even a 4-byte overflow flag.
        // Capacity is sized conservatively up front; keep the BVH GPU-resident.
        if (RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy() == EMeshSubmissionStrategy.GpuIndirectZeroReadback)
            return false;

        // Map only after a queued GPU fence has already signaled. Backends that
        // cannot expose a fence fall back to the old synchronous read for safety.
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
    /// Reads the overflow flag by temporarily mapping the GPU buffer.
    /// The buffer was configured with <see cref="EBufferMapRangeFlags.Read"/>
    /// so <c>glMapNamedBufferRange</c> will succeed with <c>GL_MAP_READ_BIT</c>.
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
                        // Phase B invariant: every GPU->CPU readback must be recorded in
                        // Engine.Rendering.Stats so GpuReadbackBytes stays honest under the
                        // instrumented strategy. This is a 4-byte read, but it counts.
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
    /// C-GPU-3 trace: dump capacity-vs-required figures so the operator can tell
    /// whether the overflow is real capacity exhaustion or stage-3 malformed-tree
    /// detection (parent-pointer cycle from duplicate Morton codes or stage-2 race).
    /// Both conditions set <see cref="OverflowBvhBit"/> in bvh_build.comp; only this
    /// trace can distinguish them.
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

    private void DispatchBuild(uint primitiveCount)
    {
        if (_buildProgram is null || _nodeBuffer is null || _rangeBuffer is null || _aabbBuffer is null)
            return;

        uint leafCount = (primitiveCount + _maxLeafPrimitives - 1u) / _maxLeafPrimitives;
        uint internalCount = leafCount > 0 ? leafCount - 1u : 0u;
        if (leafCount == 0)
            return;

        var program = _buildProgram;
        program.BindBuffer(_aabbBuffer, Bindings.Aabb);
        program.BindBuffer(_mortonBuffer!, Bindings.Morton);
        program.BindBuffer(_nodeBuffer, Bindings.Node);
        program.BindBuffer(_rangeBuffer, Bindings.Range);
        if (_overflowFlagBuffer is not null)
            program.BindBuffer(_overflowFlagBuffer, Bindings.OverflowFlag);
        program.Uniform("numPrimitives", primitiveCount);
        program.Uniform("nodeScalarCapacity", _nodeBuffer.ElementCount);
        program.Uniform("rangeScalarCapacity", _rangeBuffer.ElementCount);
        program.Uniform("mortonCapacity", GetMortonCapacity());
        
        // Set BVH configuration uniforms (OpenGL-compatible replacement for Vulkan specialization constants)
        program.Uniform("MAX_LEAF_PRIMITIVES", _maxLeafPrimitives);
        program.Uniform("BVH_MODE", _buildMode == BvhBuildMode.MortonPlusSah ? 1u : 0u);

        // Stage 0: Initialize leaves
        program.Uniform("buildStage", 0u);
        program.DispatchCompute(ComputeGroups(Math.Max(leafCount, 1u), 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        if (internalCount > 0)
        {
            // Stage 1: Build internal nodes
            program.Uniform("buildStage", 1u);
            program.DispatchCompute(ComputeGroups(internalCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

            // Stage 2: Assign parents
            program.Uniform("buildStage", 2u);
            program.DispatchCompute(ComputeGroups(internalCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }

        // Stage 3: Compute root index
        program.Uniform("buildStage", 3u);
        program.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private void DispatchRefine()
    {
        if (_buildMode != BvhBuildMode.MortonPlusSah)
            return;

        if (_refineProgram is null || _nodeBuffer is null || _rangeBuffer is null || _lastNodeCount == 0)
            return;

        var program = _refineProgram;
        program.BindBuffer(_aabbBuffer!, Bindings.Aabb);
        program.BindBuffer(_mortonBuffer!, Bindings.Morton);
        program.BindBuffer(_nodeBuffer, Bindings.Node);
        program.BindBuffer(_rangeBuffer, Bindings.Range);
        if (_overflowFlagBuffer is not null)
            program.BindBuffer(_overflowFlagBuffer, Bindings.OverflowFlag);
        
        // Set BVH configuration uniforms (OpenGL-compatible replacement for Vulkan specialization constants)
        program.Uniform("MAX_LEAF_PRIMITIVES", _maxLeafPrimitives);
        program.Uniform("BVH_MODE", _buildMode == BvhBuildMode.MortonPlusSah ? 1u : 0u);
        
        program.DispatchCompute(ComputeGroups(_lastNodeCount, 128u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private void DispatchRefit()
    {
        if (_refitProgram is null || _nodeBuffer is null || _rangeBuffer is null || _counterBuffer is null || _lastNodeCount == 0)
            return;

        var program = _refitProgram;
        program.BindBuffer(_aabbBuffer!, Bindings.Aabb);
        program.BindBuffer(_mortonBuffer!, Bindings.Morton);
        program.BindBuffer(_nodeBuffer, Bindings.Node);
        program.BindBuffer(_rangeBuffer, Bindings.Range);
        program.BindBuffer(_counterBuffer, Bindings.Counters);
        // bvh_refit.comp does not declare an OverflowFlags binding; nothing to bind here.
        program.Uniform("debugValidation", 0u);
        
        // Set BVH configuration uniforms (OpenGL-compatible replacement for Vulkan specialization constants)
        program.Uniform("MAX_LEAF_PRIMITIVES", _maxLeafPrimitives);
        program.Uniform("BVH_MODE", _buildMode == BvhBuildMode.MortonPlusSah ? 1u : 0u);

        // Stage 0: Clear counters
        program.Uniform("refitStage", 0u);
        program.DispatchCompute(ComputeGroups(_lastNodeCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        // Stage 1: Refit leaves and propagate
        program.Uniform("refitStage", 1u);
        program.DispatchCompute(ComputeGroups(_lastNodeCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private static uint ComputeGroups(uint count, uint localSize)
        => (count + localSize - 1u) / localSize;

    private static XRRenderProgram CreateProgram(ref XRShader? shader, string path)
    {
        shader ??= ShaderHelper.LoadEngineShader(path, EShaderType.Compute);
        return new XRRenderProgram(true, false, shader);
    }

    private static bool EnsureProgramReady(XRRenderProgram? program)
    {
        if (program is null)
            return false;

        if (program.IsLinked)
            return true;

        if (!program.LinkReady)
            program.Link();

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _nodeBuffer?.Dispose();
        _rangeBuffer?.Dispose();
        _mortonBuffer?.Dispose();
        _counterBuffer?.Dispose();
        _overflowFlagBuffer?.Dispose();
        _pendingOverflowFence?.Dispose();

        _buildShader?.Destroy();
        _refitShader?.Destroy();
        _refineShader?.Destroy();
        _mortonShader?.Destroy();
        _smallSortShader?.Destroy();
        _padShader?.Destroy();
        _tileSortShader?.Destroy();
        _mergeShader?.Destroy();

        _buildProgram?.Destroy();
        _refitProgram?.Destroy();
        _refineProgram?.Destroy();
        _mortonProgram?.Destroy();
        _smallSortProgram?.Destroy();
        _padProgram?.Destroy();
        _tileSortProgram?.Destroy();
        _mergeProgram?.Destroy();

        _nodeBuffer = null;
        _rangeBuffer = null;
        _mortonBuffer = null;
        _counterBuffer = null;
        _overflowFlagBuffer = null;
        _pendingOverflowFence = null;
        _aabbBuffer = null;
    }
}

/// <summary>
/// Interface for objects that can provide BVH data for GPU culling.
/// </summary>
public interface IGpuBvhProvider
{
    /// <summary>
    /// Gets the BVH node buffer for GPU traversal.
    /// </summary>
    XRDataBuffer? BvhNodeBuffer { get; }

    /// <summary>
    /// Gets the primitive range buffer for leaf node lookups.
    /// </summary>
    XRDataBuffer? BvhRangeBuffer { get; }

    /// <summary>
    /// Gets the Morton code buffer with object IDs.
    /// </summary>
    XRDataBuffer? BvhMortonBuffer { get; }

    /// <summary>
    /// Gets the number of nodes in the BVH.
    /// </summary>
    uint BvhNodeCount { get; }

    /// <summary>
    /// Whether the BVH is ready for use.
    /// </summary>
    bool IsBvhReady { get; }
}
