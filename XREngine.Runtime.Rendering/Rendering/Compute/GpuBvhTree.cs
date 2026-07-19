using System;
using System.Numerics;
using System.Threading;
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
///         (Morton, sort, build, refit).</item>
///   <item><c>GpuBvhTree.Overflow.cs</c> — async overflow-flag readback,
///         fence management, and diagnostics.</item>
/// </list>
/// </para>
/// </remarks>
public sealed partial class GpuBvhTree : IDisposable
{
    private static long s_nextDiagnosticId;

    /// <summary>Refit interval for the GPU-resident O(nodes) quality snapshot.</summary>
    public const uint QualityAnalysisRefitCadence = 30u;
    /// <summary>
    /// Shader SSBO binding points. These must match the
    /// <c>layout(std430, binding = N)</c> declarations in every BVH compute
    /// shader (<c>bvh_build.comp</c>, <c>bvh_refit.comp</c>,
    /// <c>morton_codes.comp</c>, <c>sort_morton.comp</c>, and
    /// <c>radix_morton_*.comp</c>).
    /// </summary>
    internal static class Bindings
    {
        public const uint Aabb = 0u;
        public const uint Morton = 1u;
        public const uint Node = 2u;
        public const uint OverflowFlag = 8u;
        public const uint Counters = 11u;
        public const uint RadixScratch = 12u;
        public const uint RadixOffsets = 13u;
        public const uint QualityDiagnostics = 14u;
    }

    private bool _disposed;
    private readonly object _syncRoot = new();
    private readonly string _diagnosticName;
    private readonly string _nodeBufferName;
    private readonly string _mortonBufferName;
    private readonly string _counterBufferName;
    private readonly string _radixScratchBufferName;
    private readonly string _radixOffsetsBufferName;
    private readonly string _qualityDiagnosticsBufferName;
    private readonly string _overflowFlagBufferName;

    // Build state (mutated under _syncRoot during Build).
    private uint _lastNodeCount;
    private uint _lastPrimitiveCount;
    private bool _isDirty = true;
    private BvhBuildMode _buildMode = BvhBuildMode.MortonOnly;
    private uint _maxLeafPrimitives = 1;
    private ulong _buildCount;
    private ulong _refitCount;
    private ulong _skippedCleanFrameCount;
    private ulong _clearCount;
    private ulong _bufferReallocationCount;
    private ulong _initialBuildCount;
    private ulong _topologyChangeRebuildCount;
    private ulong _normalizationEscapeRebuildCount;
    private ulong _periodicQualityRebuildCount;
    private uint _lastDirtyLeafCount;
    private ulong _lastAabbUploadBytes;
    private ulong _lastAabbCopyBytes;
    private ulong _synchronousReadbackBytes;
    private ulong _asynchronousReadbackBytes;
    private ulong _zeroReadbackSubmissionCount;
    private GpuBvhRebuildReason _pendingRebuildReason = GpuBvhRebuildReason.InitialOrUnavailable;
    private uint _qualityAnalysisRevision;
    private ulong _qualityAnalysisCount;
    private GpuBvhBuildIdentity _lastBuildIdentity;
    private bool _buildPendingResources;

    /// <summary>Creates a GPU BVH with a stable label used by backend resource diagnostics.</summary>
    public GpuBvhTree(string ownerName = "Unowned")
    {
        long id = Interlocked.Increment(ref s_nextDiagnosticId);
        _diagnosticName = $"GpuBvhTree[{ownerName}:{id}]";
        _nodeBufferName = $"{_diagnosticName}.Nodes";
        _mortonBufferName = $"{_diagnosticName}.Morton";
        _counterBufferName = $"{_diagnosticName}.Counters";
        _radixScratchBufferName = $"{_diagnosticName}.RadixScratch";
        _radixOffsetsBufferName = $"{_diagnosticName}.RadixOffsets";
        _qualityDiagnosticsBufferName = $"{_diagnosticName}.QualityDiagnostics";
        _overflowFlagBufferName = $"{_diagnosticName}.OverflowFlag";
    }

    /// <summary>Number of nodes in the most recently built BVH.</summary>
    public uint NodeCount => _lastNodeCount;

    /// <summary>Number of primitives (leaf objects) in the most recently built BVH.</summary>
    public uint PrimitiveCount => _lastPrimitiveCount;

    /// <summary>
    /// True while a required buffer upload is still being published. Callers
    /// must retry the build rather than classify this transient state as a
    /// capacity or topology failure.
    /// </summary>
    public bool IsBuildPendingResources => _buildPendingResources;

    /// <summary>Returns the current lifecycle, logical-use, and retained-capacity counters.</summary>
    public GpuBvhDiagnostics Diagnostics
    {
        get
        {
            lock (_syncRoot)
                return CreateDiagnostics();
        }
    }

