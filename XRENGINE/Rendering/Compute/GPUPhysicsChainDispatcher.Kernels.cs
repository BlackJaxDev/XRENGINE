using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private XRShader? _branchedPhysicsShader;
    private XRRenderProgram? _branchedPhysicsProgram;
    private XRDataBuffer<PhysicsChainGpuDepthRange>? _depthRangeBuffer;
    private XRDataBuffer<uint>? _depthParticleIdBuffer;
    private readonly List<PhysicsChainGpuDepthRange> _depthRanges = [];
    private readonly List<uint> _depthParticleIds = [];
    private readonly List<int> _treeDepthRangeOffsets = [];
    private readonly List<int> _treeDepthRangeCounts = [];
    private readonly List<int> _depthScratch = [];
    private readonly List<int> _depthCountsScratch = [];
    private readonly List<int> _depthCursorsScratch = [];
    private readonly int[] _kernelCandidateCounts = new int[(int)PhysicsChainKernelBucket.Count];
    private int _depthTopologySignature = int.MinValue;
    private int _depthTopologyResourceGeneration = -1;
    private bool _depthTopologyDirty;
    private int _shortLinearIndirectDispatchCount;
    private int _branchedOrLongIndirectDispatchCount;
    private int _kernelSimulationBarrierCount;

    /// <summary>
    /// Reports fixed CPU-side command submissions without reading dynamic GPU counters.
    /// </summary>
    public PhysicsChainKernelDispatchDiagnostics GetKernelDispatchDiagnosticsSnapshot()
        => new(
            _shortLinearIndirectDispatchCount,
            _branchedOrLongIndirectDispatchCount,
            _kernelSimulationBarrierCount,
            DynamicWorkgroupCountsRemainGpuAuthored: true);

    private void EnsureSpecializedKernelInitialized()
    {
        if (_branchedPhysicsProgram is not null)
            return;

        _branchedPhysicsShader = ShaderHelper.LoadEngineShader(
            "Compute/PhysicsChain/PhysicsChainBranched.comp",
            EShaderType.Compute);
        _branchedPhysicsProgram = new XRRenderProgram(true, false, _branchedPhysicsShader);
    }

    private bool IsSpecializedKernelReady()
    {
        if (_branchedPhysicsProgram is not { IsLinked: false } program)
            return true;
        if (!program.LinkReady)
            program.Link();
        return false;
    }

    private bool EnsureSpecializedKernelResources(
        IPhysicsChainComputeBackend backend,
        IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (!BuildDepthTopologyIfNeeded(requests))
            return false;

        if (!EnsureArenaCapacity(
                ref _depthRangeBuffer,
                "PhysicsChainDepthRanges",
                Math.Max(_depthRanges.Count, 1),
                preserveElementCount: 0,
                EBufferUsage.StaticDraw,
                backend)
            || !EnsureArenaCapacity(
                ref _depthParticleIdBuffer,
                "PhysicsChainDepthParticleIds",
                Math.Max(_depthParticleIds.Count, 1),
                preserveElementCount: 0,
                EBufferUsage.StaticDraw,
                backend))
            return false;

        ApplyDepthRangeHeaders();
        if (!_depthTopologyDirty && _depthTopologyResourceGeneration == _arenaResourceGeneration)
            return true;

        if (_depthRanges.Count > 0)
        {
            uint bytes = _depthRangeBuffer!.WriteDataRaw(CollectionsMarshal.AsSpan(_depthRanges));
            PushBufferUpdate(_depthRangeBuffer, fullPush: false, bytes);
            RecordArenaUpload(bytes, isStatic: true);
        }
        if (_depthParticleIds.Count > 0)
        {
            uint bytes = _depthParticleIdBuffer!.WriteDataRaw(CollectionsMarshal.AsSpan(_depthParticleIds));
            PushBufferUpdate(_depthParticleIdBuffer, fullPush: false, bytes);
            RecordArenaUpload(bytes, isStatic: true);
        }

        _depthTopologyDirty = false;
        _depthTopologyResourceGeneration = _arenaResourceGeneration;
        return true;
    }

    private bool BuildDepthTopologyIfNeeded(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        var signature = new HashCode();
        signature.Add(requests.Count);
        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            signature.Add(request.RequestId);
            signature.Add(request.StaticDataVersion);
            signature.Add(request.ArenaAllocationGeneration);
            signature.Add(request.ParticleOffset);
            signature.Add(request.Trees.Count);
        }

        int resolvedSignature = signature.ToHashCode();
        if (resolvedSignature == _depthTopologySignature
            && _treeDepthRangeOffsets.Count == _allPerTreeParams.Count)
            return true;

        _depthRanges.Clear();
        _depthParticleIds.Clear();
        _treeDepthRangeOffsets.Clear();
        _treeDepthRangeCounts.Clear();
        _treeDepthRangeOffsets.EnsureCapacity(_allPerTreeParams.Count);
        _treeDepthRangeCounts.EnsureCapacity(_allPerTreeParams.Count);

        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            for (int treeIndex = 0; treeIndex < request.Trees.Count; ++treeIndex)
            {
                GPUParticleTreeData tree = request.Trees[treeIndex];
                if (!AppendTreeDepthTopology(request, tree))
                {
                    XREngine.Debug.PhysicsWarning(
                        $"[GPUPhysicsChainDispatcher] Invalid parent ordering while building depth ranges. RequestId={request.RequestId}, Tree={treeIndex}. GPU work remains pending; no fallback was used.");
                    return false;
                }
            }
        }

        _depthTopologySignature = resolvedSignature;
        _depthTopologyDirty = true;
        return true;
    }

    private bool AppendTreeDepthTopology(GPUPhysicsChainRequest request, in GPUParticleTreeData tree)
    {
        int particleCount = tree.ParticleCount;
        int sourceStart = tree.ParticleOffset;
        if (particleCount <= 0 || sourceStart < 0 || sourceStart > request.ParticleStaticData.Count - particleCount)
            return false;

        EnsureScratchCount(_depthScratch, particleCount);
        Span<int> depths = CollectionsMarshal.AsSpan(_depthScratch).Slice(0, particleCount);
        if (!TryBuildTreeDepthOrder(request.ParticleStaticData, tree, depths, out int depthCount))
            return false;

        EnsureScratchCount(_depthCountsScratch, depthCount);
        EnsureScratchCount(_depthCursorsScratch, depthCount);
        Span<int> counts = CollectionsMarshal.AsSpan(_depthCountsScratch).Slice(0, depthCount);
        Span<int> cursors = CollectionsMarshal.AsSpan(_depthCursorsScratch).Slice(0, depthCount);
        counts.Clear();
        for (int particleIndex = 0; particleIndex < particleCount; ++particleIndex)
            ++counts[depths[particleIndex]];

        int treeParticleIdStart = _depthParticleIds.Count;
        _depthParticleIds.EnsureCapacity(treeParticleIdStart + particleCount);
        for (int particleIndex = 0; particleIndex < particleCount; ++particleIndex)
            _depthParticleIds.Add(0u);

        int cursor = treeParticleIdStart;
        for (int depth = 0; depth < depthCount; ++depth)
        {
            cursors[depth] = cursor;
            _depthRanges.Add(new PhysicsChainGpuDepthRange
            {
                ParticleIdOffset = (uint)cursor,
                ParticleCount = (uint)counts[depth],
            });
            cursor += counts[depth];
        }

        Span<uint> particleIds = CollectionsMarshal.AsSpan(_depthParticleIds);
        for (int particleIndex = 0; particleIndex < particleCount; ++particleIndex)
        {
            int depth = depths[particleIndex];
            particleIds[cursors[depth]++] = (uint)(request.ParticleOffset + sourceStart + particleIndex);
        }

        _treeDepthRangeOffsets.Add(_depthRanges.Count - depthCount);
        _treeDepthRangeCounts.Add(depthCount);
        return true;
    }

    internal static bool TryBuildTreeDepthOrder(
        IReadOnlyList<GPUParticleStaticData> particles,
        in GPUParticleTreeData tree,
        Span<int> destinationDepths,
        out int depthCount)
    {
        depthCount = 0;
        int start = tree.ParticleOffset;
        int count = tree.ParticleCount;
        if (count <= 0 || start < 0 || start > particles.Count - count || destinationDepths.Length < count)
            return false;

        int maximumDepth = 0;
        for (int localIndex = 0; localIndex < count; ++localIndex)
        {
            int sourceIndex = start + localIndex;
            int parent = particles[sourceIndex].ParentIndex;
            int parentLocalIndex;
            if (parent < 0)
            {
                destinationDepths[localIndex] = 0;
                continue;
            }
            if (parent >= start && parent < sourceIndex)
                parentLocalIndex = parent - start;
            else if (parent < localIndex)
                parentLocalIndex = parent;
            else
                return false;

            int depth = destinationDepths[parentLocalIndex] + 1;
            destinationDepths[localIndex] = depth;
            maximumDepth = Math.Max(maximumDepth, depth);
        }

        depthCount = maximumDepth + 1;
        return true;
    }

    private static void EnsureScratchCount(List<int> scratch, int count)
    {
        scratch.EnsureCapacity(count);
        while (scratch.Count < count)
            scratch.Add(0);
    }

    private void ApplyDepthRangeHeaders()
    {
        Span<GPUPerTreeParams> headers = CollectionsMarshal.AsSpan(_allPerTreeParams);
        Span<PhysicsChainGpuTreeWorkItem> workItems = CollectionsMarshal.AsSpan(_treeWorkItems);
        for (int index = 0; index < headers.Length; ++index)
        {
            GPUPerTreeParams header = headers[index];
            header.DepthRangeOffset = _treeDepthRangeOffsets[index];
            header.DepthRangeCount = _treeDepthRangeCounts[index];
            headers[index] = header;

            PhysicsChainGpuTreeWorkItem workItem = workItems[index];
            workItem.TopologyDepth = (uint)_treeDepthRangeCounts[index];
            workItems[index] = workItem;
        }
    }

    private bool DispatchSpecializedPhysics(IPhysicsChainComputeBackend backend, bool applyObjectMove)
    {
        if (_mainPhysicsProgram is null
            || _branchedPhysicsProgram is null
            || !_mainPassBindingsValid
            || _activeTreeIdBuffer is null
            || _activeWorkCounterBuffer is null
            || _indirectDispatchArgumentBuffer is null
            || _depthRangeBuffer is null
            || _depthParticleIdBuffer is null)
            return false;
        if (TotalParticleCount <= 0 || TotalTreeCount <= 0)
            return true;

        for (uint bucket = 0u; bucket < (uint)PhysicsChainKernelBucket.Count; ++bucket)
        {
            if (_kernelCandidateCounts[(int)bucket] == 0)
                continue;

            PhysicsChainKernelBucket kernelBucket = (PhysicsChainKernelBucket)bucket;
            XRRenderProgram program = kernelBucket == PhysicsChainKernelBucket.ShortLinear
                ? _mainPhysicsProgram
                : _branchedPhysicsProgram;
            ConfigureSolverProgram(program, applyObjectMove, bucket);
            using var profilerState = Engine.Profiler.Start(
                kernelBucket == PhysicsChainKernelBucket.ShortLinear
                    ? "GPUPhysicsChainDispatcher.Solver.ShortLinear"
                    : "GPUPhysicsChainDispatcher.Solver.BranchedOrLong");
            nint argumentOffset = (nint)(bucket * 3u * sizeof(uint));
            if (!TryDispatchIndirect(backend, program, _indirectDispatchArgumentBuffer, argumentOffset, kernelBucket))
                return false;

            if (kernelBucket == PhysicsChainKernelBucket.ShortLinear)
                ++_shortLinearIndirectDispatchCount;
            else
                ++_branchedOrLongIndirectDispatchCount;
        }

        return true;
    }

    private void ConfigureSolverProgram(XRRenderProgram program, bool applyObjectMove, uint bucket)
    {
        program.Uniform("ApplyObjectMove", applyObjectMove ? 1 : 0);
        program.Uniform("ParticleCount", TotalParticleCount);
        program.Uniform("TreeCount", TotalTreeCount);
        program.Uniform("UseActiveTreeIds", 1);
        program.Uniform("ActiveTreeIdBase", bucket * (uint)_activeListCapacityPerBucket);
        program.Uniform("ActiveTreeIdCapacity", (uint)_activeListCapacityPerBucket);
        program.Uniform("ActiveTreeBucket", bucket);
        program.Uniform("DepthRangeTotalCount", (uint)_depthRanges.Count);
        program.Uniform("DepthParticleIdCount", (uint)_depthParticleIds.Count);
        program.BindBuffer(_mainPassBindings.Particles, 0);
        program.BindBuffer(_mainPassBindings.ParticleStatic, 1);
        program.BindBuffer(_mainPassBindings.Transforms, 3);
        program.BindBuffer(_mainPassBindings.Colliders, 4);
        program.BindBuffer(_mainPassBindings.PerTreeParams, 5);
        program.BindBuffer(_activeTreeIdBuffer!, 6);
        program.BindBuffer(_activeWorkCounterBuffer!, 7);
        if (bucket == (uint)PhysicsChainKernelBucket.BranchedOrLong)
        {
            program.BindBuffer(_depthRangeBuffer!, 8);
            program.BindBuffer(_depthParticleIdBuffer!, 9);
        }
    }

    private void DisposeSpecializedKernelResources()
    {
        _depthRangeBuffer?.Dispose();
        _depthParticleIdBuffer?.Dispose();
        _depthRangeBuffer = null;
        _depthParticleIdBuffer = null;
        _depthRanges.Clear();
        _depthParticleIds.Clear();
        _treeDepthRangeOffsets.Clear();
        _treeDepthRangeCounts.Clear();
        _depthTopologySignature = int.MinValue;
        _depthTopologyResourceGeneration = -1;
        _depthTopologyDirty = false;
        _branchedPhysicsProgram?.Destroy();
        _branchedPhysicsShader?.Destroy();
        _branchedPhysicsProgram = null;
        _branchedPhysicsShader = null;
    }
}
