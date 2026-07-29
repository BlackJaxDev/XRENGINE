using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Live aggregate-deformation database and dispatch backend. Immutable mesh
/// inputs are appended once, pose inputs are packed once per shared world
/// frame, and all admitted jobs are submitted through bounded family batches.
/// </summary>
public sealed class AdvancedGpuDeformationResources :
    IAdvancedDeformationDispatchBackend,
    IDisposable
{
    private const uint InitialPaletteCapacity = 4_096u;
    private const uint InitialActiveBlendshapeCapacity = 1_024u;
    private const float DeltaEpsilonSquared = 1.0e-20f;

    private readonly int _frameSlotCount;
    private readonly Dictionary<XRMesh, AdvancedGpuDeformationMeshSlice>
        _meshSlices;
    private readonly Dictionary<XRMeshRenderer, AdvancedGpuDeformationPoseEntry>
        _poseEntries;
    private readonly AdvancedGpuDeformationStaticBuffers?[]
        _retiredStaticBuffers;
    private readonly AdvancedGpuDeformationOutputBuffers?[]
        _retiredOutputBuffers;
    private readonly ulong[] _retiredStaticCompletionValues;
    private readonly ulong[] _retiredOutputCompletionValues;
    private readonly ulong[] _slotSubmissionValues;
    private readonly XRDataBuffer<AdvancedDeformationJobRecord>[] _jobBuffers;
    private readonly XRDataBuffer<uint>[] _jobIndexBuffers;
    private readonly XRDataBuffer<uint>[] _jobVertexOffsetBuffers;
    private readonly XRDataBuffer<SkinPaletteMatrix>[] _paletteBuffers;
    private readonly XRDataBuffer<AdvancedActiveBlendshape>[]
        _activeBlendshapeBuffers;
    private readonly uint[] _groupedJobIndexScratch;
    private readonly AdvancedDeformationExecutor _executor = new();

    private AdvancedDeformedVertex[] _sourceVertices;
    private AdvancedSkinInfluence[] _skinInfluences;
    private AdvancedSpillInfluence[] _spillInfluences;
    private AdvancedBlendshapeRange[] _blendshapeRanges;
    private AdvancedBlendshapeSparseRecord[] _blendshapeRecords;
    private Vector4[] _blendshapeDeltas;
    private SkinPaletteMatrix[] _paletteScratch;
    private AdvancedActiveBlendshape[] _activeBlendshapeScratch;

    private AdvancedGpuDeformationStaticBuffers _staticBuffers;
    private AdvancedGpuDeformationOutputBuffers _outputBuffers;
    private XRShader? _aggregateShader;
    private XRRenderProgram? _aggregateProgram;
    private uint _sourceVertexCount;
    private uint _skinInfluenceCount;
    private uint _spillInfluenceCount;
    private uint _blendshapeRangeCount;
    private uint _blendshapeRecordCount;
    private uint _blendshapeDeltaCount = 1u;
    private uint _uploadedSourceVertexCount;
    private uint _uploadedSkinInfluenceCount;
    private uint _uploadedSpillInfluenceCount;
    private uint _uploadedBlendshapeRangeCount;
    private uint _uploadedBlendshapeRecordCount;
    private uint _uploadedBlendshapeDeltaCount;
    private uint _paletteCount;
    private uint _activeBlendshapeCount;
    private uint _currentFrameSlot;
    private uint _previousFrameSlot;
    private ulong _frameId;
    private ulong _resourceGeneration = 1UL;
    private RuntimeGraphicsApiKind _backend;
    private bool _frameOpen;
    private bool _previousOutputValid;
    private bool _staticGenerationReplaced;
    private uint _staticCapacityGrowthCount;
    private uint _outputCapacityGrowthCount;
    private uint _unsupportedMeshCount;

    public AdvancedGpuDeformationResources(
        in AdvancedPreparationOptions options)
    {
        _frameSlotCount = options.DeformedArena.FrameSlotCount;
        uint initialVertices = options.DeformedArena.InitialVertexCapacity;
        uint initialAuxiliary = Math.Max(1_024u, initialVertices / 4u);
        uint initialRanges = Math.Max(
            1_024u,
            checked((uint)options.MaximumDeformationJobs));

        _sourceVertices = new AdvancedDeformedVertex[initialVertices];
        _skinInfluences = new AdvancedSkinInfluence[initialVertices];
        _spillInfluences = new AdvancedSpillInfluence[initialAuxiliary];
        _blendshapeRanges = new AdvancedBlendshapeRange[initialRanges];
        _blendshapeRecords =
            new AdvancedBlendshapeSparseRecord[initialVertices];
        _blendshapeDeltas = new Vector4[initialVertices];
        _blendshapeDeltas[0] = Vector4.Zero;
        _paletteScratch = new SkinPaletteMatrix[InitialPaletteCapacity];
        _activeBlendshapeScratch =
            new AdvancedActiveBlendshape[InitialActiveBlendshapeCapacity];
        _groupedJobIndexScratch =
            new uint[options.MaximumDeformationJobs];

        _staticBuffers = CreateStaticBuffers();
        _outputBuffers = new AdvancedGpuDeformationOutputBuffers(
            _frameSlotCount,
            initialVertices);
        _retiredStaticBuffers =
            new AdvancedGpuDeformationStaticBuffers?[
                options.DeformedArena.RetiredGenerationCapacity];
        _retiredOutputBuffers =
            new AdvancedGpuDeformationOutputBuffers?[
                options.DeformedArena.RetiredGenerationCapacity];
        _retiredStaticCompletionValues =
            new ulong[_retiredStaticBuffers.Length];
        _retiredOutputCompletionValues =
            new ulong[_retiredOutputBuffers.Length];
        _slotSubmissionValues = new ulong[_frameSlotCount];
        _meshSlices = new Dictionary<
            XRMesh,
            AdvancedGpuDeformationMeshSlice>(
                options.MaximumDeformationJobs,
                ReferenceEqualityComparer.Instance);
        _poseEntries = new Dictionary<
            XRMeshRenderer,
            AdvancedGpuDeformationPoseEntry>(
                options.MaximumDeformationJobs,
                ReferenceEqualityComparer.Instance);

        _jobBuffers =
            new XRDataBuffer<AdvancedDeformationJobRecord>[_frameSlotCount];
        _jobIndexBuffers = new XRDataBuffer<uint>[_frameSlotCount];
        _jobVertexOffsetBuffers = new XRDataBuffer<uint>[_frameSlotCount];
        _paletteBuffers =
            new XRDataBuffer<SkinPaletteMatrix>[_frameSlotCount];
        _activeBlendshapeBuffers =
            new XRDataBuffer<AdvancedActiveBlendshape>[_frameSlotCount];
        for (int slot = 0; slot < _frameSlotCount; slot++)
        {
            _jobBuffers[slot] = CreateDynamicBuffer<
                AdvancedDeformationJobRecord>(
                    $"AdvancedDeformation.Jobs.Slot{slot}",
                    checked((uint)options.MaximumDeformationJobs));
            _jobIndexBuffers[slot] = CreateDynamicBuffer<uint>(
                $"AdvancedDeformation.GroupedJobIndices.Slot{slot}",
                checked((uint)options.MaximumDeformationJobs));
            _jobVertexOffsetBuffers[slot] = CreateDynamicBuffer<uint>(
                $"AdvancedDeformation.GroupedJobVertexOffsets.Slot{slot}",
                checked((uint)options.MaximumDeformationJobs));
            _paletteBuffers[slot] = CreateDynamicBuffer<SkinPaletteMatrix>(
                $"AdvancedDeformation.Palettes.Slot{slot}",
                InitialPaletteCapacity);
            _activeBlendshapeBuffers[slot] =
                CreateDynamicBuffer<AdvancedActiveBlendshape>(
                    $"AdvancedDeformation.ActiveBlendshapes.Slot{slot}",
                    InitialActiveBlendshapeCapacity);
        }
    }

    public RuntimeGraphicsApiKind Backend => _backend;
    public bool SupportsAggregateCompute
        => AbstractRenderer.Current is not null &&
           AdvancedDeformationBackendContract
               .SupportsProductionAggregateCompute(_backend);
    public double LastGpuMilliseconds => 0.0;
    public uint StaticCapacityGrowthCount => _staticCapacityGrowthCount;
    public uint OutputCapacityGrowthCount => _outputCapacityGrowthCount;
    public uint UnsupportedMeshCount => _unsupportedMeshCount;
    public bool PreviousOutputValid => _previousOutputValid;
    public AdvancedDeformationDispatchTelemetry LastTelemetry { get; private set; }
    public AdvancedGpuDeformationPublication Publication { get; private set; }
    public XRDataBuffer SourceVertices => _staticBuffers.SourceVertices;
    public XRDataBuffer SkinInfluences => _staticBuffers.SkinInfluences;
    public XRDataBuffer SpillInfluences => _staticBuffers.SpillInfluences;
    public XRDataBuffer SkinPalettes => _paletteBuffers[_currentFrameSlot];
    public XRDataBuffer ActiveBlendshapes
        => _activeBlendshapeBuffers[_currentFrameSlot];
    public XRDataBuffer BlendshapeRanges => _staticBuffers.BlendshapeRanges;
    public XRDataBuffer BlendshapeRecords => _staticBuffers.BlendshapeRecords;
    public XRDataBuffer BlendshapeDeltas => _staticBuffers.BlendshapeDeltas;

    public bool TryBeginFrame(
        ulong frameId,
        ulong completedValue,
        uint currentFrameSlot,
        uint previousFrameSlot,
        uint requiredOutputVertexCapacity)
    {
        if (_frameOpen)
            throw new InvalidOperationException(
                "The aggregate deformation GPU frame is already open.");
        if (currentFrameSlot >= (uint)_frameSlotCount ||
            previousFrameSlot >= (uint)_frameSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(currentFrameSlot));
        }

        DrainRetired(completedValue);
        if (_slotSubmissionValues[currentFrameSlot] > completedValue)
            return false;

        bool outputReplaced = false;
        if (requiredOutputVertexCapacity > _outputBuffers.VertexCapacity)
        {
            if (!TryReplaceOutputBuffers(requiredOutputVertexCapacity))
                return false;
            outputReplaced = true;
        }

        _frameId = frameId;
        _currentFrameSlot = currentFrameSlot;
        _previousFrameSlot = previousFrameSlot;
        _paletteCount = 0u;
        _activeBlendshapeCount = 0u;
        _previousOutputValid =
            frameId != 0UL &&
            !outputReplaced &&
            !_staticGenerationReplaced;
        _staticGenerationReplaced = false;
        _frameOpen = true;
        return true;
    }

    public bool TryGetOrAddMesh(
        XRMesh mesh,
        uint topologyGeneration,
        out AdvancedGpuDeformationMeshSlice slice)
    {
        ThrowIfFrameClosed();
        ArgumentNullException.ThrowIfNull(mesh);

        if (_meshSlices.TryGetValue(mesh, out slice) &&
            slice.TopologyGeneration == topologyGeneration)
        {
            return true;
        }

        try
        {
            mesh.EnsureComputeSkinningBuffers();
        }
        catch (InvalidOperationException)
        {
            _unsupportedMeshCount++;
            slice = default;
            return false;
        }

        uint vertexCount = checked((uint)mesh.VertexCount);
        if (vertexCount == 0u ||
            !CanReadCanonicalVertices(mesh))
        {
            _unsupportedMeshCount++;
            slice = default;
            return false;
        }

        uint spillCount = mesh.HasSpillInfluences
            ? mesh.BoneInfluenceSpillEntries?.ElementCount ?? 0u
            : 0u;
        CountBlendshapePayload(
            mesh,
            out uint rangeCount,
            out uint recordCount,
            out uint deltaCount);

        uint requiredSource = checked(_sourceVertexCount + vertexCount);
        uint requiredInfluences =
            checked(_skinInfluenceCount + vertexCount);
        uint requiredSpill =
            checked(_spillInfluenceCount + spillCount);
        uint requiredRanges =
            checked(_blendshapeRangeCount + rangeCount);
        uint requiredRecords =
            checked(_blendshapeRecordCount + recordCount);
        uint requiredDeltas =
            checked(_blendshapeDeltaCount + deltaCount);
        EnsureCpuCapacity(ref _sourceVertices, requiredSource);
        EnsureCpuCapacity(ref _skinInfluences, requiredInfluences);
        EnsureCpuCapacity(ref _spillInfluences, requiredSpill);
        EnsureCpuCapacity(ref _blendshapeRanges, requiredRanges);
        EnsureCpuCapacity(ref _blendshapeRecords, requiredRecords);
        EnsureCpuCapacity(ref _blendshapeDeltas, requiredDeltas);
        if (!TryEnsureStaticBufferCapacity(
                requiredSource,
                requiredInfluences,
                requiredSpill,
                requiredRanges,
                requiredRecords,
                requiredDeltas))
        {
            slice = default;
            return false;
        }

        uint sourceBase = _sourceVertexCount;
        uint influenceBase = _skinInfluenceCount;
        uint spillBase = _spillInfluenceCount;
        uint rangeBase = _blendshapeRangeCount;
        PackCanonicalVertices(mesh, sourceBase);
        PackSpillInfluences(mesh, spillBase, spillCount);
        PackSkinInfluences(mesh, influenceBase, spillBase);
        PackBlendshapePayload(mesh, rangeBase);

        _sourceVertexCount = requiredSource;
        _skinInfluenceCount = requiredInfluences;
        _spillInfluenceCount = requiredSpill;
        slice = new AdvancedGpuDeformationMeshSlice(
            sourceBase,
            influenceBase,
            rangeBase,
            vertexCount,
            rangeCount,
            topologyGeneration);
        _meshSlices[mesh] = slice;
        return true;
    }

    public bool TryGetOrAddPose(
        XRMeshRenderer renderer,
        XRMesh mesh,
        out AdvancedGpuDeformationPoseSlice slice)
    {
        ThrowIfFrameClosed();
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(mesh);

        if (_poseEntries.TryGetValue(renderer, out var entry) &&
            entry.FrameId == _frameId)
        {
            slice = entry.Slice;
            return true;
        }

        if (!renderer.EnsureSkinningBuffers(logWarnings: false))
        {
            slice = default;
            return false;
        }
        if (mesh.HasBlendshapes)
            renderer.EnsureBlendshapeBuffers(logWarnings: false);
        if (!renderer.HasExternalSkinPaletteSource)
        {
            if (renderer.SkinPaletteReseedCount < 2)
                renderer.ReseedSkinPaletteUntilPoseStable();
            renderer.SyncDirtyBoneMatricesToClientBuffer();
        }

        XRDataBuffer? paletteSource = renderer.ActiveSkinPaletteBuffer;
        uint paletteCount = renderer.ActiveSkinPaletteCount;
        if (paletteSource is null ||
            paletteCount == 0u ||
            renderer.ActiveSkinPaletteBase + paletteCount >
                paletteSource.ElementCount)
        {
            slice = default;
            return false;
        }

        uint activeCount = checked((uint)Math.Max(
            0,
            renderer.ActiveBlendshapeCount));
        uint requiredPalette = checked(_paletteCount + paletteCount);
        uint requiredActive =
            checked(_activeBlendshapeCount + activeCount);
        EnsureDynamicPoseCapacity(requiredPalette, requiredActive);

        CopyPalette(
            paletteSource,
            renderer.ActiveSkinPaletteBase,
            _paletteScratch,
            _paletteCount,
            paletteCount);
        CopyActiveBlendshapes(
            renderer.BlendshapeActiveWeights,
            _activeBlendshapeScratch,
            _activeBlendshapeCount,
            activeCount);

        slice = new AdvancedGpuDeformationPoseSlice(
            _paletteCount,
            paletteCount,
            _activeBlendshapeCount,
            activeCount,
            renderer.SkinnedOutputVersion,
            renderer.BlendshapeWeightsVersion);
        _paletteCount = requiredPalette;
        _activeBlendshapeCount = requiredActive;
        _poseEntries[renderer] =
            new AdvancedGpuDeformationPoseEntry(_frameId, slice);
        return true;
    }

    public void Publish(
        ReadOnlySpan<AdvancedDeformationJobRecord> jobs,
        ReadOnlySpan<int> groupedJobIndices,
        ReadOnlySpan<uint> groupedJobVertexOffsets)
    {
        ThrowIfFrameClosed();
        if (groupedJobIndices.Length != groupedJobVertexOffsets.Length)
            throw new ArgumentException(
                "Grouped job indices and vertex offsets must match.");
        if (jobs.Length > _jobBuffers[_currentFrameSlot].ElementCount ||
            groupedJobIndices.Length > _groupedJobIndexScratch.Length)
        {
            throw new InvalidOperationException(
                "Aggregate deformation publication exceeds its fixed job capacity.");
        }

        UploadStaticAppends();
        if (!jobs.IsEmpty)
            _jobBuffers[_currentFrameSlot].Write(0u, jobs);
        for (int i = 0; i < groupedJobIndices.Length; i++)
            _groupedJobIndexScratch[i] =
                checked((uint)groupedJobIndices[i]);
        if (!groupedJobIndices.IsEmpty)
        {
            _jobIndexBuffers[_currentFrameSlot].Write(
                0u,
                _groupedJobIndexScratch.AsSpan(
                    0,
                    groupedJobIndices.Length));
            _jobVertexOffsetBuffers[_currentFrameSlot].Write(
                0u,
                groupedJobVertexOffsets);
        }
        if (_paletteCount != 0u)
        {
            _paletteBuffers[_currentFrameSlot].Write(
                0u,
                _paletteScratch.AsSpan(
                    0,
                    checked((int)_paletteCount)));
        }
        if (_activeBlendshapeCount != 0u)
        {
            _activeBlendshapeBuffers[_currentFrameSlot].Write(
                0u,
                _activeBlendshapeScratch.AsSpan(
                    0,
                    checked((int)_activeBlendshapeCount)));
        }

        Publication = new AdvancedGpuDeformationPublication(
            _frameId,
            _resourceGeneration,
            _currentFrameSlot,
            _previousFrameSlot,
            _outputBuffers.Buffers[_currentFrameSlot],
            _outputBuffers.Buffers[_previousFrameSlot],
            _jobBuffers[_currentFrameSlot],
            _jobIndexBuffers[_currentFrameSlot],
            _jobVertexOffsetBuffers[_currentFrameSlot],
            checked((uint)jobs.Length),
            checked((uint)groupedJobIndices.Length),
            _previousOutputValid);
    }

    public bool TryExecute(
        AdvancedDeformationDispatchPlanner planner,
        ReadOnlySpan<AdvancedDeformationJobRecord> jobs,
        EAdvancedPreparationConsumer consumers,
        uint admissionOverflowCount)
    {
        ArgumentNullException.ThrowIfNull(planner);
        if (jobs.IsEmpty)
        {
            LastTelemetry = new AdvancedDeformationDispatchTelemetry(
                0u,
                0UL,
                0UL,
                0u,
                planner.FamilyOverflowCount,
                admissionOverflowCount,
                0.0);
            return true;
        }
        if (AbstractRenderer.Current is null)
            return false;

        _backend =
            RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend;
        if (_backend == RuntimeGraphicsApiKind.Unknown)
        {
            _backend = RuntimeEngine.Rendering.State.IsVulkan
                ? RuntimeGraphicsApiKind.Vulkan
                : RuntimeGraphicsApiKind.OpenGL;
        }

        LastTelemetry = _executor.Execute(
            planner,
            this,
            jobs,
            consumers,
            EAdvancedDeformationExecutionMode.AggregateCompute,
            admissionOverflowCount);
        return true;
    }

    public void Dispatch(
        in AdvancedDeformationDispatchBatch batch,
        ReadOnlySpan<int> jobIndices)
    {
        if (jobIndices.Length != checked((int)batch.JobCount))
            throw new InvalidOperationException(
                "Aggregate deformation received a partial dispatch batch.");
        if (!SupportsAggregateCompute)
            throw new NotSupportedException(
                $"{_backend} cannot execute aggregate deformation.");

        XRRenderProgram program = GetAggregateProgram();
        _jobBuffers[_currentFrameSlot].BindTo(program, 0u);
        _jobIndexBuffers[_currentFrameSlot].BindTo(program, 1u);
        _jobVertexOffsetBuffers[_currentFrameSlot].BindTo(program, 2u);
        _staticBuffers.SourceVertices.BindTo(program, 3u);
        _staticBuffers.SkinInfluences.BindTo(program, 4u);
        _paletteBuffers[_currentFrameSlot].BindTo(program, 5u);
        _staticBuffers.InverseBindMatrices.BindTo(program, 6u);
        _activeBlendshapeBuffers[_currentFrameSlot].BindTo(program, 7u);
        _staticBuffers.BlendshapeDeltas.BindTo(program, 8u);
        _outputBuffers.Buffers[_currentFrameSlot].BindTo(program, 9u);
        _staticBuffers.SpillInfluences.BindTo(program, 10u);
        _staticBuffers.BlendshapeRanges.BindTo(program, 11u);
        _staticBuffers.BlendshapeRecords.BindTo(program, 12u);
        program.Uniform("firstGroupedJob", batch.FirstJobIndex);
        program.Uniform("groupedJobCount", batch.JobCount);
        program.Uniform(
            "batchVertexCount",
            checked((uint)batch.VertexCount));
        program.DispatchCompute(batch.WorkGroupCount, 1u, 1u);
    }

    public void ApplyBarrier(in AdvancedPreparationBarrier barrier)
    {
        EMemoryBarrierMask mask = ConvertBarrier(barrier.OpenGlMask);
        AbstractRenderer.Current?.MemoryBarrier(mask);
    }

    /// <summary>
    /// Lowers barriers for consumers that acquire an already-dispatched
    /// shared publication later in the same world frame.
    /// </summary>
    public void ApplyConsumerBarriers(
        EAdvancedPreparationConsumer consumers)
    {
        if (consumers == EAdvancedPreparationConsumer.None)
            return;

        Span<AdvancedPreparationBarrier> barriers =
            stackalloc AdvancedPreparationBarrier[9];
        if (!AdvancedDeformationBarrierContract.TryWriteRequired(
                consumers,
                barriers,
                out int barrierCount))
        {
            throw new InvalidOperationException(
                "The fixed deformation barrier plan is too small.");
        }

        for (int i = 0; i < barrierCount; i++)
            ApplyBarrier(barriers[i]);
    }

    public void EndFrame(ulong submissionCompletionValue)
    {
        ThrowIfFrameClosed();
        _slotSubmissionValues[_currentFrameSlot] =
            submissionCompletionValue;
        _frameOpen = false;
    }

    public void Dispose()
    {
        _aggregateProgram?.Destroy();
        _aggregateShader?.Destroy();
        _aggregateProgram = null;
        _aggregateShader = null;
        _staticBuffers.Destroy();
        _outputBuffers.Destroy();
        for (int slot = 0; slot < _frameSlotCount; slot++)
        {
            _jobBuffers[slot].Destroy();
            _jobIndexBuffers[slot].Destroy();
            _jobVertexOffsetBuffers[slot].Destroy();
            _paletteBuffers[slot].Destroy();
            _activeBlendshapeBuffers[slot].Destroy();
        }
        for (int i = 0; i < _retiredStaticBuffers.Length; i++)
        {
            _retiredStaticBuffers[i]?.Destroy();
            _retiredStaticBuffers[i] = null;
        }
        for (int i = 0; i < _retiredOutputBuffers.Length; i++)
        {
            _retiredOutputBuffers[i]?.Destroy();
            _retiredOutputBuffers[i] = null;
        }
        _meshSlices.Clear();
        _poseEntries.Clear();
    }

    private void CountBlendshapePayload(
        XRMesh mesh,
        out uint rangeCount,
        out uint recordCount,
        out uint deltaCount)
    {
        rangeCount = 0u;
        recordCount = 0u;
        deltaCount = 0u;
        Vertex[] vertices = mesh.Vertices;
        string[] names = mesh.BlendshapeNames;
        if (vertices.Length != mesh.VertexCount ||
            names.Length == 0)
        {
            return;
        }

        rangeCount = checked((uint)names.Length);
        for (int shapeIndex = 0; shapeIndex < names.Length; shapeIndex++)
        {
            string name = names[shapeIndex];
            for (int vertexIndex = 0;
                 vertexIndex < vertices.Length;
                 vertexIndex++)
            {
                Vertex source = vertices[vertexIndex];
                if (!TryGetBlendshapeData(
                        source,
                        name,
                        shapeIndex,
                        out VertexData data))
                {
                    continue;
                }

                GetBlendshapeDeltas(
                    source,
                    data,
                    out Vector3 position,
                    out Vector3 normal,
                    out Vector3 tangent);
                bool hasPosition =
                    position.LengthSquared() > DeltaEpsilonSquared;
                bool hasNormal =
                    normal.LengthSquared() > DeltaEpsilonSquared;
                bool hasTangent =
                    tangent.LengthSquared() > DeltaEpsilonSquared;
                if (!hasPosition && !hasNormal && !hasTangent)
                    continue;

                recordCount++;
                deltaCount += checked(
                    (uint)(hasPosition ? 1 : 0) +
                    (uint)(hasNormal ? 1 : 0) +
                    (uint)(hasTangent ? 1 : 0));
            }
        }
    }

    private void PackBlendshapePayload(
        XRMesh mesh,
        uint rangeBase)
    {
        Vertex[] vertices = mesh.Vertices;
        string[] names = mesh.BlendshapeNames;
        if (vertices.Length != mesh.VertexCount ||
            names.Length == 0)
        {
            return;
        }

        for (int shapeIndex = 0; shapeIndex < names.Length; shapeIndex++)
        {
            uint recordStart = _blendshapeRecordCount;
            uint flags = 0u;
            string name = names[shapeIndex];
            for (int vertexIndex = 0;
                 vertexIndex < vertices.Length;
                 vertexIndex++)
            {
                Vertex source = vertices[vertexIndex];
                if (!TryGetBlendshapeData(
                        source,
                        name,
                        shapeIndex,
                        out VertexData data))
                {
                    continue;
                }

                GetBlendshapeDeltas(
                    source,
                    data,
                    out Vector3 position,
                    out Vector3 normal,
                    out Vector3 tangent);
                uint positionIndex = AppendDelta(position, 1u, ref flags);
                uint normalIndex = AppendDelta(normal, 2u, ref flags);
                uint tangentIndex = AppendDelta(tangent, 4u, ref flags);
                if ((positionIndex | normalIndex | tangentIndex) == 0u)
                    continue;

                _blendshapeRecords[_blendshapeRecordCount++] =
                    new AdvancedBlendshapeSparseRecord(
                        checked((uint)vertexIndex),
                        positionIndex,
                        normalIndex,
                        tangentIndex);
            }

            _blendshapeRanges[rangeBase + checked((uint)shapeIndex)] =
                new AdvancedBlendshapeRange(
                    recordStart,
                    _blendshapeRecordCount - recordStart,
                    flags,
                    0u);
            _blendshapeRangeCount++;
        }
    }

    private uint AppendDelta(
        Vector3 delta,
        uint attributeFlag,
        ref uint flags)
    {
        if (delta.LengthSquared() <= DeltaEpsilonSquared)
            return 0u;

        uint index = _blendshapeDeltaCount++;
        _blendshapeDeltas[index] = new Vector4(delta, 0.0f);
        flags |= attributeFlag;
        return index;
    }

    private void PackCanonicalVertices(
        XRMesh mesh,
        uint destinationBase)
    {
        Vertex[] vertices = mesh.Vertices;
        bool hasVertices = vertices.Length == mesh.VertexCount;
        for (uint vertexIndex = 0u;
             vertexIndex < checked((uint)mesh.VertexCount);
             vertexIndex++)
        {
            AdvancedDeformedVertex packed;
            if (hasVertices)
            {
                packed = AdvancedPackedVertexCodec.Pack(
                    vertices[vertexIndex],
                    destinationBase + vertexIndex);
            }
            else
            {
                Vector3 position =
                    mesh.PositionsBuffer!.GetVector3(vertexIndex);
                Vector3 normal = mesh.NormalsBuffer?.GetVector3(vertexIndex)
                    ?? Vector3.UnitY;
                Vector4 tangent4 =
                    mesh.TangentsBuffer?.GetVector4(vertexIndex)
                    ?? new Vector4(Vector3.UnitX, 1.0f);
                Vector2 uv0 =
                    mesh.TexCoordBuffers is { Length: > 0 } &&
                    mesh.TexCoordBuffers[0] is XRDataBuffer uv0Buffer
                        ? uv0Buffer.GetVector2(vertexIndex)
                        : Vector2.Zero;
                Vector2 uv1 =
                    mesh.TexCoordBuffers is { Length: > 1 } &&
                    mesh.TexCoordBuffers[1] is XRDataBuffer uv1Buffer
                        ? uv1Buffer.GetVector2(vertexIndex)
                        : Vector2.Zero;
                Vector4 color0 =
                    mesh.ColorBuffers is { Length: > 0 } &&
                    mesh.ColorBuffers[0] is XRDataBuffer color0Buffer
                        ? color0Buffer.GetVector4(vertexIndex)
                        : Vector4.One;
                Vector4 color1 =
                    mesh.ColorBuffers is { Length: > 1 } &&
                    mesh.ColorBuffers[1] is XRDataBuffer color1Buffer
                        ? color1Buffer.GetVector4(vertexIndex)
                        : Vector4.One;
                packed = AdvancedPackedVertexCodec.Pack(
                    position,
                    normal,
                    new Vector3(tangent4.X, tangent4.Y, tangent4.Z),
                    tangent4.W,
                    uv0,
                    uv1,
                    color0,
                    color1,
                    destinationBase + vertexIndex);
            }

            _sourceVertices[destinationBase + vertexIndex] = packed;
        }
    }

    private unsafe void PackSkinInfluences(
        XRMesh mesh,
        uint destinationBase,
        uint globalSpillBase)
    {
        XRDataBuffer indices =
            mesh.BoneInfluenceCoreIndices ??
            throw new InvalidOperationException(
                "Canonical skinning indices are unavailable.");
        XRDataBuffer weights =
            mesh.BoneInfluenceCoreWeights ??
            throw new InvalidOperationException(
                "Canonical skinning weights are unavailable.");
        byte* weightBytes = (byte*)weights.Address.Pointer;
        byte* indexBytes = (byte*)indices.Address.Pointer;
        uint* spillHeaders =
            mesh.BoneInfluenceSpillHeaders is XRDataBuffer headers
                ? (uint*)headers.Address.Pointer
                : null;

        for (uint vertexIndex = 0u;
             vertexIndex < checked((uint)mesh.VertexCount);
             vertexIndex++)
        {
            uint elementByteOffset = vertexIndex * indices.ElementSize;
            uint bone0;
            uint bone1;
            uint bone2;
            uint bone3;
            if (indices.ComponentType == EComponentType.Byte)
            {
                byte* source = indexBytes + elementByteOffset;
                bone0 = source[0];
                bone1 = source[1];
                bone2 = source[2];
                bone3 = source[3];
            }
            else
            {
                ushort* source =
                    (ushort*)(indexBytes + elementByteOffset);
                bone0 = source[0];
                bone1 = source[1];
                bone2 = source[2];
                bone3 = source[3];
            }

            byte* sourceWeights =
                weightBytes + vertexIndex * weights.ElementSize;
            uint header = spillHeaders is null
                ? 0u
                : spillHeaders[vertexIndex];
            uint localSpillOffset = header & 0x00FF_FFFFu;
            _skinInfluences[destinationBase + vertexIndex] = new()
            {
                Bone0 = bone0,
                Bone1 = bone1,
                Bone2 = bone2,
                Bone3 = bone3,
                Weights = new Vector4(
                    sourceWeights[0] / 255.0f,
                    sourceWeights[1] / 255.0f,
                    sourceWeights[2] / 255.0f,
                    sourceWeights[3] / 255.0f),
                SpillOffset = globalSpillBase + localSpillOffset,
                SpillCount = header >> 24,
            };
        }
    }

    private unsafe void PackSpillInfluences(
        XRMesh mesh,
        uint destinationBase,
        uint count)
    {
        if (count == 0u)
            return;

        XRDataBuffer sourceBuffer =
            mesh.BoneInfluenceSpillEntries ??
            throw new InvalidOperationException(
                "Canonical spill influences are unavailable.");
        uint* source = (uint*)sourceBuffer.Address.Pointer;
        for (uint i = 0u; i < count; i++)
        {
            uint packed = source[i];
            _spillInfluences[destinationBase + i] =
                new AdvancedSpillInfluence(
                    packed & 0xFFFFu,
                    ((packed >> 16) & 0xFFu) / 255.0f);
        }
    }

    private void EnsureDynamicPoseCapacity(
        uint requiredPalette,
        uint requiredActive)
    {
        if (requiredPalette > _paletteScratch.Length)
        {
            uint capacity = NextPowerOfTwo(requiredPalette);
            Array.Resize(
                ref _paletteScratch,
                checked((int)capacity));
        }
        if (requiredPalette >
            _paletteBuffers[_currentFrameSlot].ElementCount)
        {
            uint capacity = NextPowerOfTwo(requiredPalette);
            _paletteBuffers[_currentFrameSlot].Resize(
                capacity,
                copyData: false,
                alignClientSourceToPowerOf2: false);
        }
        if (requiredActive > _activeBlendshapeScratch.Length)
        {
            uint capacity = NextPowerOfTwo(requiredActive);
            Array.Resize(
                ref _activeBlendshapeScratch,
                checked((int)capacity));
        }
        if (requiredActive >
            _activeBlendshapeBuffers[_currentFrameSlot].ElementCount)
        {
            uint capacity = NextPowerOfTwo(requiredActive);
            _activeBlendshapeBuffers[_currentFrameSlot].Resize(
                capacity,
                copyData: false,
                alignClientSourceToPowerOf2: false);
        }
    }

    private bool TryEnsureStaticBufferCapacity(
        uint source,
        uint influences,
        uint spill,
        uint ranges,
        uint records,
        uint deltas)
    {
        if (source <= _staticBuffers.SourceVertices.ElementCount &&
            influences <= _staticBuffers.SkinInfluences.ElementCount &&
            spill <= _staticBuffers.SpillInfluences.ElementCount &&
            ranges <= _staticBuffers.BlendshapeRanges.ElementCount &&
            records <= _staticBuffers.BlendshapeRecords.ElementCount &&
            deltas <= _staticBuffers.BlendshapeDeltas.ElementCount)
        {
            return true;
        }

        int retiredSlot = FindEmpty(_retiredStaticBuffers);
        if (retiredSlot < 0)
            return false;

        AdvancedGpuDeformationStaticBuffers replacement =
            new(
                Math.Max(
                    _staticBuffers.SourceVertices.ElementCount,
                    NextPowerOfTwo(source)),
                Math.Max(
                    _staticBuffers.SkinInfluences.ElementCount,
                    NextPowerOfTwo(influences)),
                Math.Max(
                    _staticBuffers.SpillInfluences.ElementCount,
                    NextPowerOfTwo(spill)),
                Math.Max(
                    _staticBuffers.BlendshapeRanges.ElementCount,
                    NextPowerOfTwo(ranges)),
                Math.Max(
                    _staticBuffers.BlendshapeRecords.ElementCount,
                    NextPowerOfTwo(records)),
                Math.Max(
                    _staticBuffers.BlendshapeDeltas.ElementCount,
                    NextPowerOfTwo(deltas)));
        UploadAllStatic(replacement);
        _retiredStaticBuffers[retiredSlot] = _staticBuffers;
        _retiredStaticCompletionValues[retiredSlot] =
            MaximumSubmissionValue();
        _staticBuffers = replacement;
        _uploadedSourceVertexCount = _sourceVertexCount;
        _uploadedSkinInfluenceCount = _skinInfluenceCount;
        _uploadedSpillInfluenceCount = _spillInfluenceCount;
        _uploadedBlendshapeRangeCount = _blendshapeRangeCount;
        _uploadedBlendshapeRecordCount = _blendshapeRecordCount;
        _uploadedBlendshapeDeltaCount = _blendshapeDeltaCount;
        _resourceGeneration++;
        _staticCapacityGrowthCount++;
        _staticGenerationReplaced = true;
        _previousOutputValid = false;
        return true;
    }

    private bool TryReplaceOutputBuffers(uint requiredCapacity)
    {
        int retiredSlot = FindEmpty(_retiredOutputBuffers);
        if (retiredSlot < 0)
            return false;

        uint capacity = NextPowerOfTwo(requiredCapacity);
        AdvancedGpuDeformationOutputBuffers replacement =
            new(_frameSlotCount, capacity);
        _retiredOutputBuffers[retiredSlot] = _outputBuffers;
        _retiredOutputCompletionValues[retiredSlot] =
            MaximumSubmissionValue();
        _outputBuffers = replacement;
        _resourceGeneration++;
        _outputCapacityGrowthCount++;
        return true;
    }

    private void UploadStaticAppends()
    {
        UploadAppend(
            _staticBuffers.SourceVertices,
            _sourceVertices,
            ref _uploadedSourceVertexCount,
            _sourceVertexCount);
        UploadAppend(
            _staticBuffers.SkinInfluences,
            _skinInfluences,
            ref _uploadedSkinInfluenceCount,
            _skinInfluenceCount);
        UploadAppend(
            _staticBuffers.SpillInfluences,
            _spillInfluences,
            ref _uploadedSpillInfluenceCount,
            _spillInfluenceCount);
        UploadAppend(
            _staticBuffers.BlendshapeRanges,
            _blendshapeRanges,
            ref _uploadedBlendshapeRangeCount,
            _blendshapeRangeCount);
        UploadAppend(
            _staticBuffers.BlendshapeRecords,
            _blendshapeRecords,
            ref _uploadedBlendshapeRecordCount,
            _blendshapeRecordCount);
        UploadAppend(
            _staticBuffers.BlendshapeDeltas,
            _blendshapeDeltas,
            ref _uploadedBlendshapeDeltaCount,
            _blendshapeDeltaCount);
    }

    private void UploadAllStatic(
        AdvancedGpuDeformationStaticBuffers destination)
    {
        if (_sourceVertexCount != 0u)
            destination.SourceVertices.Write(
                0u,
                _sourceVertices.AsSpan(
                    0,
                    checked((int)_sourceVertexCount)));
        if (_skinInfluenceCount != 0u)
            destination.SkinInfluences.Write(
                0u,
                _skinInfluences.AsSpan(
                    0,
                    checked((int)_skinInfluenceCount)));
        if (_spillInfluenceCount != 0u)
            destination.SpillInfluences.Write(
                0u,
                _spillInfluences.AsSpan(
                    0,
                    checked((int)_spillInfluenceCount)));
        if (_blendshapeRangeCount != 0u)
            destination.BlendshapeRanges.Write(
                0u,
                _blendshapeRanges.AsSpan(
                    0,
                    checked((int)_blendshapeRangeCount)));
        if (_blendshapeRecordCount != 0u)
            destination.BlendshapeRecords.Write(
                0u,
                _blendshapeRecords.AsSpan(
                    0,
                    checked((int)_blendshapeRecordCount)));
        destination.BlendshapeDeltas.Write(
            0u,
            _blendshapeDeltas.AsSpan(
                0,
                checked((int)_blendshapeDeltaCount)));
    }

    private void DrainRetired(ulong completedValue)
    {
        for (int i = 0; i < _retiredStaticBuffers.Length; i++)
        {
            if (_retiredStaticBuffers[i] is null ||
                _retiredStaticCompletionValues[i] > completedValue)
            {
                continue;
            }

            _retiredStaticBuffers[i]!.Destroy();
            _retiredStaticBuffers[i] = null;
            _retiredStaticCompletionValues[i] = 0UL;
        }
        for (int i = 0; i < _retiredOutputBuffers.Length; i++)
        {
            if (_retiredOutputBuffers[i] is null ||
                _retiredOutputCompletionValues[i] > completedValue)
            {
                continue;
            }

            _retiredOutputBuffers[i]!.Destroy();
            _retiredOutputBuffers[i] = null;
            _retiredOutputCompletionValues[i] = 0UL;
        }
    }

    private AdvancedGpuDeformationStaticBuffers CreateStaticBuffers()
        => new(
            checked((uint)_sourceVertices.Length),
            checked((uint)_skinInfluences.Length),
            checked((uint)_spillInfluences.Length),
            checked((uint)_blendshapeRanges.Length),
            checked((uint)_blendshapeRecords.Length),
            checked((uint)_blendshapeDeltas.Length));

    private XRRenderProgram GetAggregateProgram()
    {
        _aggregateShader ??= ShaderHelper.LoadEngineShader(
            AdvancedDeformationBackendContract.AggregateShaderPath,
            EShaderType.Compute);
        return _aggregateProgram ??=
            new XRRenderProgram(true, false, _aggregateShader);
    }

    private ulong MaximumSubmissionValue()
    {
        ulong maximum = 0UL;
        for (int i = 0; i < _slotSubmissionValues.Length; i++)
            maximum = Math.Max(maximum, _slotSubmissionValues[i]);
        return maximum;
    }

    private static bool CanReadCanonicalVertices(XRMesh mesh)
        => mesh.Vertices.Length == mesh.VertexCount ||
           mesh.PositionsBuffer is
           {
               ClientSideSource: not null,
           };

    private static bool TryGetBlendshapeData(
        Vertex vertex,
        string expectedName,
        int shapeIndex,
        out VertexData data)
    {
        List<(string name, VertexData data)>? shapes =
            vertex.Blendshapes;
        if (shapes is null)
        {
            data = null!;
            return false;
        }
        if ((uint)shapeIndex < (uint)shapes.Count)
        {
            var direct = shapes[shapeIndex];
            if (string.Equals(
                    direct.name,
                    expectedName,
                    StringComparison.Ordinal))
            {
                data = direct.data;
                return data is not null;
            }
        }
        for (int i = 0; i < shapes.Count; i++)
        {
            if (!string.Equals(
                    shapes[i].name,
                    expectedName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            data = shapes[i].data;
            return data is not null;
        }

        data = null!;
        return false;
    }

    private static void GetBlendshapeDeltas(
        Vertex source,
        VertexData shape,
        out Vector3 position,
        out Vector3 normal,
        out Vector3 tangent)
    {
        position = shape.Position - source.Position;
        normal = shape.Normal.HasValue && source.Normal.HasValue
            ? shape.Normal.Value - source.Normal.Value
            : Vector3.Zero;
        tangent = shape.Tangent.HasValue && source.Tangent.HasValue
            ? shape.Tangent.Value - source.Tangent.Value
            : Vector3.Zero;
    }

    private static unsafe void CopyPalette(
        XRDataBuffer source,
        uint sourceOffset,
        SkinPaletteMatrix[] destination,
        uint destinationOffset,
        uint count)
    {
        nuint bytes = checked(
            (nuint)count *
            (nuint)Unsafe.SizeOf<SkinPaletteMatrix>());
        nint sourceByteOffset = checked(
            (nint)((nuint)sourceOffset *
            (nuint)Unsafe.SizeOf<SkinPaletteMatrix>()));
        fixed (SkinPaletteMatrix* destinationStart = destination)
        {
            Memory.Move(
                destinationStart + destinationOffset,
                source.Address + sourceByteOffset,
                checked((uint)bytes));
        }
    }

    private static void CopyActiveBlendshapes(
        XRDataBuffer? source,
        AdvancedActiveBlendshape[] destination,
        uint destinationOffset,
        uint count)
    {
        if (count == 0u)
            return;
        if (source is null || count > source.ElementCount)
            throw new InvalidOperationException(
                "Active blendshape weights are unavailable.");

        for (uint i = 0u; i < count; i++)
        {
            Vector2 pair = source.GetVector2(i);
            destination[destinationOffset + i] =
                new AdvancedActiveBlendshape(
                    checked((uint)Math.Max(0.0f, pair.X)),
                    pair.Y);
        }
    }

    private static void UploadAppend<T>(
        XRDataBuffer<T> buffer,
        T[] source,
        ref uint uploadedCount,
        uint currentCount) where T : unmanaged
    {
        if (currentCount <= uploadedCount)
            return;

        uint count = currentCount - uploadedCount;
        buffer.Write(
            uploadedCount,
            source.AsSpan(
                checked((int)uploadedCount),
                checked((int)count)));
        uploadedCount = currentCount;
    }

    private static XRDataBuffer<T> CreateDynamicBuffer<T>(
        string name,
        uint capacity) where T : unmanaged
        => new(
            name,
            EBufferTarget.ShaderStorageBuffer,
            Math.Max(1u, capacity))
        {
            Usage = EBufferUsage.StreamDraw,
            DisposeOnPush = false,
            Resizable = true,
        };

    private static void EnsureCpuCapacity<T>(
        ref T[] storage,
        uint required)
    {
        if (required <= storage.Length)
            return;
        Array.Resize(
            ref storage,
            checked((int)NextPowerOfTwo(required)));
    }

    private static int FindEmpty<T>(T?[] entries) where T : class
    {
        for (int i = 0; i < entries.Length; i++)
            if (entries[i] is null)
                return i;
        return -1;
    }

    private static uint NextPowerOfTwo(uint value)
    {
        if (value <= 1u)
            return 1u;
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return checked(value + 1u);
    }

    private static EMemoryBarrierMask ConvertBarrier(
        EAdvancedOpenGlMemoryBarrier source)
    {
        EMemoryBarrierMask result = EMemoryBarrierMask.None;
        if ((source & EAdvancedOpenGlMemoryBarrier.VertexAttributeArray) != 0)
            result |= EMemoryBarrierMask.VertexAttribArray;
        if ((source & EAdvancedOpenGlMemoryBarrier.ElementArray) != 0)
            result |= EMemoryBarrierMask.ElementArray;
        if ((source & EAdvancedOpenGlMemoryBarrier.Command) != 0)
            result |= EMemoryBarrierMask.Command;
        if ((source & EAdvancedOpenGlMemoryBarrier.TextureFetch) != 0)
            result |= EMemoryBarrierMask.TextureFetch;
        if ((source & EAdvancedOpenGlMemoryBarrier.ShaderImageAccess) != 0)
            result |= EMemoryBarrierMask.ShaderImageAccess;
        if ((source & EAdvancedOpenGlMemoryBarrier.ShaderStorage) != 0)
            result |= EMemoryBarrierMask.ShaderStorage;
        if ((source & EAdvancedOpenGlMemoryBarrier.FrameBuffer) != 0)
            result |= EMemoryBarrierMask.Framebuffer;
        return result;
    }

    private void ThrowIfFrameClosed()
    {
        if (!_frameOpen)
            throw new InvalidOperationException(
                "The aggregate deformation GPU frame is not open.");
    }
}