    /// <summary>Construction mode for the next build.</summary>
    public BvhBuildMode BuildMode
    {
        get => _buildMode;
        set
        {
            if (value == BvhBuildMode.MortonPlusSah)
                throw new NotSupportedException("MortonPlusSah is disabled because it does not preserve valid topology for multi-primitive leaves.");

            if (_buildMode == value)
                return;
            _buildMode = value;
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
            // This flag describes only the current build attempt. Validation,
            // clear, and program failures must not inherit a prior allocation wait.
            _buildPendingResources = false;

            // Consume any prior async overflow flag first; that result tells
            // us whether the previous build actually completed.
            if (PollPendingOverflowCore())
                return;

            if (primitiveCount == 0)
            {
                ClearCore();
                return;
            }

            if (!TryNormalizeSceneBounds(sceneBounds, out Vector3 sceneMin, out Vector3 sceneMax))
            {
                Debug.LogWarning("[GpuBvhTree] Refusing to build with invalid or non-finite Morton normalization bounds.");
                return;
            }

            // A scene BVH can be requested by more than one pipeline consumer
            // in a frame. Identical clean inputs already describe the published
            // tree, so do not reset/upload the overflow flag or resubmit compute.
            if (CanReuseCompletedBuild(_isDirty, _lastBuildIdentity, aabbBuffer, primitiveCount, sceneMin, sceneMax))
            {
                _skippedCleanFrameCount++;
                return;
            }

            _aabbBuffer = aabbBuffer;

            // Bail before any GPU work if a required program failed to link.
            if (!EnsureProgramsReady(primitiveCount))
                return;

            EnsureBuffers(primitiveCount);
            _lastPrimitiveCount = primitiveCount;
            DropPendingOverflowFence("superseded by a new BVH build", warnIfOld: true);
            if (!IsOverflowFlagBufferReady())
            {
                _buildPendingResources = true;
                return;
            }

            using var cpuSubmission = BvhGpuProfiler.Instance.SubmissionScope(BvhGpuProfiler.Stage.Build);
            using var gpuTiming = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Build, 1u);

            // GPU build pipeline.
            DispatchMortonCodes(primitiveCount, sceneMin, sceneMax);
            SortMortonCodes(primitiveCount);
            DispatchBuild(primitiveCount);
            DispatchRefit();
            DispatchQualityAnalysis(_pendingRebuildReason);

            // If we observed an overflow synchronously (no fence support),
            // the BVH has been wiped — bail and let the caller fall back.
            if (EnqueueOverflowFlagReadback(primitiveCount, _lastNodeCount))
                return;

            _isDirty = false;
            _lastBuildIdentity = new GpuBvhBuildIdentity(aabbBuffer, primitiveCount, sceneMin, sceneMax);
            _buildCount++;
            RecordConsumedRebuildReason(_pendingRebuildReason);
            _pendingRebuildReason = GpuBvhRebuildReason.None;
        }
    }

    internal static bool TryNormalizeSceneBounds(AABB bounds, out Vector3 min, out Vector3 max)
    {
        min = bounds.Min;
        max = bounds.Max;
        if (!IsFinite(min) || !IsFinite(max) ||
            min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            return false;

        const float DegenerateAxisEpsilon = 1e-6f;
        if (max.X - min.X < DegenerateAxisEpsilon) { min.X -= 0.5f; max.X += 0.5f; }
        if (max.Y - min.Y < DegenerateAxisEpsilon) { min.Y -= 0.5f; max.Y += 0.5f; }
        if (max.Z - min.Z < DegenerateAxisEpsilon) { min.Z -= 0.5f; max.Z += 0.5f; }
        return true;
    }

    private static bool IsFinite(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    internal static bool CanReuseCompletedBuild(
        bool isDirty,
        in GpuBvhBuildIdentity identity,
        XRDataBuffer aabbBuffer,
        uint primitiveCount,
        in Vector3 sceneMin,
        in Vector3 sceneMax)
        => !isDirty && identity.Matches(aabbBuffer, primitiveCount, sceneMin, sceneMax);

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
            using var cpuSubmission = BvhGpuProfiler.Instance.SubmissionScope(BvhGpuProfiler.Stage.Refit);
            using var gpuTiming = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Refit, 1u);
            // Refit only needs the refit program; don't link the morton /
            // sort / build / sah programs just to bounce bounds up the tree.
            if (!EnsureProgramReady(_refitProgram ??= CreateProgram(ref _refitShader, "Scene3D/RenderPipeline/bvh_refit.comp")))
                return;
            DispatchRefit();
            if ((_refitCount + 1u) % QualityAnalysisRefitCadence == 0u)
                DispatchQualityAnalysis(GpuBvhRebuildReason.None);
            _refitCount++;
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

        if (_lastNodeCount == 0 && _lastPrimitiveCount == 0)
            return;

        _lastNodeCount = 0;
        _lastPrimitiveCount = 0;
        _isDirty = true;
        _clearCount++;

        ClearNodeHeader(_nodeBuffer);
    }

    internal void RecordCleanFrameSkipped()
    {
        lock (_syncRoot)
            _skippedCleanFrameCount++;
    }

    internal void RecordRebuildReason(GpuBvhRebuildReason reason)
    {
        lock (_syncRoot)
        {
            _pendingRebuildReason = reason;
            if (reason != GpuBvhRebuildReason.None)
                _isDirty = true;
        }
    }

    internal void RecordRefitTransfer(uint dirtyLeafCount, ulong uploadBytes, ulong copyBytes)
    {
        lock (_syncRoot)
        {
            _lastDirtyLeafCount = dirtyLeafCount;
            _lastAabbUploadBytes = uploadBytes;
            _lastAabbCopyBytes = copyBytes;
        }
    }

    private void RecordConsumedRebuildReason(GpuBvhRebuildReason reason)
    {
        switch (reason)
        {
            case GpuBvhRebuildReason.InitialOrUnavailable:
                _initialBuildCount++;
                break;
            case GpuBvhRebuildReason.TopologyChanged:
                _topologyChangeRebuildCount++;
                break;
            case GpuBvhRebuildReason.NormalizationDomainEscaped:
                _normalizationEscapeRebuildCount++;
                break;
            case GpuBvhRebuildReason.PeriodicQualityCeiling:
                _periodicQualityRebuildCount++;
                break;
        }
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
