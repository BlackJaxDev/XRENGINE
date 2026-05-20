using System;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Compute;

/// <summary>
/// A fully GPU-based BVH tree usable for scene-level culling, per-model
/// collision, skinned mesh BVH, and other spatial acceleration needs.
/// </summary>
/// <remarks>
/// Builds and maintains a binary tree on the GPU using compute shaders. The
/// tree supports:
/// <list type="bullet">
///   <item>Morton-code based radix construction.</item>
///   <item>Optional SAH refinement.</item>
///   <item>Incremental refit for animated/skinned meshes.</item>
/// </list>
/// Traversal (frustum / ray) is performed by consumer shaders against the
/// exposed node/range/morton buffers; no CPU traversal API is provided here.
/// <para>
/// <b>AABB buffer lifetime contract:</b> the caller owns <c>aabbBuffer</c>
/// passed to <see cref="Build"/>. This class retains a reference for use by
/// <see cref="Refit"/> and the buffer must remain valid (not disposed, not
/// reallocated) until the next <see cref="Build"/> call replaces it,
/// <see cref="Clear"/> is called, or this object is disposed.
/// </para>
/// <para>
/// The implementation is split across partial files by concern:
/// <list type="bullet">
///   <item><c>GpuBvhTree.cs</c> — public lifecycle (Build / Refit / Clear),
///         state, Dispose orchestration, and the shared <see cref="Bindings"/>
///         contract.</item>
///   <item><c>BvhBuildMode.cs</c> — <see cref="BvhBuildMode"/> enum.</item>
///   <item><c>IGpuBvhProvider.cs</c> — <see cref="IGpuBvhProvider"/> consumer
///         interface.</item>
///   <item><c>GpuBvhTree.Programs.cs</c> — shader / program loading,
///         link-readiness polling, and teardown.</item>
///   <item><c>GpuBvhTree.Buffers.cs</c> — SSBO sizing and lifetime.</item>
///   <item><c>GpuBvhTree.Dispatch.cs</c> — per-stage compute dispatches
///         (Morton, sort, build, refine, refit).</item>
///   <item><c>GpuBvhTree.Overflow.cs</c> — async overflow-flag readback,
///         fence management, and diagnostics.</item>
/// </list>
/// </para>
/// </remarks>
public sealed partial class GpuBvhTree : IDisposable
{
    /// <summary>
    /// Shader SSBO binding points. These must match the
    /// <c>layout(std430, binding = N)</c> declarations in every BVH compute
    /// shader (<c>bvh_build.comp</c>, <c>bvh_refit.comp</c>,
    /// <c>bvh_sah_refine.comp</c>, <c>morton_codes.comp</c>,
    /// <c>sort_morton*.comp</c>, <c>pad_morton.comp</c>,
    /// <c>merge_morton.comp</c>, <c>merge_morton_local.comp</c>).
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

    private bool _disposed;
    private readonly object _syncRoot = new();

    // Build state (mutated under _syncRoot during Build).
    private uint _lastNodeCount;
    private uint _lastPrimitiveCount;
    private bool _isDirty = true;
    private BvhBuildMode _buildMode = BvhBuildMode.MortonOnly;
    private uint _maxLeafPrimitives = 1;

    /// <summary>Number of nodes in the most recently built BVH.</summary>
    public uint NodeCount => _lastNodeCount;

    /// <summary>Number of primitives (leaf objects) in the most recently built BVH.</summary>
    public uint PrimitiveCount => _lastPrimitiveCount;

    /// <summary>Construction mode for the next build.</summary>
    public BvhBuildMode BuildMode
    {
        get => _buildMode;
        set
        {
            if (_buildMode == value)
                return;
            _buildMode = value;
            // BVH_MODE is a plain uniform, not a specialization constant, so
            // existing shaders/programs stay valid. Just rebuild.
            MarkDirty();
        }
    }

    /// <summary>Maximum number of primitives stored per leaf node.</summary>
    public uint MaxLeafPrimitives
    {
        get => _maxLeafPrimitives;
        set
        {
            uint clamped = Math.Max(1u, value);
            if (_maxLeafPrimitives == clamped)
                return;
            _maxLeafPrimitives = clamped;
            // MAX_LEAF_PRIMITIVES is a plain uniform; rebuild only.
            MarkDirty();
        }
    }

    /// <summary>True when the BVH needs to be rebuilt.</summary>
    public bool IsDirty => _isDirty;

    /// <summary>Marks the BVH as needing a full rebuild.</summary>
    public void MarkDirty()
    {
        lock (_syncRoot)
            _isDirty = true;
    }

    /// <summary>
    /// Builds or rebuilds the BVH from the provided AABB data.
    /// </summary>
    /// <param name="aabbBuffer">
    /// Buffer containing AABB data (vec4 min, vec4 max pairs). The caller
    /// retains ownership; this class holds a reference for subsequent
    /// <see cref="Refit"/> calls. The buffer must remain valid until the next
    /// <see cref="Build"/>, <see cref="Clear"/>, or <see cref="Dispose"/>.
    /// </param>
    /// <param name="primitiveCount">Number of primitives (AABBs) to build from.</param>
    /// <param name="sceneBounds">World-space bounds for Morton code normalization.</param>
    public void Build(XRDataBuffer aabbBuffer, uint primitiveCount, AABB sceneBounds)
    {
        lock (_syncRoot)
        {
            // Consume any prior async overflow flag first; that result tells
            // us whether the previous build actually completed.
            if (PollPendingOverflowCore())
                return;

            if (primitiveCount == 0)
            {
                ClearCore();
                return;
            }

            _aabbBuffer = aabbBuffer;

            // Bail before any GPU work if a required program failed to link
            // — otherwise the individual Dispatch* methods would silently
            // skip and we would mark the BVH clean over a partial / stale
            // build.
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

            // GPU build pipeline.
            DispatchMortonCodes(primitiveCount, sceneMin, sceneMax);
            SortMortonCodes(primitiveCount);
            DispatchBuild(primitiveCount);
            if (_buildMode == BvhBuildMode.MortonPlusSah)
                DispatchRefine();
            DispatchRefit();

            // If we observed an overflow synchronously (no fence support),
            // the BVH has been wiped — bail and let the caller fall back.
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
    /// The AABB buffer originally passed to <see cref="Build"/> must still be
    /// valid. See the class-level remarks for the lifetime contract.
    /// </para>
    /// </summary>
    public void Refit()
    {
        if (_lastNodeCount == 0 || _aabbBuffer is null)
            return;

        lock (_syncRoot)
        {
            // Refit only needs the refit program; don't link the morton /
            // sort / build / sah programs just to bounce bounds up the tree.
            if (!EnsureProgramReady(_refitProgram ??= CreateProgram(ref _refitShader, "Scene3D/RenderPipeline/bvh_refit.comp")))
                return;
            DispatchRefit();
            // bvh_refit.comp does not write to OverflowFlags (binding 8), so
            // a CPU readback here would always return 0 and only cost a
            // pipeline stall. Overflow is exclusively a build-time condition.
        }
    }

    /// <summary>Clears the BVH state and zeroes the GPU buffers.</summary>
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Each subsystem owns its tear-down so this orchestrator stays small.
        DisposeBuffersCore();
        DisposeOverflowCore();
        DisposeProgramsCore();
    }
}
