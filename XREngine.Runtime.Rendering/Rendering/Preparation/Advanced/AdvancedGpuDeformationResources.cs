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
public sealed partial class AdvancedGpuDeformationResources :
    IAdvancedDeformationDispatchBackend,
    IDisposable
{
    private const uint InitialPaletteCapacity = 4_096u;
    private const uint InitialActiveBlendshapeCapacity = 1_024u;
    private const float DeltaEpsilonSquared = 1.0e-20f;

    private readonly int _frameSlotCount;
    private readonly AdvancedGpuDeformationStaticGeneration[]
        _staticGenerations;
    private readonly int[] _slotStaticGenerationIndices;
    private readonly Dictionary<XRMeshRenderer, AdvancedGpuDeformationPoseEntry>
        _poseEntries;
    private readonly XRGpuFence?[] _slotProducerFences;
    private readonly bool[] _slotOutputValid;
    private readonly bool[] _slotReusePoisoned;
    private readonly XRDataBuffer<AdvancedDeformationJobRecord>[] _jobBuffers;
    private readonly XRDataBuffer<uint>[] _jobIndexBuffers;
    private readonly XRDataBuffer<uint>[] _jobVertexOffsetBuffers;
    private readonly XRDataBuffer<SkinPaletteMatrix>[] _paletteBuffers;
    private readonly XRDataBuffer<AdvancedActiveBlendshape>[]
        _activeBlendshapeBuffers;
    private readonly uint[] _groupedJobIndexScratch;
    private readonly AdvancedDeformationExecutor _executor = new();

    private SkinPaletteMatrix[] _paletteScratch;
    private AdvancedActiveBlendshape[] _activeBlendshapeScratch;
    private int[] _blendshapeSourceIndices = [];
    private readonly Dictionary<string, int> _blendshapeFirstNameIndices =
        new(StringComparer.Ordinal);

    private AdvancedGpuDeformationStaticGeneration _staticGeneration;
    private AdvancedGpuDeformationOutputBuffers _outputBuffers;
    private XRShader? _aggregateShader;
    private XRRenderProgram? _aggregateProgram;
    private ulong _staticGenerationUse;
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

    // These aliases keep the packing code focused on deformation payloads
    // while making the selected static generation the only mutable owner.
    private Dictionary<XRMesh, AdvancedGpuDeformationMeshSlice> _meshSlices
        => _staticGeneration.MeshSlices;
    private ref AdvancedDeformedVertex[] _sourceVertices
        => ref _staticGeneration.SourceVertices;
    private ref AdvancedSkinInfluence[] _skinInfluences
        => ref _staticGeneration.SkinInfluences;
    private ref AdvancedSpillInfluence[] _spillInfluences
        => ref _staticGeneration.SpillInfluences;
    private ref AdvancedBlendshapeRange[] _blendshapeRanges
        => ref _staticGeneration.BlendshapeRanges;
    private ref AdvancedBlendshapeSparseRecord[] _blendshapeRecords
        => ref _staticGeneration.BlendshapeRecords;
    private ref Vector4[] _blendshapeDeltas
        => ref _staticGeneration.BlendshapeDeltas;
    private AdvancedGpuDeformationStaticBuffers _staticBuffers
    {
        get => _staticGeneration.Buffers;
        set => _staticGeneration.Buffers = value;
    }
    private ref uint _sourceVertexCount => ref _staticGeneration.SourceVertexCount;
    private ref uint _skinInfluenceCount => ref _staticGeneration.SkinInfluenceCount;
    private ref uint _spillInfluenceCount => ref _staticGeneration.SpillInfluenceCount;
    private ref uint _blendshapeRangeCount => ref _staticGeneration.BlendshapeRangeCount;
    private ref uint _blendshapeRecordCount => ref _staticGeneration.BlendshapeRecordCount;
    private ref uint _blendshapeDeltaCount => ref _staticGeneration.BlendshapeDeltaCount;
    private ref uint _uploadedSourceVertexCount => ref _staticGeneration.UploadedSourceVertexCount;
    private ref uint _uploadedSkinInfluenceCount => ref _staticGeneration.UploadedSkinInfluenceCount;
    private ref uint _uploadedSpillInfluenceCount => ref _staticGeneration.UploadedSpillInfluenceCount;
    private ref uint _uploadedBlendshapeRangeCount => ref _staticGeneration.UploadedBlendshapeRangeCount;
    private ref uint _uploadedBlendshapeRecordCount => ref _staticGeneration.UploadedBlendshapeRecordCount;
    private ref uint _uploadedBlendshapeDeltaCount => ref _staticGeneration.UploadedBlendshapeDeltaCount;

    public AdvancedGpuDeformationResources(
        in AdvancedPreparationOptions options)
    {
        _frameSlotCount = options.DeformedArena.FrameSlotCount;
        uint initialVertices = options.DeformedArena.InitialVertexCapacity;
        uint initialAuxiliary = Math.Max(1_024u, initialVertices / 4u);
        uint initialRanges = Math.Max(
            1_024u,
            checked((uint)options.MaximumDeformationJobs));

        _paletteScratch = new SkinPaletteMatrix[InitialPaletteCapacity];
        _activeBlendshapeScratch =
            new AdvancedActiveBlendshape[InitialActiveBlendshapeCapacity];
        _groupedJobIndexScratch =
            new uint[options.MaximumDeformationJobs];

        _staticGenerations = new AdvancedGpuDeformationStaticGeneration[
            checked(_frameSlotCount + 1)];
        for (int generation = 0;
             generation < _staticGenerations.Length;
             generation++)
        {
            _staticGenerations[generation] =
                new AdvancedGpuDeformationStaticGeneration(
                    initialVertices,
                    initialAuxiliary,
                    initialRanges,
                    options.MaximumDeformationJobs);
        }
        _staticGeneration = _staticGenerations[0];
        // Preserve the original single-generation baseline allocation. The
        // remaining bounded generations allocate only when selected.
        _staticGeneration.EnsureInitialized();
        _slotStaticGenerationIndices = new int[_frameSlotCount];
        Array.Fill(_slotStaticGenerationIndices, -1);
        _outputBuffers = new AdvancedGpuDeformationOutputBuffers(
            _frameSlotCount,
            initialVertices);
        _slotProducerFences = new XRGpuFence?[_frameSlotCount];
        _slotOutputValid = new bool[_frameSlotCount];
        _slotReusePoisoned = new bool[_frameSlotCount];
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
    public XRDataBuffer SourceVertices => _staticGeneration.Buffers.SourceVertices;
    public XRDataBuffer SkinInfluences => _staticGeneration.Buffers.SkinInfluences;
    public XRDataBuffer SpillInfluences => _staticGeneration.Buffers.SpillInfluences;
    public XRDataBuffer SkinPalettes => _paletteBuffers[_currentFrameSlot];
    public XRDataBuffer ActiveBlendshapes
        => _activeBlendshapeBuffers[_currentFrameSlot];
    public XRDataBuffer BlendshapeRanges => _staticGeneration.Buffers.BlendshapeRanges;
    public XRDataBuffer BlendshapeRecords => _staticGeneration.Buffers.BlendshapeRecords;
    public XRDataBuffer BlendshapeDeltas => _staticGeneration.Buffers.BlendshapeDeltas;

    /// <summary>
    /// Selects immutable static inputs for one exact canonical scene revision.
    /// An existing exact generation is reused; otherwise only an unpinned
    /// least-recently-used generation may be reassigned.
    /// </summary>
    public bool TrySelectStaticGeneration(
        GPUScene scene,
        ulong databaseEpoch,
        ulong topologyGeneration)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_frameOpen)
            throw new InvalidOperationException(
                "Static deformation selection must occur before frame authoring.");

        AdvancedGpuDeformationStaticGeneration? matched = null;
        for (int index = 0; index < _staticGenerations.Length; index++)
        {
            AdvancedGpuDeformationStaticGeneration candidate =
                _staticGenerations[index];
            if (!candidate.Matches(scene, databaseEpoch, topologyGeneration))
                continue;

            if (matched is null || candidate.LastUse > matched.LastUse)
                matched = candidate;
        }
        if (matched is not null)
        {
            bool switched = !ReferenceEquals(_staticGeneration, matched);
            _staticGeneration = matched;
            matched.LastUse = ++_staticGenerationUse;
            _staticGenerationReplaced = switched;
            if (switched)
            {
                _previousOutputValid = false;
                AdvanceResourceGeneration();
            }
            return true;
        }

        AdvancedGpuDeformationStaticGeneration? replacement = null;
        for (int index = 0; index < _staticGenerations.Length; index++)
        {
            AdvancedGpuDeformationStaticGeneration candidate =
                _staticGenerations[index];
            if (candidate.PinCount != 0u)
                continue;
            if (replacement is null || candidate.LastUse < replacement.LastUse)
                replacement = candidate;
        }
        if (replacement is null)
            return false;

        replacement.Assign(scene, databaseEpoch, topologyGeneration);
        replacement.LastUse = ++_staticGenerationUse;
        _staticGeneration = replacement;
        _staticGenerationReplaced = true;
        _previousOutputValid = false;
        AdvanceResourceGeneration();
        return true;
    }

    /// <summary>
    /// Reclaims static mesh and pose ownership when the canonical scene
    /// generation changes. Reuse is deferred until every output slot is no
    /// longer referenced by the native backend.
    /// </summary>
    public bool TryResetStaticGenerationAtBoundary()
    {
        if (_frameOpen)
        {
            throw new InvalidOperationException(
                "Static deformation data can only be reset at a frame boundary.");
        }

        if (_staticGeneration.PinCount != 0u)
            return false;

        _staticGeneration.EnsureInitialized();
        _meshSlices.Clear();
        _poseEntries.Clear();
        _sourceVertexCount = 0u;
        _skinInfluenceCount = 0u;
        _spillInfluenceCount = 0u;
        _blendshapeRangeCount = 0u;
        _blendshapeRecordCount = 0u;
        _blendshapeDeltaCount = 1u;
        _blendshapeDeltas[0] = Vector4.Zero;
        _uploadedSourceVertexCount = 0u;
        _uploadedSkinInfluenceCount = 0u;
        _uploadedSpillInfluenceCount = 0u;
        _uploadedBlendshapeRangeCount = 0u;
        _uploadedBlendshapeRecordCount = 0u;
        _uploadedBlendshapeDeltaCount = 0u;
        _paletteCount = 0u;
        _activeBlendshapeCount = 0u;
        _previousOutputValid = false;
        _staticGenerationReplaced = true;
        AdvanceResourceGeneration();
        return true;
    }

    public bool TryBeginFrame(
        ulong frameId,
        ulong completedValue,
        uint currentFrameSlot,
        uint previousFrameSlot,
        uint requiredOutputVertexCapacity)
    {
        // Retained for renderer-independent resource probes. Production
        // preparation supplies the canonical key through the overload below.
        if (_staticGeneration.Scene is not null)
        {
            return TryBeginFrame(
                frameId,
                completedValue,
                currentFrameSlot,
                previousFrameSlot,
                requiredOutputVertexCapacity,
                _staticGeneration.Scene,
                _staticGeneration.DatabaseEpoch,
                _staticGeneration.TopologyGeneration);
        }
        if (_frameOpen)
            throw new InvalidOperationException(
                "The aggregate deformation GPU frame is already open.");
        if (currentFrameSlot >= (uint)_frameSlotCount ||
            previousFrameSlot >= (uint)_frameSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(currentFrameSlot));
        }

        _ = completedValue;
        if (!TryAcquireOutputSlot(currentFrameSlot))
            return false;
        ReleaseStaticGenerationPin(currentFrameSlot);
        _staticGeneration.EnsureInitialized();
        return TryOpenFrame(
            frameId,
            currentFrameSlot,
            previousFrameSlot,
            requiredOutputVertexCapacity);
    }

    /// <summary>
    /// Opens one output slot and then selects static source inputs. Reusing the
    /// current output slot releases its former static generation pin first.
    /// </summary>
    public bool TryBeginFrame(
        ulong frameId,
        ulong completedValue,
        uint currentFrameSlot,
        uint previousFrameSlot,
        uint requiredOutputVertexCapacity,
        GPUScene scene,
        ulong databaseEpoch,
        ulong topologyGeneration)
    {
        if (_frameOpen)
            throw new InvalidOperationException(
                "The aggregate deformation GPU frame is already open.");
        if (currentFrameSlot >= (uint)_frameSlotCount ||
            previousFrameSlot >= (uint)_frameSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(currentFrameSlot));
        }

        _ = completedValue;
        if (!TryAcquireOutputSlot(currentFrameSlot))
            return false;
        ReleaseStaticGenerationPin(currentFrameSlot);
        if (!TrySelectStaticGeneration(scene, databaseEpoch, topologyGeneration))
            return false;

        return TryOpenFrame(
            frameId,
            currentFrameSlot,
            previousFrameSlot,
            requiredOutputVertexCapacity);
    }

    private bool TryOpenFrame(
        ulong frameId,
        uint currentFrameSlot,
        uint previousFrameSlot,
        uint requiredOutputVertexCapacity)
    {
        bool outputReplaced = false;
        if (requiredOutputVertexCapacity > _outputBuffers.VertexCapacity)
        {
            if (!TryAcquireAllOutputSlots() ||
                !TryReplaceOutputBuffers(requiredOutputVertexCapacity))
            {
                return false;
            }
            ReleaseAllStaticGenerationPins();
            outputReplaced = true;
        }

        _frameId = frameId;
        // Pose entries only deduplicate one frame. Keeping prior entries pins
        // renderers from unloaded worlds in the process-wide preparation owner.
        _poseEntries.Clear();
        _currentFrameSlot = currentFrameSlot;
        _previousFrameSlot = previousFrameSlot;
        _paletteCount = 0u;
        _activeBlendshapeCount = 0u;
        _previousOutputValid =
            frameId != 0UL &&
            !outputReplaced &&
            !_staticGenerationReplaced &&
            _slotOutputValid[previousFrameSlot] &&
            _slotStaticGenerationIndices[previousFrameSlot] >= 0 &&
            ReferenceEquals(
                _staticGenerations[
                    _slotStaticGenerationIndices[previousFrameSlot]],
                _staticGeneration);
        _slotOutputValid[currentFrameSlot] = false;
        _staticGenerationReplaced = false;
        _frameOpen = true;
        return true;
    }

    public bool TryGetOrAddMesh(
        XRMesh mesh,
        uint topologyGeneration,
        out AdvancedGpuDeformationMeshSlice slice)
        => TryPrepareMesh(mesh, topologyGeneration, out slice) ==
            AdvancedGpuDeformationMeshPreparationStatus.Ready;

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

        XRMeshRenderer.BoneResourceSnapshot boneState = renderer.CaptureBoneResources();
        XRMeshRenderer.BlendshapeResourceSnapshot blendshapeState =
            renderer.CaptureBlendshapeResources();
        bool hasExternalPalette = renderer.HasExternalSkinPaletteSource;
        XRDataBuffer? paletteSource = hasExternalPalette
            ? renderer.ActiveSkinPaletteBuffer
            : boneState.SkinPalette;
        uint paletteBase = hasExternalPalette ? renderer.ActiveSkinPaletteBase : 0u;
        uint paletteCount = hasExternalPalette
            ? renderer.ActiveSkinPaletteCount
            : checked((uint)mesh.GetSkinningBufferStateSnapshot().UtilizedBones.Length + 1u);
        if (paletteSource is null ||
            paletteCount == 0u ||
            paletteBase + paletteCount >
                paletteSource.ElementCount)
        {
            slice = default;
            return false;
        }

        uint activeCount = checked((uint)Math.Max(
            0,
            blendshapeState.ActiveCount));
        uint requiredPalette = checked(_paletteCount + paletteCount);
        uint requiredActive =
            checked(_activeBlendshapeCount + activeCount);
        EnsureDynamicPoseCapacity(requiredPalette, requiredActive);

        CopyPalette(
            paletteSource,
            paletteBase,
            _paletteScratch,
            _paletteCount,
            paletteCount);
        CopyActiveBlendshapes(
            blendshapeState.ActiveWeights,
            _activeBlendshapeScratch,
            _activeBlendshapeCount,
            activeCount);

        slice = new AdvancedGpuDeformationPoseSlice(
            _paletteCount,
            paletteCount,
            _activeBlendshapeCount,
            activeCount,
            renderer.SkinnedOutputVersion,
            blendshapeState.WeightsVersion);
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

        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
            return false;

        _backend =
            RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend;
        if (_backend == RuntimeGraphicsApiKind.Unknown)
        {
            _backend = RuntimeEngine.Rendering.State.IsVulkan
                ? RuntimeGraphicsApiKind.Vulkan
                : RuntimeGraphicsApiKind.OpenGL;
        }

        // Pin before the first enqueue attempt. A throwing backend may have
        // accepted some work before it reports failure, and must not expose
        // its static inputs for reuse in that case.
        PinCurrentStaticGeneration(_currentFrameSlot);
        try
        {
            bool executed = _executor.TryExecute(
                planner,
                this,
                jobs,
                consumers,
                EAdvancedDeformationExecutionMode.AggregateCompute,
                admissionOverflowCount,
                out AdvancedDeformationDispatchTelemetry telemetry,
                out _,
                out uint enqueuedDispatchCount);
            LastTelemetry = telemetry;
            if (enqueuedDispatchCount == 0u)
            {
                ReleaseStaticGenerationPin(_currentFrameSlot);
                return false;
            }

            XRGpuFence? producerFence = renderer.InsertGpuFence();
            if (producerFence is null)
            {
                // Accepted work without a completion marker has unknown lifetime.
                // Poison the slot and retain its static source generation.
                _slotReusePoisoned[_currentFrameSlot] = true;
                return false;
            }
            if (_slotProducerFences[_currentFrameSlot] is not null)
            {
                producerFence.Dispose();
                _slotReusePoisoned[_currentFrameSlot] = true;
                return false;
            }

            _slotProducerFences[_currentFrameSlot] = producerFence;
            _slotOutputValid[_currentFrameSlot] = executed;
            return executed;
        }
        catch
        {
            _slotReusePoisoned[_currentFrameSlot] = true;
            throw;
        }
    }

    public void Dispatch(
        in AdvancedDeformationDispatchBatch batch,
        ReadOnlySpan<int> jobIndices)
    {
        ERendererComputeEnqueueStatus status =
            TryDispatch(in batch, jobIndices);
        if (status != ERendererComputeEnqueueStatus.Enqueued)
        {
            throw new InvalidOperationException(
                $"Aggregate deformation dispatch was rejected with {status}.");
        }
    }

    public ERendererComputeEnqueueStatus TryDispatch(
        in AdvancedDeformationDispatchBatch batch,
        ReadOnlySpan<int> jobIndices)
    {
        if (jobIndices.Length != checked((int)batch.JobCount))
            return ERendererComputeEnqueueStatus.InvalidResource;
        if (!SupportsAggregateCompute)
            return ERendererComputeEnqueueStatus.Unsupported;

        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
            return ERendererComputeEnqueueStatus.NoPassContext;

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
        return renderer.TryDispatchCompute(
            program,
            batch.WorkGroupCount,
            1u,
            1u);
    }

    public void ApplyBarrier(in AdvancedPreparationBarrier barrier)
    {
        ERendererComputeEnqueueStatus status =
            TryApplyBarrier(in barrier);
        if (status != ERendererComputeEnqueueStatus.Enqueued)
        {
            throw new InvalidOperationException(
                $"Aggregate deformation barrier was rejected with {status}.");
        }
    }

    public ERendererComputeEnqueueStatus TryApplyBarrier(
        in AdvancedPreparationBarrier barrier)
    {
        AbstractRenderer? renderer = AbstractRenderer.Current;
        return renderer is null
            ? ERendererComputeEnqueueStatus.NoPassContext
            : renderer.TryMemoryBarrier(ConvertBarrier(barrier.OpenGlMask));
    }

    /// <summary>
    /// Lowers barriers for consumers that acquire an already-dispatched
    /// shared publication later in the same world frame.
    /// </summary>
    public bool TryApplyConsumerBarriers(
        EAdvancedPreparationConsumer consumers)
    {
        if (consumers == EAdvancedPreparationConsumer.None)
            return true;

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
            if (TryApplyBarrier(in barriers[i]) !=
                ERendererComputeEnqueueStatus.Enqueued)
            {
                return false;
            }

        return true;
    }

    /// <summary>
    /// Closes CPU authoring for the current slot. GPU completion is represented
    /// only by its producer fence and exact backend resource lifetime.
    /// </summary>
    public void EndFrame(ulong authoringFrameId)
    {
        ThrowIfFrameClosed();
        _ = authoringFrameId;
        _frameOpen = false;
    }
    public void Dispose()
    {
        _aggregateProgram?.Destroy();
        // ShaderHelper owns the cached engine shader. Destroying it here leaves
        // later renderer generations holding a destroyed cache entry.
        _aggregateProgram = null;
        _aggregateShader = null;
        for (int generation = 0;
             generation < _staticGenerations.Length;
             generation++)
        {
            _staticGenerations[generation].Destroy();
        }
        _outputBuffers.Destroy();
        for (int slot = 0; slot < _frameSlotCount; slot++)
        {
            _jobBuffers[slot].Destroy();
            _jobIndexBuffers[slot].Destroy();
            _jobVertexOffsetBuffers[slot].Destroy();
            _paletteBuffers[slot].Destroy();
            _activeBlendshapeBuffers[slot].Destroy();
        }
        for (int slot = 0; slot < _slotProducerFences.Length; slot++)
        {
            _slotProducerFences[slot]?.Dispose();
            _slotProducerFences[slot] = null;
        }
        foreach (AdvancedGpuDeformationStaticGeneration generation in
                 _staticGenerations)
        {
            generation.ClearMeshSlices();
        }
        _poseEntries.Clear();
    }

    private void BuildBlendshapeSourceIndices(XRMesh mesh)
    {
        Vertex[] vertices = mesh.Vertices;
        string[] names = mesh.BlendshapeNames;
        if (vertices.Length != mesh.VertexCount || names.Length == 0)
            return;

        EnsureCpuCapacity(
            ref _blendshapeSourceIndices,
            checked((uint)vertices.Length * (uint)names.Length));
        for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
            List<(string name, VertexData data)>? shapes =
                vertices[vertexIndex].Blendshapes;
            _blendshapeFirstNameIndices.Clear();
            int firstNullNameIndex = -1;
            if (shapes is not null)
            {
                _blendshapeFirstNameIndices.EnsureCapacity(shapes.Count);
                for (int sourceIndex = 0; sourceIndex < shapes.Count; sourceIndex++)
                {
                    string sourceName = shapes[sourceIndex].name;
                    if (sourceName is null)
                    {
                        if (firstNullNameIndex < 0)
                            firstNullNameIndex = sourceIndex;
                    }
                    else
                    {
                        _blendshapeFirstNameIndices.TryAdd(sourceName, sourceIndex);
                    }
                }
            }

            for (int shapeIndex = 0; shapeIndex < names.Length; shapeIndex++)
            {
                string name = names[shapeIndex];
                int sourceIndex = -1;
                if (shapes is not null)
                {
                    if ((uint)shapeIndex < (uint)shapes.Count &&
                        string.Equals(shapes[shapeIndex].name, name, StringComparison.Ordinal))
                    {
                        sourceIndex = shapeIndex;
                    }
                    else if (name is null)
                    {
                        sourceIndex = firstNullNameIndex;
                    }
                    else if (_blendshapeFirstNameIndices.TryGetValue(name, out int firstIndex))
                    {
                        sourceIndex = firstIndex;
                    }
                }

                _blendshapeSourceIndices[
                    checked(shapeIndex * vertices.Length + vertexIndex)] = sourceIndex;
            }
        }
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
            for (int vertexIndex = 0;
                 vertexIndex < vertices.Length;
                 vertexIndex++)
            {
                Vertex source = vertices[vertexIndex];
                if (!TryGetIndexedBlendshapeData(
                        source,
                        _blendshapeSourceIndices[
                            checked(shapeIndex * vertices.Length + vertexIndex)],
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
            for (int vertexIndex = 0;
                 vertexIndex < vertices.Length;
                 vertexIndex++)
            {
                Vertex source = vertices[vertexIndex];
                if (!TryGetIndexedBlendshapeData(
                        source,
                        _blendshapeSourceIndices[
                            checked(shapeIndex * vertices.Length + vertexIndex)],
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
        if (!(delta.LengthSquared() > DeltaEpsilonSquared))
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
        for (uint vertexIndex = 0u;
             vertexIndex < checked((uint)mesh.VertexCount);
             vertexIndex++)
            _sourceVertices[destinationBase + vertexIndex] =
                PackCanonicalVertex(mesh, vertexIndex, destinationBase + vertexIndex);
    }

    private static AdvancedDeformedVertex PackCanonicalVertex(
        XRMesh mesh,
        uint vertexIndex,
        uint sourceIndex)
    {
        Vertex[] vertices = mesh.Vertices;
        if (vertices.Length == mesh.VertexCount)
            return AdvancedPackedVertexCodec.Pack(vertices[vertexIndex], sourceIndex);

        Vector3 position = mesh.PositionsBuffer!.GetVector3(vertexIndex);
        Vector3 normal = mesh.NormalsBuffer?.GetVector3(vertexIndex) ?? Vector3.UnitY;
        Vector4 tangent4 = mesh.TangentsBuffer?.GetVector4(vertexIndex)
            ?? new Vector4(Vector3.UnitX, 1.0f);
        Vector2 uv0 = mesh.TexCoordBuffers is { Length: > 0 } &&
            mesh.TexCoordBuffers[0] is XRDataBuffer uv0Buffer
                ? uv0Buffer.GetVector2(vertexIndex) : Vector2.Zero;
        Vector2 uv1 = mesh.TexCoordBuffers is { Length: > 1 } &&
            mesh.TexCoordBuffers[1] is XRDataBuffer uv1Buffer
                ? uv1Buffer.GetVector2(vertexIndex) : Vector2.Zero;
        Vector4 color0 = mesh.ColorBuffers is { Length: > 0 } &&
            mesh.ColorBuffers[0] is XRDataBuffer color0Buffer
                ? color0Buffer.GetVector4(vertexIndex) : Vector4.One;
        Vector4 color1 = mesh.ColorBuffers is { Length: > 1 } &&
            mesh.ColorBuffers[1] is XRDataBuffer color1Buffer
                ? color1Buffer.GetVector4(vertexIndex) : Vector4.One;
        return AdvancedPackedVertexCodec.Pack(
            position, normal,
            new Vector3(tangent4.X, tangent4.Y, tangent4.Z), tangent4.W,
            uv0, uv1, color0, color1, sourceIndex,
            mesh.TexCoordBuffers is { Length: > 1 } &&
            mesh.TexCoordBuffers[1] is XRDataBuffer);
    }

    private unsafe void PackSkinInfluences(
        XRMesh mesh,
        XRMeshSkinningBufferState skinningState,
        uint destinationBase,
        uint globalSpillBase)
    {
        XRDataBuffer indices =
            skinningState.CoreIndices ??
            throw new InvalidOperationException(
                "Canonical skinning indices are unavailable.");
        XRDataBuffer weights =
            skinningState.CoreWeights ??
            throw new InvalidOperationException(
                "Canonical skinning weights are unavailable.");
        byte* weightBytes = (byte*)weights.Address.Pointer;
        byte* indexBytes = (byte*)indices.Address.Pointer;
        uint* spillHeaders =
            skinningState.SpillHeaders is XRDataBuffer headers
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
        XRMeshSkinningBufferState skinningState,
        uint destinationBase,
        uint count)
    {
        if (count == 0u)
            return;

        XRDataBuffer sourceBuffer =
            skinningState.SpillEntries ??
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
        AdvancedGpuDeformationStaticBuffers previous = _staticBuffers;
        _staticBuffers = replacement;
        // A writable generation is never pinned. Its previous static buffers
        // therefore cannot be referenced by an output slot.
        previous.Destroy();
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
        uint capacity = NextPowerOfTwo(requiredCapacity);
        AdvancedGpuDeformationOutputBuffers replacement =
            new(_frameSlotCount, capacity);
        AdvancedGpuDeformationOutputBuffers previous = _outputBuffers;
        _outputBuffers = replacement;
        previous.Destroy();
        Array.Clear(_slotOutputValid);
        _resourceGeneration++;
        _outputCapacityGrowthCount++;
        return true;
    }

    private bool TryEnsureStaticInputsWritable()
    {
        if (_staticGeneration.PinCount == 0u)
            return true;

        AdvancedGpuDeformationStaticGeneration? successor = null;
        for (int index = 0; index < _staticGenerations.Length; index++)
        {
            AdvancedGpuDeformationStaticGeneration candidate =
                _staticGenerations[index];
            if (ReferenceEquals(candidate, _staticGeneration) ||
                candidate.PinCount != 0u)
            {
                continue;
            }
            if (successor is null || candidate.LastUse < successor.LastUse)
                successor = candidate;
        }
        if (successor is null || _staticGeneration.Scene is null)
            return false;

        AdvancedGpuDeformationStaticGeneration source = _staticGeneration;
        successor.Assign(
            source.Scene,
            source.DatabaseEpoch,
            source.TopologyGeneration);
        _staticGeneration = successor;
        EnsureCpuCapacity(ref _sourceVertices, source.SourceVertexCount);
        EnsureCpuCapacity(ref _skinInfluences, source.SkinInfluenceCount);
        EnsureCpuCapacity(ref _spillInfluences, source.SpillInfluenceCount);
        EnsureCpuCapacity(ref _blendshapeRanges, source.BlendshapeRangeCount);
        EnsureCpuCapacity(ref _blendshapeRecords, source.BlendshapeRecordCount);
        EnsureCpuCapacity(ref _blendshapeDeltas, source.BlendshapeDeltaCount);
        Array.Copy(source.SourceVertices, _sourceVertices, source.SourceVertexCount);
        Array.Copy(source.SkinInfluences, _skinInfluences, source.SkinInfluenceCount);
        Array.Copy(source.SpillInfluences, _spillInfluences, source.SpillInfluenceCount);
        Array.Copy(source.BlendshapeRanges, _blendshapeRanges, source.BlendshapeRangeCount);
        Array.Copy(source.BlendshapeRecords, _blendshapeRecords, source.BlendshapeRecordCount);
        Array.Copy(source.BlendshapeDeltas, _blendshapeDeltas, source.BlendshapeDeltaCount);
        foreach ((XRMesh mesh, AdvancedGpuDeformationMeshSlice slice) in
                 source.MeshSlices)
        {
            _meshSlices.Add(mesh, slice);
        }
        _sourceVertexCount = source.SourceVertexCount;
        _skinInfluenceCount = source.SkinInfluenceCount;
        _spillInfluenceCount = source.SpillInfluenceCount;
        _blendshapeRangeCount = source.BlendshapeRangeCount;
        _blendshapeRecordCount = source.BlendshapeRecordCount;
        _blendshapeDeltaCount = source.BlendshapeDeltaCount;
        if (!TryEnsureStaticBufferCapacity(
                _sourceVertexCount,
                _skinInfluenceCount,
                _spillInfluenceCount,
                _blendshapeRangeCount,
                _blendshapeRecordCount,
                _blendshapeDeltaCount))
        {
            _staticGeneration = source;
            return false;
        }
        UploadAllStatic(_staticBuffers);
        _uploadedSourceVertexCount = _sourceVertexCount;
        _uploadedSkinInfluenceCount = _skinInfluenceCount;
        _uploadedSpillInfluenceCount = _spillInfluenceCount;
        _uploadedBlendshapeRangeCount = _blendshapeRangeCount;
        _uploadedBlendshapeRecordCount = _blendshapeRecordCount;
        _uploadedBlendshapeDeltaCount = _blendshapeDeltaCount;
        successor.LastUse = ++_staticGenerationUse;
        _staticGenerationReplaced = true;
        _previousOutputValid = false;
        AdvanceResourceGeneration();
        return true;
    }

    private void PinCurrentStaticGeneration(uint slot)
    {
        int generationIndex = Array.IndexOf(_staticGenerations, _staticGeneration);
        if (generationIndex < 0 || _slotStaticGenerationIndices[slot] >= 0)
            throw new InvalidOperationException(
                "The deformation output slot already owns a static generation.");

        _slotStaticGenerationIndices[slot] = generationIndex;
        _staticGeneration.PinCount++;
    }

    private void ReleaseStaticGenerationPin(uint slot)
    {
        int generationIndex = _slotStaticGenerationIndices[slot];
        if (generationIndex < 0)
            return;

        AdvancedGpuDeformationStaticGeneration generation =
            _staticGenerations[generationIndex];
        if (generation.PinCount == 0u)
            throw new InvalidOperationException(
                "The deformation static-generation pin count is corrupt.");

        generation.PinCount--;
        _slotStaticGenerationIndices[slot] = -1;
    }

    private void ReleaseAllStaticGenerationPins()
    {
        for (uint slot = 0u; slot < (uint)_frameSlotCount; slot++)
            ReleaseStaticGenerationPin(slot);
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

    private void AdvanceResourceGeneration()
    {
        _resourceGeneration++;
        if (_resourceGeneration == 0UL)
            _resourceGeneration = 1UL;
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
        if (_aggregateProgram is not null)
            return _aggregateProgram;

        XRRenderProgram program = new(false, false, _aggregateShader)
        {
            Name = "AdvancedDeformation.Aggregate",
            AllowAsyncBackendCompile = true,
        };
        program.AllowLink();
        return _aggregateProgram = program;
    }

    private static bool CanReadCanonicalVertices(XRMesh mesh)
        => mesh.Vertices.Length == mesh.VertexCount ||
           mesh.PositionsBuffer is
           {
               ClientSideSource: not null,
           };

    private static bool TryGetIndexedBlendshapeData(
        Vertex vertex,
        int sourceIndex,
        out VertexData data)
    {
        List<(string name, VertexData data)>? shapes =
            vertex.Blendshapes;
        if (shapes is null || (uint)sourceIndex >= (uint)shapes.Count)
        {
            data = null!;
            return false;
        }
        data = shapes[sourceIndex].data;
        return data is not null;
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
    }}
