using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

public readonly record struct GPUPhysicsChainBandwidthSnapshot(
    long CpuUploadBytes,
    long GpuCopyBytes,
    long CpuReadbackBytes,
    int DispatchGroupCount,
    int DispatchIterationCount,
    long ResidentParticleBytes,
    long StandaloneCpuUploadBytes,
    long StandaloneCpuReadbackBytes,
    long BatchedCpuUploadBytes,
    long BatchedGpuCopyBytes,
    long BatchedCpuReadbackBytes,
    long HierarchyRecalcTicks)
{
    public long TotalTransferBytes => CpuUploadBytes + GpuCopyBytes + CpuReadbackBytes;
    public double HierarchyRecalcMilliseconds => HierarchyRecalcTicks * 1000.0 / Stopwatch.Frequency;

    public GPUPhysicsChainBandwidthSnapshot Delta(in GPUPhysicsChainBandwidthSnapshot previous)
        => new(
            CpuUploadBytes - previous.CpuUploadBytes,
            GpuCopyBytes - previous.GpuCopyBytes,
            CpuReadbackBytes - previous.CpuReadbackBytes,
            DispatchGroupCount - previous.DispatchGroupCount,
            DispatchIterationCount - previous.DispatchIterationCount,
            ResidentParticleBytes - previous.ResidentParticleBytes,
            StandaloneCpuUploadBytes - previous.StandaloneCpuUploadBytes,
            StandaloneCpuReadbackBytes - previous.StandaloneCpuReadbackBytes,
            BatchedCpuUploadBytes - previous.BatchedCpuUploadBytes,
            BatchedGpuCopyBytes - previous.BatchedGpuCopyBytes,
            BatchedCpuReadbackBytes - previous.BatchedCpuReadbackBytes,
            HierarchyRecalcTicks - previous.HierarchyRecalcTicks);
}

/// <summary>Bounded asynchronous readback health and latency telemetry.</summary>
public readonly record struct PhysicsChainReadbackDiagnostics(
    int InFlightCount,
    int PoolCount,
    int PoolHighWater,
    long SubmittedCount,
    long CompletedCount,
    long FailedFenceCount,
    long ReadFailureCount,
    long StaleDropCount,
    long EnqueueAttemptCount,
    long EnqueueFailureCount,
    string LastEnqueueFailureReason,
    long OldestInFlightAgeFrames,
    long MaximumCompletedLatencyFrames);

/// <summary>Last failed dispatcher stage and cumulative failure count.</summary>
public readonly record struct PhysicsChainDispatchDiagnostics(
    long FailureCount,
    string LastFailureStage,
    long LastFailureFrame);

/// <summary>
/// Centralized dispatcher that batches all GPU physics chain components into a single compute dispatch per frame.
/// This maximizes GPU parallelization by processing all physics chains in one dispatch.
/// </summary>
public sealed partial class GPUPhysicsChainDispatcher
{
    /// <summary>Reports particle storage and its upload route without reading GPU memory.</summary>
    public XRBufferStateSnapshot? ParticleArenaBufferStatus => _particlesBuffer?.GetStateSnapshot();

    /// <summary>Reports template storage and its upload route without reading GPU memory.</summary>
    public XRBufferStateSnapshot? TemplateArenaBufferStatus => _particleStaticBuffer?.GetStateSnapshot();

    private static readonly bool VerboseLogging = false;
    private const uint LocalSizeX = 128u;
    private const int MaxArenaElementCount = 1 << 25;
    private const int ActiveWorkCounterElementCount = (int)PhysicsChainKernelBucket.Count * 2;
    private const int IndirectArgumentElementCount = (int)PhysicsChainKernelBucket.Count * 3;
    private const uint ReadbackGatherLocalSizeX = 64u;
    private const int SelectiveReadbackSlotCount = 3;
    private static readonly int ParticleStateSizeBytes = Unsafe.SizeOf<GPUParticleData>();
    private static readonly int ParticleStaticSizeBytes = Unsafe.SizeOf<GPUParticleStaticData>();
    private static readonly int PerTreeParamsSizeBytes = Unsafe.SizeOf<GPUPerTreeParams>();
    private static readonly int ColliderSizeBytes = Unsafe.SizeOf<GPUColliderData>();
    private static readonly int AffineInputSizeBytes = Unsafe.SizeOf<PhysicsChainAffineInput>();
    private static readonly PhysicsChainComputePass ArenaGrowthCompletionPass = new(
        PhysicsChainComputePassKind.ArenaGrowth,
        EMemoryBarrierMask.BufferUpdate | EMemoryBarrierMask.ShaderStorage);
    private static readonly PhysicsChainComputePass InputPublicationPass = new(
        PhysicsChainComputePassKind.InputPublication,
        EMemoryBarrierMask.ClientMappedBuffer);
    private static readonly PhysicsChainComputePass SimulationCompletionPass = new(
        PhysicsChainComputePassKind.Simulation,
        EMemoryBarrierMask.ShaderStorage);
    private static readonly PhysicsChainComputePass BonePaletteCompletionPass = new(
        PhysicsChainComputePassKind.BonePalettePublication,
        EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.BufferUpdate);
    private static readonly PhysicsChainComputePass ActiveWorkResetCompletionPass = new(
        PhysicsChainComputePassKind.ActiveWorkReset,
        EMemoryBarrierMask.ShaderStorage);
    private static readonly PhysicsChainComputePass ActiveWorkCompactionCompletionPass = new(
        PhysicsChainComputePassKind.ActiveWorkCompaction,
        EMemoryBarrierMask.ShaderStorage);
    private static readonly PhysicsChainComputePass IndirectArgumentCompletionPass = new(
        PhysicsChainComputePassKind.IndirectArgumentGeneration,
        EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
    private static readonly PhysicsChainComputePass SelectiveReadbackGatherCompletionPass = new(
        PhysicsChainComputePassKind.SelectiveReadbackGather,
        EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.BufferUpdate);
    private static readonly PhysicsChainComputePass SelectiveReadbackTransferCompletionPass = new(
        PhysicsChainComputePassKind.ReadbackTransfer,
        EMemoryBarrierMask.BufferUpdate | EMemoryBarrierMask.GpuReadback);
    private const long MaximumPendingReadbackFenceAgeFrames = 256L;

    private static long s_cpuUploadBytes;
    private static long s_gpuCopyBytes;
    private static long s_cpuReadbackBytes;
    private static long s_standaloneCpuUploadBytes;
    private static long s_standaloneCpuReadbackBytes;
    private static long s_batchedCpuUploadBytes;
    private static long s_batchedGpuCopyBytes;
    private static long s_batchedCpuReadbackBytes;
    private static long s_hierarchyRecalcTicks;
    private static long s_arenaGrowthCopyBytes;
    private static long s_arenaStaticUploadBytes;
    private static long s_arenaDynamicUploadBytes;
    private static int s_dispatchGroupCount;
    private static int s_dispatchIterationCount;
    private static int s_arenaGrowthCount;
    private static long s_residentParticleBytes;
    private long _readbackSubmittedCount;
    private long _readbackCompletedCount;
    private long _readbackFailedFenceCount;
    private long _readbackReadFailureCount;
    private long _readbackEnqueueAttemptCount;
    private long _readbackEnqueueFailureCount;
    private string _lastReadbackEnqueueFailureReason = string.Empty;
    private long _maximumReadbackLatencyFrames;
    private int _readbackPoolHighWater;
    private long _dispatchFailureCount;
    private string _lastDispatchFailureStage = string.Empty;
    private long _lastDispatchFailureFrame = -1L;
    private bool _currentDispatchGroupIsBatched = true;

    private static GPUPhysicsChainDispatcher? _instance;
    public static GPUPhysicsChainDispatcher Instance => _instance ??= new GPUPhysicsChainDispatcher();

    private bool _enabled = true;
    private bool _initialized;
    private bool _backendEvaluated;
    private AbstractRenderer? _evaluatedRenderer;
    private IPhysicsChainComputeBackend? _computeBackend;
    private GPUPhysicsChainBackendStatus _backendStatus = GPUPhysicsChainBackendStatus.NotEvaluated;

    // Registered components
    private readonly ConcurrentDictionary<IPhysicsChainComputeSource, GPUPhysicsChainRequest> _registeredComponents = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly object _registrationTransitionSync = new();
    private readonly object _registeredComponentsSync = new();
    private readonly List<GPUPhysicsChainRequest> _registeredComponentSnapshot = [];
    private readonly Dictionary<IPhysicsChainComputeSource, GPUPhysicsChainRequest> _outputPublicationRequestByComponent =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly List<GPUPhysicsChainRequest> _activeRequests = new();
    private readonly List<GPUPhysicsChainRequest> _isolatedRequests = new();
    private bool _paletteOutputRefreshPending;
    private readonly List<GPUPhysicsChainRequest> _dispatchGroup = [];
    private readonly List<InFlightDispatch> _inFlight = [];
    private readonly List<DeferredPhysicsChainArenaResource> _deferredArenaResources = [];
    private readonly Stack<XRDataBuffer<GPUParticleData>> _readbackBufferPool = new();
    private readonly Dictionary<IPhysicsChainReadbackCoordinator, PhysicsChainSelectiveReadbackWorldResources> _selectiveReadbackWorldResources =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly List<PhysicsChainSelectiveReadbackWorldResources> _retiredSelectiveReadbackWorldResources = [];
    private readonly PhysicsChainReadbackGatherPlan?[] _pendingSelectiveReadbackPlans = new PhysicsChainReadbackGatherPlan?[SelectiveReadbackSlotCount];
    private readonly List<PhysicsChainGpuReadbackGatherItem> _selectiveReadbackGatherItems = [];
    private readonly HashSet<IPhysicsChainReadbackCoordinator> _readbackWorlds =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly HashSet<IPhysicsChainReadbackCoordinator> _readbackWorldsScheduledThisFrame =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly List<IPhysicsChainReadbackCoordinator> _readbackWorldsToRetire = [];

    // Shared GPU resources
    private XRShader? _mainPhysicsShader;
    private XRShader? _activeWorkShader;
    private XRShader? _gpuBonePaletteShader;
    private XRShader? _selectiveReadbackGatherShader;
    private XRRenderProgram? _mainPhysicsProgram;
    private XRRenderProgram? _activeWorkProgram;
    private XRRenderProgram? _gpuBonePaletteProgram;
    private XRRenderProgram? _selectiveReadbackGatherProgram;

    // Combined buffers for all components
    private XRDataBuffer<GPUParticleData>? _particlesBuffer;
    private XRDataBuffer<GPUParticleStaticData>? _particleStaticBuffer;
    private XRDataBuffer<PhysicsChainAffineInput>? _transformMatricesBuffer;
    private XRDataBuffer<GPUColliderData>? _collidersBuffer;
    private XRDataBuffer<GPUPerTreeParams>? _perTreeParamsBuffer;
    private XRDataBuffer<PhysicsChainGpuInstanceMetadata>? _instanceMetadataBuffer;
    private XRDataBuffer<PhysicsChainGpuTreeWorkItem>? _treeWorkItemBuffer;
    private XRDataBuffer<uint>? _activeTreeIdBuffer;
    private XRDataBuffer<uint>? _activeWorkCounterBuffer;
    private XRDataBuffer<uint>? _indirectDispatchArgumentBuffer;
    private XRDataBuffer<GPUDrivenBoneMappingData>? _gpuDrivenBonePaletteMappingsBuffer;
    private XRDataBuffer<SkinPaletteMatrix>? _gpuDrivenSkinPaletteBuffer;
    private XRDataBuffer<SkinPaletteMatrix>? _gpuDrivenPreviousSkinPaletteBuffer;
    private XRDataBuffer<Matrix4x4>? _gpuDrivenBoneInvBindMatricesBuffer;

    // Compact dynamic dispatch headers. Long-lived simulation resources use stable arenas.
    private readonly List<GPUPerTreeParams> _allPerTreeParams = [];
    private readonly List<PhysicsChainGpuInstanceMetadata> _instanceMetadata = [];
    private readonly List<PhysicsChainGpuTreeWorkItem> _treeWorkItems = [];
    private readonly List<GpuDrivenRendererPaletteBinding> _gpuDrivenPaletteBindings = [];
    private readonly List<GPUDrivenBoneMappingData> _gpuDrivenBoneMappings = [];
    private readonly List<SkinPaletteMatrix> _gpuDrivenSkinPalette = [];
    private readonly List<Matrix4x4> _gpuDrivenBoneInvBindMatrices = [];
    private int _gpuDrivenBonePaletteSignature = int.MinValue;
    private int _particleArenaHighWater;
    private int _particleArenaUploadedHighWater;
    private int _colliderArenaHighWater;
    private int _colliderArenaUploadedHighWater;
    private int _arenaLayoutResetRequested;
    private int _arenaResourceGeneration;
    private readonly List<PhysicsChainDynamicHeaderLayoutEntry> _dynamicHeaderLayout = [];
    private readonly List<PhysicsChainDynamicHeaderLayoutEntry> _activeWorkLayout = [];
    private int _activeWorkResourceGeneration = -1;
    private int _dynamicHeaderResourceGeneration = -1;
    private long _deferredArenaBytes;
    private PhysicsChainComputeBindings _mainPassBindings;
    private bool _mainPassBindingsValid;
    private PhysicsChainActiveWorkScanMode _activeWorkScanMode = PhysicsChainActiveWorkScanMode.PortableWorkgroup;
    private int _activeListCapacityPerBucket;
    private int _activeListGrowthCount;
    private int _activeWorkDispatchPassCount;
    private int _activeWorkStorageBarrierCount;
    private int _activeWorkCommandBarrierCount;
    private uint _activeWorkFrameIndex;
    private uint _readbackBackendGeneration = 1u;
    private uint _readbackArenaGeneration = 1u;
    private uint _readbackLayoutGeneration = 1u;
    private long _readbackFrameIndex;
    private long _lastReadbackPollFrame = -1L;
    // Statistics
    public int RegisteredComponentCount => _registeredComponents.Count;

    /// <summary>Reads the registration count without capturing full dispatcher diagnostics.</summary>
    public static int CaptureRegisteredComponentCount() => Instance.RegisteredComponentCount;
    public int TotalParticleCount { get; private set; }
    public int TotalTreeCount { get; private set; }
    public int TotalColliderCount { get; private set; }

    public GPUPhysicsChainBackendStatus BackendStatus
        => Volatile.Read(ref _backendStatus);

    /// <summary>Reports dispatch failures without reading GPU buffers.</summary>
    public PhysicsChainDispatchDiagnostics DispatchDiagnostics => GetDispatchDiagnosticsSnapshot();

    /// <summary>Reports asynchronous readback counters without requesting a readback.</summary>
    public PhysicsChainReadbackDiagnostics ReadbackDiagnostics => GetReadbackDiagnosticsSnapshot();

    /// <summary>Reports resident arena capacity and live allocation counts.</summary>
    public GPUPhysicsChainArenaDiagnostics ArenaDiagnostics => GetArenaDiagnosticsSnapshot();

    /// <summary>Reports palette allocation and publication counters.</summary>
    public PhysicsChainPaletteAtlasDiagnostics PaletteDiagnostics => GetPaletteAtlasDiagnosticsSnapshot();

    /// <summary>Reports solver command counts without reading GPU work counters.</summary>
    public PhysicsChainKernelDispatchDiagnostics KernelDiagnostics => GetKernelDispatchDiagnosticsSnapshot();

    public static GPUPhysicsChainBandwidthSnapshot GetBandwidthPressureSnapshot()
        => new(
            Interlocked.Read(ref s_cpuUploadBytes),
            Interlocked.Read(ref s_gpuCopyBytes),
            Interlocked.Read(ref s_cpuReadbackBytes),
            Volatile.Read(ref s_dispatchGroupCount),
            Volatile.Read(ref s_dispatchIterationCount),
            Interlocked.Read(ref s_residentParticleBytes),
            Interlocked.Read(ref s_standaloneCpuUploadBytes),
            Interlocked.Read(ref s_standaloneCpuReadbackBytes),
            Interlocked.Read(ref s_batchedCpuUploadBytes),
            Interlocked.Read(ref s_batchedGpuCopyBytes),
            Interlocked.Read(ref s_batchedCpuReadbackBytes),
            Interlocked.Read(ref s_hierarchyRecalcTicks));

    public GPUPhysicsChainArenaDiagnostics GetArenaDiagnosticsSnapshot()
        => new(
            GetCurrentArenaCapacityBytes(),
            GetCurrentArenaLiveBytes(),
            Interlocked.Read(ref _deferredArenaBytes),
            Interlocked.Read(ref s_arenaGrowthCopyBytes),
            Interlocked.Read(ref s_arenaStaticUploadBytes),
            Interlocked.Read(ref s_arenaDynamicUploadBytes),
            Volatile.Read(ref s_arenaGrowthCount),
            _arenaResourceGeneration);

    public PhysicsChainActiveWorkDiagnostics GetActiveWorkDiagnosticsSnapshot()
        => new(
            _activeWorkScanMode,
            _treeWorkItems.Count,
            _activeListCapacityPerBucket,
            _activeListGrowthCount,
            _activeWorkDispatchPassCount,
            _activeWorkStorageBarrierCount,
            _activeWorkCommandBarrierCount,
            _arenaResourceGeneration,
            UsesGpuAuthoredIndirectArguments: true);

    public PhysicsChainReadbackDiagnostics GetReadbackDiagnosticsSnapshot()
    {
        long oldestAge = 0L;
        for (int i = 0; i < _inFlight.Count; ++i)
            oldestAge = Math.Max(oldestAge, Math.Max(_readbackFrameIndex - _inFlight[i].SubmittedFrame, 0L));
        long staleDropCount = 0L;
        foreach (IPhysicsChainReadbackCoordinator world in _readbackWorlds)
            staleDropCount += world.GetReadbackTransferCounters().DiscardedStaleElements;

        return new PhysicsChainReadbackDiagnostics(
            _inFlight.Count,
            _readbackBufferPool.Count,
            _readbackPoolHighWater,
            _readbackSubmittedCount,
            _readbackCompletedCount,
            _readbackFailedFenceCount,
            _readbackReadFailureCount,
            staleDropCount,
            _readbackEnqueueAttemptCount,
            _readbackEnqueueFailureCount,
            _lastReadbackEnqueueFailureReason,
            oldestAge,
            _maximumReadbackLatencyFrames);
    }

    public PhysicsChainDispatchDiagnostics GetDispatchDiagnosticsSnapshot()
        => new(_dispatchFailureCount, _lastDispatchFailureStage, _lastDispatchFailureFrame);

    private bool RecordDispatchFailure(string stage)
    {
        ++_dispatchFailureCount;
        _lastDispatchFailureStage = stage;
        _lastDispatchFailureFrame = _readbackFrameIndex;
        return false;
    }

    internal static PhysicsChainActiveWorkScanMode SelectActiveWorkScanMode(in PhysicsChainComputeCapabilities capabilities)
        => capabilities.SupportsSubgroupArithmetic
            ? PhysicsChainActiveWorkScanMode.SubgroupArithmetic
            : PhysicsChainActiveWorkScanMode.PortableWorkgroup;

    public static void ResetBandwidthPressureStats()
    {
        Interlocked.Exchange(ref s_cpuUploadBytes, 0L);
        Interlocked.Exchange(ref s_gpuCopyBytes, 0L);
        Interlocked.Exchange(ref s_cpuReadbackBytes, 0L);
        Interlocked.Exchange(ref s_standaloneCpuUploadBytes, 0L);
        Interlocked.Exchange(ref s_standaloneCpuReadbackBytes, 0L);
        Interlocked.Exchange(ref s_batchedCpuUploadBytes, 0L);
        Interlocked.Exchange(ref s_batchedGpuCopyBytes, 0L);
        Interlocked.Exchange(ref s_batchedCpuReadbackBytes, 0L);
        Interlocked.Exchange(ref s_hierarchyRecalcTicks, 0L);
        Interlocked.Exchange(ref s_arenaGrowthCopyBytes, 0L);
        Interlocked.Exchange(ref s_arenaStaticUploadBytes, 0L);
        Interlocked.Exchange(ref s_arenaDynamicUploadBytes, 0L);
        Interlocked.Exchange(ref s_dispatchGroupCount, 0);
        Interlocked.Exchange(ref s_dispatchIterationCount, 0);
        Interlocked.Exchange(ref s_arenaGrowthCount, 0);
    }

    internal static void RecordCpuUploadBytes(long bytes, bool isBatched)
    {
        if (bytes <= 0)
            return;

        Interlocked.Add(ref s_cpuUploadBytes, bytes);
        if (isBatched)
            Interlocked.Add(ref s_batchedCpuUploadBytes, bytes);
        else
            Interlocked.Add(ref s_standaloneCpuUploadBytes, bytes);
    }

    internal static void RecordGpuCopyBytes(long bytes, bool isBatched)
    {
        if (bytes <= 0)
            return;

        Interlocked.Add(ref s_gpuCopyBytes, bytes);
        if (isBatched)
            Interlocked.Add(ref s_batchedGpuCopyBytes, bytes);
    }

    internal static void RecordCpuReadbackBytes(long bytes, bool isBatched)
    {
        if (bytes <= 0)
            return;

        Interlocked.Add(ref s_cpuReadbackBytes, bytes);
        if (isBatched)
            Interlocked.Add(ref s_batchedCpuReadbackBytes, bytes);
        else
            Interlocked.Add(ref s_standaloneCpuReadbackBytes, bytes);
    }

    internal static void RecordHierarchyRecalcTicks(long ticks)
    {
        if (ticks > 0)
            Interlocked.Add(ref s_hierarchyRecalcTicks, ticks);
    }

    #region GPU Data Structures

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUParticleData
    {
        public Vector3 Position;
        public float _pad0;
        public Vector3 PrevPosition;
        public float _pad1;
        public int IsColliding;
        public Vector3 _pad2;
        public Vector3 PreviousPhysicsPosition;
        public float _pad3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUParticleStaticData
    {
        public Vector3 TransformLocalPosition;
        public float _pad0;
        public int ParentIndex;
        public float Damping;
        public float Elasticity;
        public float Stiffness;
        public float Inert;
        public float Friction;
        public float Radius;
        public float BoneLength;
        public int TreeIndex;
        public int _pad1;
        public int _pad2;
        public int _pad3;
    }

    /// <summary>
    /// Per-tree dispatch parameters passed via SSBO so all trees can be dispatched in a single compute call.
    /// Layout must match the GLSL PerTreeParams struct (std430).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUPerTreeParams
    {
        public float DeltaTime;
        public float ObjectScale;
        public float Weight;
        public int FreezeAxis;
        public Vector3 Force;
        public int ColliderCount;
        public Vector3 Gravity;
        public int ColliderOffset;
        public Vector3 ObjectMove;
        public float _pad0;
        public Vector3 RestGravity;
        public int ParticleOffset;
        public int ParticleCount;
        public int LoopCount;
        public int DepthRangeOffset;
        public int DepthRangeCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUParticleTreeData
    {
        public Vector3 RestGravity;
        public int ParticleOffset;
        public int ParticleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUColliderData
    {
        public Vector4 Center;
        public Vector4 Params;
        public Vector4 Orientation;
        public int Type;
        public int _pad0;
        public int _pad1;
        public int _pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUDispatchParams
    {
        public float DeltaTime;
        public float ObjectScale;
        public float Weight;
        public int FreezeAxis;
        public Vector3 Force;
        public int ColliderCount;
        public Vector3 Gravity;
        public int ColliderOffset;
        public Vector3 ObjectMove;
        public int TreeOffset;
        public int ParticleOffset;
        public int ParticleCount;
        public int _pad0;
        public int _pad1;
    }

    [XREngine.Rendering.Shaders.GpuRecord("BoneMappingData", "GPUScene")]
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUDrivenBoneMappingData
    {
        public int ParticleIndex;
        public int ChildParticleIndex;
        public int BoneMatrixIndex;
        public int Flags;
        [XREngine.Rendering.Shaders.GpuPaddedVector3(nameof(_pad0))]
        public Vector3 RestLocalDirection;
        public float _pad0;
    }

    public readonly record struct GpuDrivenRendererPaletteBinding(
        IPhysicsChainComputeSource Component,
        XRMeshRenderer Renderer,
        GPUDrivenBoneMappingData[] Mappings,
        int ParticleBaseOffset,
        uint BoneMatrixElementCount,
        bool DrivesCompleteBonePalette,
        int BindingGeneration,
        int ParticleStateVersion,
        long RendererBoneBufferGeneration);

    #endregion

    #region Registration

    /// <summary>
    /// Registers a GPU physics chain component for batched processing.
    /// </summary>
    public void Register(IPhysicsChainComputeSource component)
    {
        lock (_registrationTransitionSync)
            RegisterCore(component);
    }

    private void RegisterCore(IPhysicsChainComputeSource component)
    {
        var request = new GPUPhysicsChainRequest(component);
        GPUPhysicsChainRequest? replacedRequest = null;
        bool registered;

        lock (_registeredComponentsSync)
        {
            for (int requestIndex = _registeredComponentSnapshot.Count - 1; requestIndex >= 0; --requestIndex)
            {
                GPUPhysicsChainRequest candidate = _registeredComponentSnapshot[requestIndex];
                if (ReferenceEquals(candidate.Component, component)
                    || candidate.Component.ID != component.ID)
                    continue;

                _registeredComponentSnapshot.RemoveAt(requestIndex);
                _registeredComponents.TryRemove(candidate.Component, out _);
                if (candidate.DebugSelected)
                {
                    --_selectedGpuDebugChainCount;
                    ++_gpuDebugSelectionRevision;
                    candidate.DebugSelected = false;
                    QueueGpuDebugWorldRetirementIfUnused(candidate.DebugSelectedWorld);
                }
                replacedRequest ??= candidate;
            }

            if (replacedRequest is not null)
                request.AdoptArenaAllocationFrom(replacedRequest);

            registered = _registeredComponents.TryAdd(component, request);
            if (registered)
            {
                request.DebugSelected = component.DebugDrawChains;
                request.DebugSelectedWorld = request.DebugSelected ? component.World : null;
                if (request.DebugSelected)
                {
                    ++_selectedGpuDebugChainCount;
                    ++_gpuDebugSelectionRevision;
                    if (request.DebugSelectedWorld is { } selectedWorld)
                        _gpuDebugWorldsPendingRetirement.Remove(selectedWorld);
                }
                _registeredComponentSnapshot.Add(request);
            }
        }

        if (replacedRequest is not null)
        {
            try
            {
                replacedRequest.Component.ClearBatchedGpuDrivenBonePaletteSources();
            }
            catch (Exception exception)
            {
                XREngine.Debug.LogException(exception);
            }
        }

        if (registered)
        {
            if (VerboseLogging)
            {
                string replacement = replacedRequest is null
                    ? string.Empty
                    : $", ReplacedRequestId={replacedRequest.RequestId}";
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] Register: Component registered. ComponentHash={component.GetHashCode():X}, RequestId={request.RequestId}, TotalRegistered={_registeredComponents.Count}{replacement}");
            }
        }
        else
        {
            XREngine.Debug.LogWarning($"[GPUPhysicsChainDispatcher] Register: Component already registered (TryAdd failed). ComponentHash={component.GetHashCode():X}. This may indicate a double-registration bug.");
        }
    }

    /// <summary>
    /// Unregisters a GPU physics chain component.
    /// </summary>
    public void Unregister(IPhysicsChainComputeSource component)
    {
        lock (_registrationTransitionSync)
            UnregisterCore(component);
    }

    private void UnregisterCore(IPhysicsChainComputeSource component)
    {
        GPUPhysicsChainRequest? request;
        lock (_registeredComponentsSync)
        {
            if (_registeredComponents.TryRemove(component, out request))
            {
                _registeredComponentSnapshot.Remove(request);
                if (request.DebugSelected)
                {
                    --_selectedGpuDebugChainCount;
                    ++_gpuDebugSelectionRevision;
                    request.DebugSelected = false;
                    QueueGpuDebugWorldRetirementIfUnused(request.DebugSelectedWorld);
                    if (_selectedGpuDebugChainCount == 0)
                        _gpuDebugDiagnostics = default;
                }
                if (_registeredComponentSnapshot.Count == 0)
                    Interlocked.Exchange(ref _arenaLayoutResetRequested, 1);
            }
        }

        if (request is not null)
        {
            // Release the removed request's palette source before a replacement can register.
            try
            {
                request.Component.ClearBatchedGpuDrivenBonePaletteSources();
            }
            catch (Exception exception)
            {
                XREngine.Debug.LogException(exception);
            }
            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] Unregister: Component unregistered. ComponentHash={component.GetHashCode():X}, RequestId={request.RequestId}, TotalRegistered={_registeredComponents.Count}");
            return;
        }

        XREngine.Debug.LogWarning($"[GPUPhysicsChainDispatcher] Unregister: Component was not registered (TryRemove failed). ComponentHash={component.GetHashCode():X}. This may indicate an unbalanced register/unregister.");
    }
    /// <summary>
    /// Checks if a component is registered for batched processing.
    /// </summary>
    public bool IsRegistered(IPhysicsChainComputeSource component)
        => _registeredComponents.ContainsKey(component);

    #endregion

    #region Main Processing

    /// <summary>
    /// Copies <paramref name="source"/> into a request-owned <paramref name="backing"/> array
    /// and returns it as the new <see cref="IReadOnlyList{T}"/>. The backing array is
    /// exactly-sized and only reallocated when the element count changes, so steady-state
    /// frames incur zero heap allocations from this path.
    /// </summary>
    private static T[] SnapshotInto<T>(IReadOnlyList<T> source, ref T[] backing)
    {
        int count = source.Count;
        if (count == 0)
            return [];

        if (backing.Length != count)
            backing = new T[count];

        if (source is List<T> list)
            CollectionsMarshal.AsSpan(list).CopyTo(backing);
        else
            for (int i = 0; i < count; i++)
                backing[i] = source[i];

        return backing;
    }

    private static T[] SnapshotInto<T>(ReadOnlySpan<T> source, ref T[] backing)
    {
        if (source.Length == 0)
            return [];
        if (backing.Length != source.Length)
            backing = new T[source.Length];
        source.CopyTo(backing);
        return backing;
    }

    private static void SnapshotParticles(ReadOnlySpan<PhysicsChainGpuParticle> source, ref GPUParticleData[] backing)
    {
        if (backing.Length != source.Length)
            backing = source.Length == 0 ? [] : new GPUParticleData[source.Length];
        for (int i = 0; i < source.Length; ++i)
        {
            PhysicsChainGpuParticle value = source[i];
            backing[i] = new GPUParticleData
            {
                Position = value.Position,
                PrevPosition = value.PrevPosition,
                IsColliding = value.IsColliding,
                PreviousPhysicsPosition = value.PreviousPhysicsPosition,
            };
        }
    }

    private static void SnapshotParticleStatic(ReadOnlySpan<PhysicsChainGpuParticleStatic> source, ref GPUParticleStaticData[] backing)
    {
        if (backing.Length != source.Length)
            backing = source.Length == 0 ? [] : new GPUParticleStaticData[source.Length];
        for (int i = 0; i < source.Length; ++i)
        {
            PhysicsChainGpuParticleStatic value = source[i];
            backing[i] = new GPUParticleStaticData
            {
                TransformLocalPosition = value.TransformLocalPosition,
                ParentIndex = value.ParentIndex,
                Damping = value.Damping,
                Elasticity = value.Elasticity,
                Stiffness = value.Stiffness,
                Inert = value.Inert,
                Friction = value.Friction,
                Radius = value.Radius,
                BoneLength = value.BoneLength,
                TreeIndex = value.TreeIndex,
            };
        }
    }

    private static void SnapshotTrees(ReadOnlySpan<PhysicsChainGpuTree> source, ref GPUParticleTreeData[] backing)
    {
        if (backing.Length != source.Length)
            backing = source.Length == 0 ? [] : new GPUParticleTreeData[source.Length];
        for (int i = 0; i < source.Length; ++i)
        {
            PhysicsChainGpuTree value = source[i];
            backing[i] = new GPUParticleTreeData
            {
                RestGravity = value.RestGravity,
                ParticleOffset = value.ParticleOffset,
                ParticleCount = value.ParticleCount,
            };
        }
    }

    private static void SnapshotColliders(ReadOnlySpan<PhysicsChainGpuCollider> source, ref GPUColliderData[] backing)
    {
        if (backing.Length != source.Length)
            backing = source.Length == 0 ? [] : new GPUColliderData[source.Length];
        for (int i = 0; i < source.Length; ++i)
        {
            PhysicsChainGpuCollider value = source[i];
            backing[i] = new GPUColliderData
            {
                Center = value.Center,
                Params = value.Params,
                Orientation = value.Orientation,
                Type = value.Type,
            };
        }
    }

    /// <summary>Copies borrowed chain data into the pending packet during this call.</summary>
    public void SubmitData(
        IPhysicsChainComputeSource component,
        in PhysicsChainGpuDispatchSnapshot snapshot,
        bool effectiveGpuDrivenSkinning)
    {
        if (!_registeredComponents.TryGetValue(component, out var request))
            return;

        lock (request.InputGate)
        {
            PhysicsChainRenderInputPacket input = request.PendingInput;
            if (input.ParticleStateVersion != snapshot.ParticleStateVersion || input.Particles.Length != snapshot.Particles.Length)
                SnapshotParticles(snapshot.Particles, ref input.Particles);
            if (input.StaticDataVersion != snapshot.StaticDataVersion || input.ParticleStaticData.Length != snapshot.ParticleStatic.Length)
                SnapshotParticleStatic(snapshot.ParticleStatic, ref input.ParticleStaticData);
            if (input.TreeDataVersion != snapshot.TreeDataVersion || input.Trees.Length != snapshot.Trees.Length)
                SnapshotTrees(snapshot.Trees, ref input.Trees);
            if (input.TransformDataSignature != snapshot.TransformSignature || input.Transforms.Length != snapshot.Transforms.Length)
                input.Transforms = SnapshotInto(snapshot.Transforms, ref input.Transforms);
            if (input.ColliderDataSignature != snapshot.ColliderSignature || input.Colliders.Length != snapshot.Colliders.Length)
                SnapshotColliders(snapshot.Colliders, ref input.Colliders);
            if (input.BoneStructureSignature != snapshot.BoneStructureSignature || input.Bones.Length != snapshot.Bones.Length)
                input.Bones = SnapshotInto(snapshot.Bones, ref input.Bones);

            CompleteSubmittedInput(component, request, input, snapshot.DeltaTime, snapshot.ObjectScale,
                snapshot.Weight, snapshot.Force, snapshot.Gravity, snapshot.ObjectMove,
                snapshot.FreezeAxis, snapshot.LoopCount, snapshot.TimeVar,
                snapshot.ExecutionGeneration, snapshot.SubmissionId, snapshot.StaticDataVersion,
                snapshot.TreeDataVersion, snapshot.ParticleStateVersion, snapshot.TransformSignature,
                snapshot.ColliderSignature, snapshot.BoneStructureSignature,
                effectiveGpuDrivenSkinning, snapshot.SpatialInput, snapshot.ReadbackSourceGeneration);
        }
    }

    /// <summary>
    /// Submits particle and collider data for a component. Called during component's Prepare phase.
    /// </summary>
    public void SubmitData(
        IPhysicsChainComputeSource component,
        IReadOnlyList<GPUParticleData> particles,
        IReadOnlyList<GPUParticleStaticData> particleStaticData,
        IReadOnlyList<GPUParticleTreeData> trees,
        IReadOnlyList<Matrix4x4> transforms,
        IReadOnlyList<GPUColliderData> colliders,
        float deltaTime,
        float objectScale,
        float weight,
        Vector3 force,
        Vector3 gravity,
        Vector3 objectMove,
        int freezeAxis,
        int loopCount,
        float timeVar,
        int executionGeneration,
        long submissionId,
        int staticDataVersion,
        int particleStateVersion,
        int transformDataSignature,
        int colliderDataSignature,
        int? treeDataVersion = null,
        ReadOnlySpan<PhysicsChainGpuBone> bones = default,
        int boneStructureSignature = 0,
        bool effectiveGpuDrivenSkinning = false,
        PhysicsChainGpuSpatialInput spatialInput = default)
    {
        if (!_registeredComponents.TryGetValue(component, out var request))
            return;

        lock (request.InputGate)
        {
            PhysicsChainRenderInputPacket input = request.PendingInput;
            int treeVersion = treeDataVersion ?? staticDataVersion;
            if (input.ParticleStateVersion != particleStateVersion || input.Particles.Length != particles.Count)
                input.Particles = SnapshotInto(particles, ref input.Particles);
            if (input.StaticDataVersion != staticDataVersion || input.ParticleStaticData.Length != particleStaticData.Count)
                input.ParticleStaticData = SnapshotInto(particleStaticData, ref input.ParticleStaticData);
            if (input.TreeDataVersion != treeVersion || input.Trees.Length != trees.Count)
                input.Trees = SnapshotInto(trees, ref input.Trees);
            if (input.TransformDataSignature != transformDataSignature || input.Transforms.Length != transforms.Count)
                input.Transforms = SnapshotInto(transforms, ref input.Transforms);
            if (input.ColliderDataSignature != colliderDataSignature || input.Colliders.Length != colliders.Count)
                input.Colliders = SnapshotInto(colliders, ref input.Colliders);
            if (input.BoneStructureSignature != boneStructureSignature || input.Bones.Length != bones.Length)
                input.Bones = SnapshotInto(bones, ref input.Bones);

            CompleteSubmittedInput(component, request, input, deltaTime, objectScale, weight,
                force, gravity, objectMove, freezeAxis, loopCount, timeVar, executionGeneration,
                submissionId, staticDataVersion, treeVersion, particleStateVersion,
                transformDataSignature, colliderDataSignature, boneStructureSignature,
                effectiveGpuDrivenSkinning, spatialInput, component.ReadbackSourceGeneration);
        }
    }

    private static void CompleteSubmittedInput(
        IPhysicsChainComputeSource component,
        GPUPhysicsChainRequest request,
        PhysicsChainRenderInputPacket input,
        float deltaTime,
        float objectScale,
        float weight,
        Vector3 force,
        Vector3 gravity,
        Vector3 objectMove,
        int freezeAxis,
        int loopCount,
        float timeVar,
        int executionGeneration,
        long submissionId,
        int staticDataVersion,
        int treeDataVersion,
        int particleStateVersion,
        int transformDataSignature,
        int colliderDataSignature,
        int boneStructureSignature,
        bool effectiveGpuDrivenSkinning,
        PhysicsChainGpuSpatialInput spatialInput,
        long readbackSourceGeneration)
    {
        input.DeltaTime = deltaTime;
        input.ObjectScale = objectScale;
        input.Weight = weight;
        input.Force = force;
        input.Gravity = gravity;
        input.ObjectMove = objectMove;
        input.FreezeAxis = freezeAxis;
        input.LoopCount = loopCount;
        input.TimeVar = timeVar;
        input.UpdateMode = component.UpdateMode;
        input.DispatchIsolationKey = component.UseBatchedDispatcher ? 0 : request.RequestId;
        input.ExecutionGeneration = executionGeneration;
        input.ReadbackSourceGeneration = readbackSourceGeneration;
        input.SubmissionId = submissionId;
        input.StaticDataVersion = staticDataVersion;
        input.TreeDataVersion = treeDataVersion;
        input.ParticleStateVersion = particleStateVersion;
        input.TransformDataSignature = transformDataSignature;
        input.ColliderDataSignature = colliderDataSignature;
        input.BoneStructureSignature = boneStructureSignature;
        input.EffectiveGpuDrivenSkinning = effectiveGpuDrivenSkinning;
        input.SpatialInput = spatialInput;
        input.ValidateAffineInputs(transformDataSignature);
        input.HasData = true;
        input.Sequence = ++request.NextInputSequence;
        request.NeedsUpdate = true;
    }

    /// <summary>
    /// Updates compact GPU scheduling metadata without changing structural arena ownership.
    /// </summary>
    public bool SetGpuSchedulingMetadata(
        IPhysicsChainComputeSource component,
        bool enabled,
        bool relevant,
        bool sleeping,
        uint qualityTier,
        uint cadence,
        uint phase,
        uint featureMask)
    {
        if (!_registeredComponents.TryGetValue(component, out GPUPhysicsChainRequest? request))
            return false;

        uint resolvedEnabled = enabled ? 1u : 0u;
        uint resolvedRelevant = relevant ? 1u : 0u;
        uint resolvedSleepState = sleeping ? 1u : 0u;
        uint resolvedCadence = Math.Max(cadence, 1u);
        uint resolvedPhase = phase % resolvedCadence;
        lock (request.InputGate)
        {
            PhysicsChainRenderInputPacket input = request.PendingInput;
            if (input.Enabled == resolvedEnabled
                && input.Relevant == resolvedRelevant
                && input.SleepState == resolvedSleepState
                && input.QualityTier == qualityTier
                && input.Cadence == resolvedCadence
                && input.Phase == resolvedPhase
                && input.FeatureMask == featureMask)
                return true;

            if (input.Sequence <= request.AcceptedInput.Sequence && request.AcceptedInput.HasData)
                input.CopyDataFrom(request.AcceptedInput);

            input.Enabled = resolvedEnabled;
            input.Relevant = resolvedRelevant;
            input.SleepState = resolvedSleepState;
            input.QualityTier = qualityTier;
            input.Cadence = resolvedCadence;
            input.Phase = resolvedPhase;
            input.FeatureMask = featureMask;
            input.SchedulingMetadataInitialized = true;
            input.Sequence = ++request.NextInputSequence;
            request.NeedsUpdate = true;
        }
        return true;
    }

    public bool TryGetRenderParticleBuffers(IPhysicsChainComputeSource component, out XRDataBuffer? particleBuffer, out XRDataBuffer? particleStaticBuffer, out int particleOffset, out int particleCount)
    {
        particleBuffer = _particlesBuffer;
        particleStaticBuffer = _particleStaticBuffer;
        particleOffset = 0;
        particleCount = 0;

        if (!_registeredComponents.TryGetValue(component, out GPUPhysicsChainRequest? request)
            || particleBuffer is null
            || particleStaticBuffer is null
            || request.ParticleOffset < 0)
            return false;

        particleOffset = request.ParticleOffset;
        particleCount = request.Particles.Count > 0 ? request.Particles.Count : request.LastKnownParticleCount;
        return particleCount > 0;
    }

    /// <summary>
    /// Process all registered components in a single batched dispatch.
    /// Should be called from the render thread (e.g., GlobalPreRender or GlobalPostRender).
    /// </summary>
    public void ProcessDispatches()
    {
        lock (_registrationTransitionSync)
            ProcessDispatchesCore();
    }

    private void ProcessDispatchesCore()
    {
        if (!_enabled)
            return;

        PollSimulationReceipts();
        _paletteOutputRefreshPending |= ConfirmResidentSpatialState();
        unchecked { ++_readbackFrameIndex; }
        _readbackWorldsScheduledThisFrame.Clear();
        ApplyPendingArenaLayoutReset();

        // Collect active requests
        _activeRequests.Clear();
        _isolatedRequests.Clear();
        lock (_registeredComponentsSync)
        {
            _activeRequests.EnsureCapacity(_registeredComponentSnapshot.Count);
            _isolatedRequests.EnsureCapacity(_registeredComponentSnapshot.Count);
            for (int i = 0; i < _registeredComponentSnapshot.Count; ++i)
            {
                GPUPhysicsChainRequest request = _registeredComponentSnapshot[i];
                if (!request.TryAcceptInput(out ulong rejectedSequence,
                    out int invalidTransformIndex, out bool transformCountMismatch))
                {
                    if (rejectedSequence != 0u)
                        RecordDispatchFailure(transformCountMismatch
                            ? $"AffineTransformCountMismatch:{request.RequestId}:{rejectedSequence}:{invalidTransformIndex}"
                            : $"NonAffineTransformInput:{request.RequestId}:{rejectedSequence}:{invalidTransformIndex}");
                    continue;
                }

                if (request.DispatchIsolationKey == 0)
                    _activeRequests.Add(request);
                else
                    _isolatedRequests.Add(request);
            }

            // Registration order is the stable order within both dispatch buckets.
        }

        for (int i = 0; i < _isolatedRequests.Count; ++i)
            _activeRequests.Add(_isolatedRequests[i]);

        for (int i = 0; i < _activeRequests.Count; ++i)
        {
            GPUPhysicsChainRequest request = _activeRequests[i];
            PhysicsChainRenderInputPacket accepted = request.AcceptedInput;
            request.Component.AcceptGpuDrivenBoneBindings(accepted.Bones, accepted.BoneStructureSignature,
                accepted.ParticleStateVersion, accepted.EffectiveGpuDrivenSkinning);
        }

        bool paletteBindingsChanged = false;
        lock (_registeredComponentsSync)
        {
            for (int i = 0; i < _registeredComponentSnapshot.Count; ++i)
                paletteBindingsChanged |= _registeredComponentSnapshot[i].Component.RefreshGpuDrivenBoneBindings();
        }
        _paletteOutputRefreshPending |= paletteBindingsChanged;

        bool pendingSelectiveReadback = HasPendingSelectiveReadback();
        if (_activeRequests.Count == 0 && !_paletteOutputRefreshPending && !pendingSelectiveReadback)
            return;

        IPhysicsChainComputeBackend? backend = ResolveComputeBackend(AbstractRenderer.Current);
        if (backend is null)
            return;

        EnsureInitialized();

        // Gate on backend program link readiness. Binding a compute program
        // whose link is still queued on the shared GL context can deadlock the
        // render thread on NVIDIA (parallel-link worker hazard observed in
        // GlobalPreRender). The first call to Link() kicks off async
        // compilation; we skip this frame's dispatch and try again next frame.
        if (_mainPhysicsProgram is { } mainPgm && !EnsureProgramLinked(mainPgm)
            || _activeWorkProgram is { } activeWorkPgm && !EnsureProgramLinked(activeWorkPgm))
            return;

        if (!IsSpecializedKernelReady())
            return;
        _activeWorkScanMode = SelectActiveWorkScanMode(backend.Capabilities);
        unchecked { ++_activeWorkFrameIndex; }
        if (_activeRequests.Count == 0)
            PublishWithoutSimulationInputPage(backend);
        else
            ProcessDispatchGroups(backend);
    }

    internal static GPUPhysicsChainBackendStatus EvaluateBackendCapability(
        string? backendName,
        bool rendererAvailable,
        bool backendContractAvailable)
    {
        string resolvedBackendName = string.IsNullOrWhiteSpace(backendName)
            ? "None"
            : backendName;

        if (!rendererAvailable)
        {
            return new GPUPhysicsChainBackendStatus(
                GPUPhysicsChainBackendState.Unavailable,
                resolvedBackendName,
                CanDispatchGpu: false,
                CpuFallbackUsed: false,
                "Batched GPU physics-chain simulation was requested, but no active renderer is available.");
        }

        if (!backendContractAvailable)
        {
            return new GPUPhysicsChainBackendStatus(
                GPUPhysicsChainBackendState.Unsupported,
                resolvedBackendName,
                CanDispatchGpu: false,
                CpuFallbackUsed: false,
                $"Renderer backend '{resolvedBackendName}' does not expose the required core GPU physics-chain capabilities.");
        }

        return new GPUPhysicsChainBackendStatus(
            GPUPhysicsChainBackendState.Ready,
            resolvedBackendName,
            CanDispatchGpu: true,
            CpuFallbackUsed: false,
            $"Batched GPU physics-chain simulation is available on renderer backend '{resolvedBackendName}'.");
    }

    private IPhysicsChainComputeBackend? ResolveComputeBackend(AbstractRenderer? renderer)
    {
        if (_backendEvaluated && ReferenceEquals(renderer, _evaluatedRenderer) && _computeBackend is not null)
            return _computeBackend;

        bool rendererChanged = _backendEvaluated && !ReferenceEquals(renderer, _evaluatedRenderer);
        if (rendererChanged)
        {
            InvalidateInFlightReadbacksForRendererChange();
            RetireSimulationReceipts();
            RetireInputPages();
            RetireOutputPages();
            ResetGpuBoundsResources();
        }

        bool backendIdentityChanged = !_backendEvaluated || rendererChanged;
        _backendEvaluated = true;
        _evaluatedRenderer = renderer;
        if (backendIdentityChanged)
            BumpEpoch(ref _readbackBackendGeneration);
        _computeBackend = PhysicsChainComputeBackendFactory.TryCreate(renderer, out IPhysicsChainComputeBackend? backend)
            && backend!.Capabilities.SupportsCorePipeline
            ? backend
            : null;

        PublishBackendStatus(EvaluateBackendCapability(
            renderer?.GetType().Name,
            rendererAvailable: renderer is not null,
            backendContractAvailable: _computeBackend is not null));
        return _computeBackend;
    }

    private void InvalidateInFlightReadbacksForRendererChange()
    {
        foreach (PhysicsChainSelectiveReadbackWorldResources bank in _selectiveReadbackWorldResources.Values)
        {
            bank.RejectLogicalTransfers();
            _retiredSelectiveReadbackWorldResources.Add(bank);
        }
        _selectiveReadbackWorldResources.Clear();
        for (int i = 0; i < _inFlight.Count; ++i)
        {
            InFlightDispatch entry = _inFlight[i];
            entry.Fence.Dispose();
            _readbackBufferPool.Push(entry.ReadbackBuffer);
            for (int requestIndex = 0; requestIndex < entry.RequestCount; ++requestIndex)
                entry.Requests[requestIndex].Component.NotifyGpuReadbackUnavailable("renderer-changed");
            ArrayPool<GPUReadbackRequestSnapshot>.Shared.Return(entry.Requests, clearArray: false);
        }

        _inFlight.Clear();
        BumpEpoch(ref _readbackBackendGeneration);
    }

    private static bool TryDispatchDirect(
        IPhysicsChainComputeBackend backend,
        XRRenderProgram program,
        uint groupsX,
        uint groupsY,
        uint groupsZ,
        PhysicsChainComputePassKind passKind,
        IRenderResourceLeaseOwner? authoringLease = null)
    {
        PhysicsChainComputeEnqueueStatus status = backend.TryDispatchDirect(
            program,
            groupsX,
            groupsY,
            groupsZ,
            passKind,
            authoringLease);
        if (status == PhysicsChainComputeEnqueueStatus.Enqueued)
            return true;

        string key = $"GPUPhysicsChainDispatcher.Enqueue.{backend.Name}.{passKind}.{status}";
        if (XREngine.Debug.ShouldLogEvery(key, TimeSpan.FromSeconds(5.0)))
        {
            XREngine.Debug.PhysicsWarning(
                $"[GPUPhysicsChainDispatcher] Backend '{backend.Name}' rejected {passKind} dispatch ({status}). " +
                "GPU work remains pending; no CPU fallback was used.");
        }

        return false;
    }

    private static bool TryDispatchIndirect(
        IPhysicsChainComputeBackend backend,
        XRRenderProgram program,
        XRDataBuffer arguments,
        nint byteOffset,
        PhysicsChainKernelBucket bucket)
    {
        PhysicsChainComputeEnqueueStatus status = backend.TryDispatchIndirect(program, arguments, byteOffset);
        if (status == PhysicsChainComputeEnqueueStatus.Enqueued)
            return true;

        string key = $"GPUPhysicsChainDispatcher.Enqueue.{backend.Name}.Indirect.{bucket}.{status}";
        if (XREngine.Debug.ShouldLogEvery(key, TimeSpan.FromSeconds(5.0)))
        {
            XREngine.Debug.PhysicsWarning(
                $"[GPUPhysicsChainDispatcher] Backend '{backend.Name}' rejected indirect dispatch for bucket {bucket} ({status}). " +
                "GPU work remains pending; no CPU fallback was used.");
        }

        return false;
    }

    private static bool TryCopyBuffer(
        IPhysicsChainComputeBackend backend,
        in PhysicsChainComputeBufferCopy copy,
        string operation)
    {
        PhysicsChainComputeEnqueueStatus status = backend.TryCopyBuffer(copy);
        if (status == PhysicsChainComputeEnqueueStatus.Enqueued)
            return true;

        string key = $"GPUPhysicsChainDispatcher.Enqueue.{backend.Name}.Copy.{operation}.{status}";
        if (XREngine.Debug.ShouldLogEvery(key, TimeSpan.FromSeconds(5.0)))
        {
            XREngine.Debug.PhysicsWarning(
                $"[GPUPhysicsChainDispatcher] Backend '{backend.Name}' rejected {operation} buffer copy ({status}). " +
                "GPU work remains pending; no CPU fallback was used.");
        }

        return false;
    }

    private static bool TryCompletePass(
        IPhysicsChainComputeBackend backend,
        in PhysicsChainComputePass pass)
    {
        PhysicsChainComputeEnqueueStatus status = backend.TryCompletePass(pass);
        if (status == PhysicsChainComputeEnqueueStatus.Enqueued)
            return true;

        string key = $"GPUPhysicsChainDispatcher.Enqueue.{backend.Name}.Barrier.{pass.Kind}.{status}";
        if (XREngine.Debug.ShouldLogEvery(key, TimeSpan.FromSeconds(5.0)))
        {
            XREngine.Debug.PhysicsWarning(
                $"[GPUPhysicsChainDispatcher] Backend '{backend.Name}' rejected the {pass.Kind} completion barrier ({status}). " +
                "GPU work remains pending; no CPU fallback was used.");
        }

        return false;
    }

    private void PublishBackendStatus(GPUPhysicsChainBackendStatus status)
    {
        GPUPhysicsChainBackendStatus previous = Interlocked.Exchange(ref _backendStatus, status);
        if (previous == status
            || status.State is GPUPhysicsChainBackendState.Ready
                or GPUPhysicsChainBackendState.NotEvaluated
                or GPUPhysicsChainBackendState.Disabled)
            return;

        string logKey = $"GPUPhysicsChainDispatcher.Backend.{status.State}.{status.BackendName}";
        if (!XREngine.Debug.ShouldLogEvery(logKey, TimeSpan.FromSeconds(5.0)))
            return;

        XREngine.Debug.PhysicsWarning(
            $"[GPUPhysicsChainDispatcher] {status.Diagnostic} " +
            "The requested GPU work remains pending; no CPU fallback was used.");
    }

    public void ProcessCompletions()
    {
        ProcessGpuDebugRetirements();
        if (!_enabled)
            return;

        ProcessDeferredArenaResources();
        PollSelectiveReadbackTransfersOnce();
        RetireInactiveReadbackWorlds();
        for (int i = _inFlight.Count - 1; i >= 0; --i)
        {
            InFlightDispatch entry = _inFlight[i];
            EGpuFenceStatus fenceStatus = entry.Fence.Poll();
            long fenceAgeFrames = Math.Max(_readbackFrameIndex - entry.SubmittedFrame, 0L);
            if (fenceStatus == EGpuFenceStatus.Pending
                && fenceAgeFrames <= MaximumPendingReadbackFenceAgeFrames)
                continue;
            if (fenceStatus == EGpuFenceStatus.Pending)
                fenceStatus = EGpuFenceStatus.Failed;

            if (fenceStatus == EGpuFenceStatus.Failed)
            {
                ++_readbackFailedFenceCount;
                for (int requestIndex = 0; requestIndex < entry.RequestCount; ++requestIndex)
                    entry.Requests[requestIndex].Component.NotifyGpuReadbackUnavailable("submission-fence-failed");
            }
            else
            {
                long latencyFrames = Math.Max(_readbackFrameIndex - entry.SubmittedFrame, 0L);
                _maximumReadbackLatencyFrames = Math.Max(_maximumReadbackLatencyFrames, latencyFrames);
                GPUParticleData[] readback = ArrayPool<GPUParticleData>.Shared.Rent(Math.Max(entry.ParticleCount, 1));
                try
                {
                    Span<GPUParticleData> particles = readback.AsSpan(0, entry.ParticleCount);
                    if (ReadParticleDataFromBuffer(
                            entry.ReadbackBuffer,
                            particles,
                            entry.Backend,
                            entry.IsBatched))
                    {
                        DistributeReadbackToComponents(entry.Requests.AsSpan(0, entry.RequestCount), readback, entry.ParticleCount);
                        ++_readbackCompletedCount;
                    }
                    else
                    {
                        ++_readbackReadFailureCount;
                        for (int requestIndex = 0; requestIndex < entry.RequestCount; ++requestIndex)
                            entry.Requests[requestIndex].Component.NotifyGpuReadbackUnavailable("mapped-staging-read-failed");
                    }
                }
                finally
                {
                    ArrayPool<GPUParticleData>.Shared.Return(readback, clearArray: false);
                }
            }

            entry.Fence.Dispose();
            _readbackBufferPool.Push(entry.ReadbackBuffer);
            _readbackPoolHighWater = Math.Max(_readbackPoolHighWater, _readbackBufferPool.Count);
            ArrayPool<GPUReadbackRequestSnapshot>.Shared.Return(entry.Requests, clearArray: false);
            _inFlight.RemoveAt(i);
        }
    }

    private void ProcessDeferredArenaResources()
    {
        for (int i = _deferredArenaResources.Count - 1; i >= 0; --i)
        {
            DeferredPhysicsChainArenaResource entry = _deferredArenaResources[i];
            if (entry.RetirementFence is null)
                continue;

            EGpuFenceStatus status = entry.RetirementFence.Poll();
            if (status == EGpuFenceStatus.Pending)
                continue;
            if (status == EGpuFenceStatus.Failed)
            {
                entry.RetirementFence.Dispose();
                XRGpuFence? retryFence = entry.Backend.InsertFence();
                int retryCount = entry.FailedFenceRetryCount + 1;
                _deferredArenaResources[i] = entry with
                {
                    RetirementFence = retryFence,
                    FailedFenceRetryCount = retryCount,
                };

                if (retryFence is null &&
                    XREngine.Debug.ShouldLogEvery(
                        $"GPUPhysicsChainDispatcher.ArenaRetirementFence.Unavailable.{entry.Backend.Name}",
                        TimeSpan.FromSeconds(5.0)))
                {
                    XREngine.Debug.PhysicsWarning(
                        $"[GPUPhysicsChainDispatcher] Arena retirement fence failed and backend '{entry.Backend.Name}' could not enqueue a replacement marker; retaining the superseded resource until reset.");
                }
                else if (retryFence is not null &&
                    XREngine.Debug.ShouldLogEvery(
                        $"GPUPhysicsChainDispatcher.ArenaRetirementFence.Retry.{entry.Backend.Name}",
                        TimeSpan.FromSeconds(5.0)))
                {
                    XREngine.Debug.PhysicsWarning(
                        $"[GPUPhysicsChainDispatcher] Re-enqueued a failed arena retirement marker after an unsubmitted frame. RetryCount={retryCount}.");
                }
                continue;
            }

            entry.RetirementFence.Dispose();
            entry.Resource.Dispose();
            Interlocked.Add(ref _deferredArenaBytes, -entry.ByteLength);
            _deferredArenaResources.RemoveAt(i);
        }
    }

    private void DisposeDeferredArenaResources()
    {
        for (int i = 0; i < _deferredArenaResources.Count; ++i)
        {
            DeferredPhysicsChainArenaResource entry = _deferredArenaResources[i];
            entry.RetirementFence?.Dispose();
            entry.Resource.Dispose();
        }

        _deferredArenaResources.Clear();
        Interlocked.Exchange(ref _deferredArenaBytes, 0L);
    }

    #endregion

    #region Buffer Management

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        _mainPhysicsShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/PhysicsChain.comp", EShaderType.Compute);
        _activeWorkShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/PhysicsChainActiveWork.comp", EShaderType.Compute);
        _selectiveReadbackGatherShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/PhysicsChainReadbackGather.comp", EShaderType.Compute);
        _mainPhysicsProgram = new XRRenderProgram(true, false, _mainPhysicsShader);
        _activeWorkProgram = new XRRenderProgram(true, false, _activeWorkShader);
        EnsureSpecializedKernelInitialized();
        _selectiveReadbackGatherProgram = new XRRenderProgram(true, false, _selectiveReadbackGatherShader);

        _initialized = true;
    }

    private void EnsureGpuBonePaletteProgram()
    {
        if (_gpuBonePaletteProgram is not null)
            return;

        _gpuBonePaletteShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/PhysicsChainBonePalette.comp", EShaderType.Compute);
        _gpuBonePaletteProgram = new XRRenderProgram(true, false, _gpuBonePaletteShader);
    }

    private bool BuildResidentArenaBuffers(IPhysicsChainComputeBackend backend, IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        _allPerTreeParams.Clear();
        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            if (!EnsureRequestArenaSlices(request, out bool allocationChanged))
                return false;

            request.PendingArenaAllocationChange |= allocationChanged;
            request.TreeOffset = _allPerTreeParams.Count;
            for (int treeIndex = 0; treeIndex < request.Trees.Count; ++treeIndex)
            {
                GPUParticleTreeData tree = request.Trees[treeIndex];
                _allPerTreeParams.Add(new GPUPerTreeParams
                {
                    DeltaTime = request.TimeVar,
                    ObjectScale = request.ObjectScale,
                    Weight = request.Weight,
                    FreezeAxis = request.FreezeAxis,
                    Force = request.Force,
                    ColliderCount = request.Colliders.Count,
                    Gravity = request.Gravity,
                    ColliderOffset = request.ColliderOffset,
                    ObjectMove = request.ObjectMove,
                    RestGravity = tree.RestGravity,
                    ParticleOffset = request.ParticleOffset + tree.ParticleOffset,
                    ParticleCount = tree.ParticleCount,
                    LoopCount = request.LoopCount,
                });
            }
        }

        BuildActiveWorkRecords(requests);
        EnsureTransformInputCatalogCapacity();
        TotalParticleCount = _particleArenaHighWater;
        TotalTreeCount = _allPerTreeParams.Count;
        TotalColliderCount = _colliderArenaHighWater;
        if (!EnsureResidentArenaCapacity(backend, _allPerTreeParams.Count)
            || !EnsureActiveWorkCapacity(backend, _instanceMetadata.Count, _treeWorkItems.Count)
            || !EnsureInputPageCapacity(backend, _allPerTreeParams.Count,
                _instanceMetadata.Count, _treeWorkItems.Count,
                _colliderArenaHighWater, _particleArenaHighWater)
            || !EnsureSpecializedKernelResources(backend, requests))
            return false;

        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
            UploadDirtyRequestRanges(requests[requestIndex]);

        if (!UploadInputPageColliders(requests)
            || !UploadInputPageTransforms()
            || !UploadDynamicTreeHeaders(requests)
            || !UploadActiveWorkInputs(requests)
            || !TryCompletePass(backend, InputPublicationPass))
            return false;
        _particleArenaUploadedHighWater = _particleArenaHighWater;
        _colliderArenaUploadedHighWater = _colliderArenaHighWater;
        RefreshMainPassBindings();
        Interlocked.Exchange(ref s_residentParticleBytes, (long)(_particlesBuffer?.Length ?? 0u));
        return true;
    }

    private void BuildActiveWorkRecords(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        _instanceMetadata.Clear();
        _treeWorkItems.Clear();
        _instanceMetadata.EnsureCapacity(requests.Count);
        Array.Clear(_kernelCandidateCounts);
        _treeWorkItems.EnsureCapacity(_allPerTreeParams.Count);

        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            _instanceMetadata.Add(new PhysicsChainGpuInstanceMetadata
            {
                Enabled = request.Enabled,
                Relevant = request.Relevant,
                SleepState = request.SleepState,
                QualityTier = request.QualityTier,
                Cadence = Math.Max(request.Cadence, 1u),
                Phase = request.Phase,
                LoopCount = (uint)Math.Max(request.LoopCount, 0),
                FeatureMask = request.FeatureMask,
            });

            for (int treeIndex = 0; treeIndex < request.Trees.Count; ++treeIndex)
            {
                GPUParticleTreeData tree = request.Trees[treeIndex];
                PhysicsChainKernelBucket bucket = ClassifyKernelBucket(request.ParticleStaticData, tree);
                _treeWorkItems.Add(new PhysicsChainGpuTreeWorkItem
                {
                    InstanceIndex = (uint)requestIndex,
                    TreeParamIndex = (uint)(request.TreeOffset + treeIndex),
                    KernelBucket = (uint)bucket,
                    TopologyDepth = (uint)Math.Max(tree.ParticleCount, 1),
                });
                ++_kernelCandidateCounts[(int)bucket];
            }
        }
    }

    internal static PhysicsChainKernelBucket ClassifyKernelBucket(
        IReadOnlyList<GPUParticleStaticData> particles,
        in GPUParticleTreeData tree)
    {
        if (tree.ParticleCount <= 32)
        {
            int start = tree.ParticleOffset;
            int end = Math.Min(start + tree.ParticleCount, particles.Count);
            bool linear = start >= 0 && end - start == tree.ParticleCount;
            for (int index = start + 1; linear && index < end; ++index)
            {
                int parent = particles[index].ParentIndex;
                linear = parent == index - 1 || parent == index - start - 1;
            }

            if (linear)
                return PhysicsChainKernelBucket.ShortLinear;
        }

        return PhysicsChainKernelBucket.BranchedOrLong;
    }

    private bool EnsureActiveWorkCapacity(IPhysicsChainComputeBackend backend, int instanceCount, int candidateCount)
    {
        if (candidateCount > MaxArenaElementCount / (int)PhysicsChainKernelBucket.Count)
        {
            XREngine.Debug.PhysicsWarning(
                $"[GPUPhysicsChainDispatcher] Active-work candidate capacity exhausted. Candidates={candidateCount}.");
            return false;
        }

        int capacityPerBucket = CalculateActiveListCapacityPerBucket(candidateCount);
        uint previousActiveIdCapacity = _activeTreeIdBuffer?.ElementCount ?? 0u;
        if (!EnsureArenaCapacity(ref _activeTreeIdBuffer, "PhysicsChainActiveTreeIds", capacityPerBucket * (int)PhysicsChainKernelBucket.Count, 0, EBufferUsage.DynamicDraw, backend)
            || !EnsureArenaCapacity(ref _activeWorkCounterBuffer, "PhysicsChainActiveWorkCounters", ActiveWorkCounterElementCount, 0, EBufferUsage.DynamicDraw, backend)
            || !EnsureArenaCapacity(
                ref _indirectDispatchArgumentBuffer,
                "PhysicsChainIndirectDispatchArguments",
                IndirectArgumentElementCount,
                0,
                EBufferUsage.DynamicDraw,
                backend,
                EBufferTarget.DispatchIndirectBuffer))
            return false;

        _activeListCapacityPerBucket = (int)(_activeTreeIdBuffer!.ElementCount / (uint)PhysicsChainKernelBucket.Count);
        if (previousActiveIdCapacity > 0u && _activeTreeIdBuffer.ElementCount > previousActiveIdCapacity)
            ++_activeListGrowthCount;
        return true;
    }

    internal static int CalculateActiveListCapacityPerBucket(int candidateCount)
        => checked((int)XRMath.NextPowerOfTwo((uint)Math.Max(candidateCount, 1)));

    private bool UploadActiveWorkInputs(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        bool fullUpload = !IsActiveWorkLayoutCurrent(requests)
            || _activeWorkResourceGeneration != _arenaResourceGeneration;
        int metadataDirtyStart = fullUpload ? 0 : -1;
        int metadataDirtyEndExclusive = fullUpload ? _instanceMetadata.Count : -1;
        int treeDirtyStart = fullUpload ? 0 : -1;
        int treeDirtyEndExclusive = fullUpload ? _treeWorkItems.Count : -1;

        if (!fullUpload)
        {
            for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
            {
                GPUPhysicsChainRequest request = requests[requestIndex];
                if (request.UploadedSchedulingMetadataVersion != request.SchedulingMetadataVersion)
                {
                    if (metadataDirtyStart < 0)
                        metadataDirtyStart = requestIndex;
                    metadataDirtyEndExclusive = requestIndex + 1;
                }

                bool treeWorkDirty = request.UploadedTreeWorkStaticVersion != request.StaticDataVersion
                    || request.UploadedTreeWorkArenaGeneration != request.ArenaAllocationGeneration
                    || request.UploadedTreeWorkCount != request.Trees.Count;
                if (!treeWorkDirty || request.Trees.Count == 0)
                    continue;

                if (treeDirtyStart < 0)
                    treeDirtyStart = request.TreeOffset;
                treeDirtyEndExclusive = request.TreeOffset + request.Trees.Count;
            }
        }

        if (_instanceMetadataBuffer is not null && metadataDirtyStart >= 0)
        {
            int dirtyCount = metadataDirtyEndExclusive - metadataDirtyStart;
            Span<PhysicsChainGpuInstanceMetadata> dirtyMetadata =
                CollectionsMarshal.AsSpan(_instanceMetadata).Slice(metadataDirtyStart, dirtyCount);
            if (!TryWriteInputPage(_instanceMetadataBuffer, dirtyMetadata, metadataDirtyStart))
                return false;
        }

        if (_treeWorkItemBuffer is not null && treeDirtyStart >= 0 && treeDirtyEndExclusive > treeDirtyStart)
        {
            int dirtyCount = treeDirtyEndExclusive - treeDirtyStart;
            Span<PhysicsChainGpuTreeWorkItem> dirtyTreeWork =
                CollectionsMarshal.AsSpan(_treeWorkItems).Slice(treeDirtyStart, dirtyCount);
            if (!TryWriteInputPage(_treeWorkItemBuffer, dirtyTreeWork, treeDirtyStart))
                return false;
        }

        if (metadataDirtyStart < 0 && treeDirtyStart < 0)
            return true;

        _activeWorkResourceGeneration = _arenaResourceGeneration;
        _activeWorkLayout.Clear();
        _activeWorkLayout.EnsureCapacity(requests.Count);
        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            _activeWorkLayout.Add(new PhysicsChainDynamicHeaderLayoutEntry(request.RequestId, request.Trees.Count));
            request.UploadedSchedulingMetadataVersion = request.SchedulingMetadataVersion;
            request.UploadedTreeWorkStaticVersion = request.StaticDataVersion;
            request.UploadedTreeWorkArenaGeneration = request.ArenaAllocationGeneration;
            request.UploadedTreeWorkCount = request.Trees.Count;
        }
        return true;
    }

    private bool IsActiveWorkLayoutCurrent(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (_activeWorkLayout.Count != requests.Count)
            return false;

        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            PhysicsChainDynamicHeaderLayoutEntry entry = _activeWorkLayout[requestIndex];
            if (entry.RequestId != request.RequestId || entry.TreeCount != request.Trees.Count)
                return false;
        }

        return true;
    }

    private bool EnsureRequestArenaSlices(GPUPhysicsChainRequest request, out bool allocationChanged)
    {
        allocationChanged = false;
        int particleCount = Math.Max(
            Math.Max(request.Particles.Count, request.ParticleStaticData.Count),
            request.Transforms.Count);
        particleCount = Math.Max(particleCount, 1);
        if (request.ParticleOffset < 0 || request.ParticleArenaCapacity < particleCount)
        {
            if (!TryAllocateArenaSlice(ref _particleArenaHighWater, particleCount, request.RequestId, "particle", out int offset, out int capacity))
                return false;

            request.ParticleOffset = offset;
            request.ParticleArenaCapacity = capacity;
            allocationChanged = true;
        }

        int colliderCount = request.Colliders.Count;
        if (colliderCount > 0 && (request.ColliderOffset < 0 || request.ColliderArenaCapacity < colliderCount))
        {
            if (!TryAllocateArenaSlice(ref _colliderArenaHighWater, colliderCount, request.RequestId, "collider", out int offset, out int capacity))
                return false;

            request.ColliderOffset = offset;
            request.ColliderArenaCapacity = capacity;
            allocationChanged = true;
        }
        else if (request.ColliderOffset < 0)
        {
            request.ColliderOffset = 0;
        }

        if (allocationChanged)
            ++request.ArenaAllocationGeneration;
        return true;
    }

    private static bool TryAllocateArenaSlice(
        ref int highWater,
        int requiredCount,
        int requestId,
        string arenaName,
        out int offset,
        out int capacity)
    {
        offset = -1;
        capacity = 0;
        if (requiredCount <= 0 || requiredCount > MaxArenaElementCount)
        {
            XREngine.Debug.PhysicsWarning(
                $"[GPUPhysicsChainDispatcher] Refusing invalid {arenaName} arena allocation. RequestId={requestId}, Required={requiredCount}.");
            return false;
        }

        uint geometricCapacity = XRMath.NextPowerOfTwo((uint)requiredCount);
        if (geometricCapacity > MaxArenaElementCount || highWater > MaxArenaElementCount - (int)geometricCapacity)
        {
            XREngine.Debug.PhysicsWarning(
                $"[GPUPhysicsChainDispatcher] {arenaName} arena capacity exhausted. RequestId={requestId}, HighWater={highWater}, Required={requiredCount}.");
            return false;
        }

        offset = highWater;
        capacity = (int)geometricCapacity;
        highWater += capacity;
        return true;
    }

    private bool EnsureResidentArenaCapacity(IPhysicsChainComputeBackend backend, int requiredTreeHeaderCount)
    {
        // CPU writes only seed or reset ranges. Simulation and arena growth stay on the GPU.
        if (!EnsureArenaCapacity(
                ref _particlesBuffer,
                "PhysicsChainParticleArena",
                _particleArenaHighWater,
                _particleArenaUploadedHighWater,
                EBufferUsage.StaticCopy,
                backend))
            return false;
        if (!EnsureArenaCapacity(
                ref _particleStaticBuffer,
                "PhysicsChainTemplateArena",
                _particleArenaHighWater,
                _particleArenaUploadedHighWater,
                EBufferUsage.StaticDraw,
                backend))
            return false;
        return true;
    }

    private bool EnsureArenaCapacity<T>(
        ref XRDataBuffer<T>? buffer,
        string name,
        int requiredElementCount,
        int preserveElementCount,
        EBufferUsage usage,
        IPhysicsChainComputeBackend backend,
        EBufferTarget target = EBufferTarget.ShaderStorageBuffer)
        where T : unmanaged
    {
        uint required = (uint)Math.Max(requiredElementCount, 1);
        if (buffer is not null && buffer.ElementCount >= required)
            return true;

        uint capacity = XRMath.NextPowerOfTwo(required);
        ulong capacityBytes = (ulong)capacity * (uint)Unsafe.SizeOf<T>();
        if (capacity > MaxArenaElementCount || capacityBytes > uint.MaxValue)
        {
            XREngine.Debug.PhysicsWarning(
                $"[GPUPhysicsChainDispatcher] Refusing {name} growth beyond addressable capacity. RequiredElements={requiredElementCount}, Capacity={capacity}, Bytes={capacityBytes}.");
            return false;
        }

        var replacement = new XRDataBuffer<T>(name, target, capacity)
        {
            DisposeOnPush = false,
            Usage = usage,
        };

        XRDataBuffer<T>? previous = buffer;
        if (previous is not null && preserveElementCount > 0)
        {
            int copyElements = Math.Min(preserveElementCount, (int)previous.ElementCount);
            nuint copyBytes = (nuint)(copyElements * Unsafe.SizeOf<T>());
            var copy = new PhysicsChainComputeBufferCopy(previous, 0, replacement, 0, copyBytes);
            if (!TryCopyBuffer(backend, copy, $"arena growth for {name}"))
            {
                replacement.Dispose();
                XREngine.Debug.PhysicsWarning(
                    $"[GPUPhysicsChainDispatcher] Backend '{backend.Name}' failed to preserve {name} during growth. GPU work remains pending; no fallback was used.");
                return false;
            }

            RecordGpuCopyBytes((long)copyBytes, _currentDispatchGroupIsBatched);
            Interlocked.Add(ref s_arenaGrowthCopyBytes, (long)copyBytes);
            if (!TryCompletePass(backend, ArenaGrowthCompletionPass))
            {
                replacement.Dispose();
                XREngine.Debug.PhysicsWarning(
                    $"[GPUPhysicsChainDispatcher] Backend '{backend.Name}' failed to enqueue the {name} arena-growth dependency. GPU work remains pending.");
                return false;
            }
        }

        buffer = replacement;
        ++_arenaResourceGeneration;
        BumpEpoch(ref _readbackArenaGeneration);
        _mainPassBindingsValid = false;
        if (previous is not null)
        {
            Interlocked.Increment(ref s_arenaGrowthCount);
            DeferArenaResource(previous, backend);
            XREngine.Debug.PhysicsWarning(
                $"[GPUPhysicsChainDispatcher] Grew {name} to {capacity} elements. ResourceGeneration={_arenaResourceGeneration}.");
        }
        else if (VerboseLogging)
        {
            XREngine.Debug.Out(
                $"[GPUPhysicsChainDispatcher] Allocated {name} with {capacity} elements. ResourceGeneration={_arenaResourceGeneration}.");
        }
        return true;
    }

    private void DeferArenaResource(XRDataBuffer resource, IPhysicsChainComputeBackend backend)
    {
        XRGpuFence? retirementFence = backend.InsertFence();
        long byteLength = resource.Length;
        _deferredArenaResources.Add(new DeferredPhysicsChainArenaResource(
            resource,
            backend,
            retirementFence,
            byteLength));
        Interlocked.Add(ref _deferredArenaBytes, byteLength);
        if (retirementFence is null)
        {
            XREngine.Debug.PhysicsWarning(
                $"[GPUPhysicsChainDispatcher] Backend '{backend.Name}' did not provide an arena retirement fence; the superseded resource will remain alive until reset.");
        }
    }

    private void UploadDirtyRequestRanges(GPUPhysicsChainRequest request)
    {
        bool allocationChanged = request.PendingArenaAllocationChange
            || request.UploadedArenaAllocationGeneration != request.ArenaAllocationGeneration;
        GPUPhysicsChainUploadPlan plan = GPUPhysicsChainUploadPlan.Create(
            allocationChanged,
            request.UploadedParticleStateVersion,
            request.ParticleStateVersion,
            request.UploadedStaticDataVersion,
            request.StaticDataVersion,
            request.UploadedTransformDataVersion,
            request.TransformDataSignature,
            request.UploadedColliderDataVersion,
            request.ColliderDataSignature);

        if (plan.UploadParticleState && _particlesBuffer is not null && request.Particles.Count > 0)
        {
            uint bytes = _particlesBuffer.WriteDataRaw(
                request.AcceptedInput.Particles.AsSpan(),
                (uint)request.ParticleOffset);
            PushBufferUpdate(_particlesBuffer, fullPush: false, bytes, request.ParticleOffset * ParticleStateSizeBytes);
            RecordArenaUpload(bytes, isStatic: false);
        }

        if (plan.UploadStaticTemplate && _particleStaticBuffer is not null && request.ParticleStaticData.Count > 0)
        {
            Span<GPUParticleStaticData> adjusted = request.GetAdjustedStaticData();
            for (int i = 0; i < adjusted.Length; ++i)
            {
                adjusted[i] = request.AcceptedInput.ParticleStaticData[i];
                if (adjusted[i].ParentIndex >= 0)
                    adjusted[i].ParentIndex += request.ParticleOffset;
            }

            uint bytes = _particleStaticBuffer.WriteDataRaw(adjusted, (uint)request.ParticleOffset);
            PushBufferUpdate(_particleStaticBuffer, fullPush: false, bytes, request.ParticleOffset * ParticleStaticSizeBytes);
            RecordArenaUpload(bytes, isStatic: true);
        }

        if (plan.UploadTransformInputs && request.Transforms.Count > 0)
        {
            RecordTransformCatalogRange(request.ParticleOffset, request.Transforms.Count);
            Span<PhysicsChainAffineInput> destination =
                CollectionsMarshal.AsSpan(_transformInputCatalog).Slice(
                    request.ParticleOffset, request.Transforms.Count);
            for (int index = 0; index < destination.Length; ++index)
                destination[index] = PhysicsChainAffineInput.FromMatrix(
                    request.AcceptedInput.Transforms[index]);
        }

        ObserveResidentSpatialInputs(request, plan.UploadParticleState, plan.UploadTransformInputs);
        request.UploadedArenaAllocationGeneration = request.ArenaAllocationGeneration;
        request.UploadedParticleStateVersion = request.ParticleStateVersion;
        request.UploadedStaticDataVersion = request.StaticDataVersion;
        request.UploadedTransformDataVersion = request.TransformDataSignature;
        request.UploadedColliderDataVersion = request.ColliderDataSignature;
        request.PendingArenaAllocationChange = false;
    }

    private bool UploadDynamicTreeHeaders(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (_perTreeParamsBuffer is null || _allPerTreeParams.Count == 0)
            return true;

        bool fullUpload = !IsDynamicHeaderLayoutCurrent(requests)
            || _dynamicHeaderResourceGeneration != _arenaResourceGeneration;
        int dirtyStart = fullUpload ? 0 : -1;
        int dirtyEndExclusive = fullUpload ? _allPerTreeParams.Count : -1;
        if (!fullUpload)
        {
            for (int i = 0; i < requests.Count; ++i)
            {
                GPUPhysicsChainRequest request = requests[i];
                if (!IsDynamicHeaderDirty(request) || request.Trees.Count == 0)
                    continue;

                if (dirtyStart < 0 || request.TreeOffset < dirtyStart)
                    dirtyStart = request.TreeOffset;
                dirtyEndExclusive = Math.Max(dirtyEndExclusive, request.TreeOffset + request.Trees.Count);
            }
        }

        if (dirtyStart < 0)
            return true;

        int dirtyCount = dirtyEndExclusive - dirtyStart;
        Span<GPUPerTreeParams> dirtyHeaders = CollectionsMarshal.AsSpan(_allPerTreeParams).Slice(dirtyStart, dirtyCount);
        if (!TryWriteInputPage(_perTreeParamsBuffer, dirtyHeaders, dirtyStart))
            return false;
        _dynamicHeaderResourceGeneration = _arenaResourceGeneration;

        _dynamicHeaderLayout.Clear();
        _dynamicHeaderLayout.EnsureCapacity(requests.Count);
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            _dynamicHeaderLayout.Add(new PhysicsChainDynamicHeaderLayoutEntry(request.RequestId, request.Trees.Count));
            MarkDynamicHeaderUploaded(request);
        }
        return true;
    }

    private bool IsDynamicHeaderLayoutCurrent(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (_dynamicHeaderLayout.Count != requests.Count)
            return false;

        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            PhysicsChainDynamicHeaderLayoutEntry entry = _dynamicHeaderLayout[i];
            if (entry.RequestId != request.RequestId || entry.TreeCount != request.Trees.Count)
                return false;
        }

        return true;
    }

    private static bool IsDynamicHeaderDirty(GPUPhysicsChainRequest request)
        => request.UploadedDynamicHeaderVersion != request.DynamicHeaderVersion
            || request.UploadedHeaderTreeVersion != request.TreeDataVersion
            || request.UploadedHeaderArenaGeneration != request.ArenaAllocationGeneration
            || request.UploadedHeaderColliderCount != request.Colliders.Count;

    private static void MarkDynamicHeaderUploaded(GPUPhysicsChainRequest request)
    {
        request.UploadedDynamicHeaderVersion = request.DynamicHeaderVersion;
        request.UploadedHeaderTreeVersion = request.TreeDataVersion;
        request.UploadedHeaderArenaGeneration = request.ArenaAllocationGeneration;
        request.UploadedHeaderColliderCount = request.Colliders.Count;
    }

    private void RecordArenaUpload(uint bytes, bool isStatic)
    {
        if (bytes == 0u)
            return;

        RecordCpuUploadBytes(bytes, _currentDispatchGroupIsBatched);
        if (isStatic)
            Interlocked.Add(ref s_arenaStaticUploadBytes, bytes);
        else
            Interlocked.Add(ref s_arenaDynamicUploadBytes, bytes);
    }

    private void RefreshMainPassBindings()
    {
        if (_mainPassBindingsValid && _mainPassBindings.IsCurrent(_arenaResourceGeneration))
            return;
        if (_particlesBuffer is null
            || _particleStaticBuffer is null
            || _transformMatricesBuffer is null
            || _collidersBuffer is null
            || _perTreeParamsBuffer is null)
            return;

        _mainPassBindings = new PhysicsChainComputeBindings(
            _particlesBuffer,
            _particleStaticBuffer,
            _transformMatricesBuffer,
            _collidersBuffer,
            _perTreeParamsBuffer,
            _arenaResourceGeneration);
        _mainPassBindingsValid = true;
    }

    private long GetCurrentArenaCapacityBytes()
        => (long)(_particlesBuffer?.Length ?? 0u)
            + (_particleStaticBuffer?.Length ?? 0u)
            + (_transformMatricesBuffer?.Length ?? 0u)
            + (_collidersBuffer?.Length ?? 0u)
            + (_perTreeParamsBuffer?.Length ?? 0u)
            + (_instanceMetadataBuffer?.Length ?? 0u)
            + (_treeWorkItemBuffer?.Length ?? 0u)
            + (_activeTreeIdBuffer?.Length ?? 0u)
            + (_activeWorkCounterBuffer?.Length ?? 0u)
            + (_indirectDispatchArgumentBuffer?.Length ?? 0u)
            + (_depthRangeBuffer?.Length ?? 0u)
            + (_depthParticleIdBuffer?.Length ?? 0u);

    private long GetCurrentArenaLiveBytes()
    {
        long liveBytes = (long)_allPerTreeParams.Count * PerTreeParamsSizeBytes
            + (long)_instanceMetadata.Count * Unsafe.SizeOf<PhysicsChainGpuInstanceMetadata>()
            + (long)_treeWorkItems.Count * Unsafe.SizeOf<PhysicsChainGpuTreeWorkItem>()
            + (long)_treeWorkItems.Count * sizeof(uint)
            + ActiveWorkCounterElementCount * sizeof(uint)
            + IndirectArgumentElementCount * sizeof(uint)
            + (long)_depthRanges.Count * Unsafe.SizeOf<PhysicsChainGpuDepthRange>()
            + (long)_depthParticleIds.Count * sizeof(uint);
        lock (_registeredComponentsSync)
        {
            for (int i = 0; i < _registeredComponentSnapshot.Count; ++i)
            {
                GPUPhysicsChainRequest request = _registeredComponentSnapshot[i];
                if (request.ParticleOffset >= 0)
                {
                    liveBytes += (long)request.Particles.Count * ParticleStateSizeBytes;
                    liveBytes += (long)request.ParticleStaticData.Count * ParticleStaticSizeBytes;
                    liveBytes += (long)request.Transforms.Count * AffineInputSizeBytes;
                }
                if (request.ColliderOffset >= 0)
                    liveBytes += (long)request.Colliders.Count * ColliderSizeBytes;
            }
        }

        return liveBytes;
    }    private static bool ValidateGpuCopyRange(
        XRDataBuffer source,
        nint readOffset,
        XRDataBuffer destination,
        nint writeOffset,
        nuint byteCount,
        string operation,
        int requestId)
    {
        if (IsBufferRangeValid(source, readOffset, byteCount) &&
            IsBufferRangeValid(destination, writeOffset, byteCount))
            return true;

        XREngine.Debug.LogWarning(
            $"[GPUPhysicsChainDispatcher] Skipping invalid GPU copy range. Operation={operation}, RequestId={requestId}, " +
            $"ReadOffset={readOffset}, WriteOffset={writeOffset}, ByteCount={byteCount}, SourceLength={source.Length}, DestinationLength={destination.Length}");
        return false;
    }

    private static bool IsBufferRangeValid(XRDataBuffer buffer, nint offset, nuint byteCount)
    {
        if (offset < 0)
            return false;

        ulong start = (ulong)offset;
        ulong count = (ulong)byteCount;
        ulong end = start + count;
        return end >= start && end <= buffer.Length;
    }

    private static bool EnsureBufferCapacity<T>(ref XRDataBuffer<T>? buffer, string name, uint elementCount) where T : unmanaged
    {
        if (elementCount == 0)
            elementCount = 1;

        if (buffer is null)
        {
            buffer = new XRDataBuffer<T>(name, EBufferTarget.ShaderStorageBuffer, XRMath.NextPowerOfTwo(elementCount));
            buffer.DisposeOnPush = false;
            buffer.Usage = EBufferUsage.DynamicDraw;
            return true;
        }

        if (buffer.ElementCount < elementCount)
        {
            buffer.Resize(XRMath.NextPowerOfTwo(elementCount), copyData: false);
            return true;
        }

        return false;
    }

    private static void PushBufferUpdate(XRDataBuffer? buffer, bool fullPush, uint byteLength, int byteOffset = 0)
    {
        if (buffer is null)
            return;

        if (fullPush)
            buffer.CommitDirtyBytes(0u, buffer.Length);
        else if (byteLength > 0)
            buffer.CommitDirtyBytes(checked((uint)byteOffset), byteLength);
    }

    private XRDataBuffer<GPUParticleData> AcquireReadbackBuffer(uint particleCount)
    {
        uint elementCount = Math.Max(particleCount, 1u);
        while (_readbackBufferPool.Count > 0)
        {
            XRDataBuffer<GPUParticleData> buffer = _readbackBufferPool.Pop();
            if (buffer.ElementCount >= elementCount)
                return buffer;

            buffer.Dispose();
        }

        var created = new XRDataBuffer<GPUParticleData>("PhysicsChainReadback", EBufferTarget.ShaderStorageBuffer, elementCount)
        {
            DefaultMemoryPolicy = XRBufferMemoryPolicy.GpuToCpuReadback,
            DisposeOnPush = false,
            Usage = EBufferUsage.StreamRead,
        };
        // Materialize backend storage so the ordered copy has a valid destination.
        created.SetDataRaw(new GPUParticleData[elementCount]);
        created.PushData();
        return created;
    }

    private bool PublishBatchedGpuDrivenBoneMatrices(
        IPhysicsChainComputeBackend backend, IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        using var profilerState = RuntimeEngine.Profiler.Start("GPUPhysicsChainDispatcher.PublishBatchedGpuDrivenBoneMatrices");

        if (!RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader)
        {
            ClearBatchedGpuDrivenBonePaletteSources(requests);
            return false;
        }

        if (_particlesBuffer is null || _transformMatricesBuffer is null || requests.Count == 0)
            return false;

        CollectBatchedGpuDrivenBonePaletteBindings(requests);
        if (_gpuDrivenPaletteBindings.Count == 0)
            return false;

        EnsureGpuBonePaletteProgram();
        if (_gpuBonePaletteProgram is null)
        {
            ClearBatchedGpuDrivenBonePaletteSources(requests);
            return false;
        }

        // Gate on backend program link readiness (NVIDIA parallel-link hazard).
        if (!EnsureProgramLinked(_gpuBonePaletteProgram))
        {
            ClearBatchedGpuDrivenBonePaletteSources(requests);
            return false;
        }

        int signature = ComputeGpuDrivenBonePaletteSignature(_gpuDrivenPaletteBindings);
        PhysicsChainOutputPage? outputPage = _writingOutputPageIndex >= 0
            ? _outputPages[_writingOutputPageIndex] : null;
        if (outputPage is not null)
        {
            _gpuDrivenSkinPaletteBuffer = outputPage.CurrentPalette;
            _gpuDrivenPreviousSkinPaletteBuffer = outputPage.PreviousPalette;
        }
        else
        {
            _gpuDrivenSkinPaletteBuffer = _unpagedSkinPaletteBuffer;
            _gpuDrivenPreviousSkinPaletteBuffer = _unpagedPreviousSkinPaletteBuffer;
        }
        bool needsRebuild = signature != _gpuDrivenBonePaletteSignature
            || outputPage is not null && outputPage.PaletteSignature != signature
            || _gpuDrivenBonePaletteMappingsBuffer is null
            || _gpuDrivenSkinPaletteBuffer is null
            || _gpuDrivenPreviousSkinPaletteBuffer is null
            || _gpuDrivenBoneInvBindMatricesBuffer is null;

        if (needsRebuild)
            RebuildBatchedGpuDrivenBonePaletteBuffers(signature);
        else if (outputPage is null)
            (_gpuDrivenSkinPaletteBuffer, _gpuDrivenPreviousSkinPaletteBuffer) =
                (_gpuDrivenPreviousSkinPaletteBuffer, _gpuDrivenSkinPaletteBuffer);

        if (outputPage is not null)
        {
            outputPage.CurrentPalette = _gpuDrivenSkinPaletteBuffer;
            outputPage.PreviousPalette = _gpuDrivenPreviousSkinPaletteBuffer;
            outputPage.PaletteSignature = signature;
            if (!backend.EnsureGpuBufferReady(_gpuDrivenPreviousSkinPaletteBuffer!))
                return false;
        }
        else
        {
            _unpagedSkinPaletteBuffer = _gpuDrivenSkinPaletteBuffer;
            _unpagedPreviousSkinPaletteBuffer = _gpuDrivenPreviousSkinPaletteBuffer;
        }

        if (_gpuDrivenBoneMappings.Count == 0
            || _gpuDrivenBonePaletteMappingsBuffer is null
            || _gpuDrivenSkinPaletteBuffer is null
            || _gpuDrivenPreviousSkinPaletteBuffer is null
            || _gpuDrivenBoneInvBindMatricesBuffer is null)
            return false;
        if (!SeedPartialGpuDrivenBonePalettes(backend))
        {
            ClearBatchedGpuDrivenBonePaletteSources(requests);
            return false;
        }

        _gpuBonePaletteProgram.Uniform("particleBaseOffset", 0);
        _gpuBonePaletteProgram.Uniform("mappingCount", _gpuDrivenBoneMappings.Count);
        _gpuBonePaletteProgram.BindBuffer(_particlesBuffer, 0);
        _gpuBonePaletteProgram.BindBuffer(_transformMatricesBuffer, 1);
        _gpuBonePaletteProgram.BindBuffer(_gpuDrivenBonePaletteMappingsBuffer, 2);
        _gpuBonePaletteProgram.BindBuffer(_gpuDrivenSkinPaletteBuffer, 3);
        _gpuBonePaletteProgram.BindBuffer(_gpuDrivenBoneInvBindMatricesBuffer, 4);

        uint groupsX = (uint)(_gpuDrivenBoneMappings.Count + 63) / 64u;
        if (!TryDispatchDirect(
                backend,
                _gpuBonePaletteProgram,
                Math.Max(groupsX, 1u),
                1u,
                1u,
                PhysicsChainComputePassKind.BonePalettePublication))
        {
            ClearBatchedGpuDrivenBonePaletteSources(requests);
            return false;
        }
        return true;
    }

    private void CollectBatchedGpuDrivenBonePaletteBindings(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        _gpuDrivenPaletteBindings.Clear();
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            if (!request.Component.HasGpuDrivenRenderers)
                continue;

            request.Component.AppendBatchedGpuDrivenBonePaletteBindings(request.ParticleOffset, _gpuDrivenPaletteBindings);
        }
    }

    private static int ComputeGpuDrivenBonePaletteSignature(List<GpuDrivenRendererPaletteBinding> bindings)
    {
        var hash = new HashCode();
        hash.Add(bindings.Count);

        for (int i = 0; i < bindings.Count; ++i)
        {
            GpuDrivenRendererPaletteBinding binding = bindings[i];
            hash.Add(RuntimeHelpers.GetHashCode(binding.Renderer));
            hash.Add(binding.ParticleBaseOffset);
            hash.Add(binding.BoneMatrixElementCount);
            hash.Add(binding.Mappings.Length);
            hash.Add(binding.DrivesCompleteBonePalette);
            hash.Add(binding.BindingGeneration);
            hash.Add(binding.ParticleStateVersion);
            hash.Add(binding.RendererBoneBufferGeneration);
        }

        return hash.ToHashCode();
    }

    private void RebuildBatchedGpuDrivenBonePaletteBuffers(int signature)
    {
        _gpuDrivenBoneMappings.Clear();
        _gpuDrivenSkinPalette.Clear();
        _gpuDrivenBoneInvBindMatrices.Clear();

        PrepareStableGpuDrivenPaletteLayout();
        int atlasElementCount = checked((int)_gpuDrivenPaletteSliceAllocator.HighWater);
        EnsureListCount(_gpuDrivenSkinPalette, atlasElementCount, SkinPaletteMatrix.Identity);
        EnsureListCount(_gpuDrivenBoneInvBindMatrices, atlasElementCount, Matrix4x4.Identity);
        _gpuDrivenPaletteSlicesSeeded.Clear();

        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            uint baseElement = _gpuDrivenPaletteSliceBases[bindingIndex];
            uint elementCount = Math.Max(binding.BoneMatrixElementCount, 1u);

            // Compatible complete palettes share one slice and one mapping generation.
            if (!_gpuDrivenPaletteSlicesSeeded.Add(baseElement))
                continue;

            XRMeshRenderer.BoneResourceSnapshot rendererResources =
                binding.Renderer.CaptureBoneResources();
            CopySkinPaletteRange(rendererResources.SkinPalette, _gpuDrivenSkinPalette, baseElement, elementCount);
            CopyMatrixRange(rendererResources.InverseBindMatrices, _gpuDrivenBoneInvBindMatrices, baseElement, elementCount);

            for (int mappingIndex = 0; mappingIndex < binding.Mappings.Length; ++mappingIndex)
            {
                GPUDrivenBoneMappingData mapping = binding.Mappings[mappingIndex];
                mapping.ParticleIndex += binding.ParticleBaseOffset;
                if (mapping.ChildParticleIndex >= 0)
                    mapping.ChildParticleIndex += binding.ParticleBaseOffset;
                mapping.BoneMatrixIndex += (int)baseElement;
                _gpuDrivenBoneMappings.Add(mapping);
            }
        }

        bool mappingsResized = EnsureBufferCapacity(
            ref _gpuDrivenBonePaletteMappingsBuffer,
            "PhysicsChainGlobalBonePaletteMappings",
            (uint)Math.Max(_gpuDrivenBoneMappings.Count, 1));
        bool skinPaletteResized = EnsureBufferCapacity(
            ref _gpuDrivenSkinPaletteBuffer,
            "PhysicsChainGlobalSkinPalette",
            (uint)Math.Max(_gpuDrivenSkinPalette.Count, 1));
        bool previousSkinPaletteResized = EnsureBufferCapacity(
            ref _gpuDrivenPreviousSkinPaletteBuffer,
            "PhysicsChainGlobalPreviousSkinPalette",
            (uint)Math.Max(_gpuDrivenSkinPalette.Count, 1));
        bool invBindMatricesResized = EnsureBufferCapacity(
            ref _gpuDrivenBoneInvBindMatricesBuffer,
            "PhysicsChainGlobalBoneInvBindMatrices",
            (uint)Math.Max(_gpuDrivenBoneInvBindMatrices.Count, 1));

        uint mappingBytes = _gpuDrivenBonePaletteMappingsBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuDrivenBoneMappings)) ?? 0u;
        PushBufferUpdate(_gpuDrivenBonePaletteMappingsBuffer, mappingsResized, mappingBytes);

        uint skinPaletteBytes = _gpuDrivenSkinPaletteBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuDrivenSkinPalette)) ?? 0u;
        PushBufferUpdate(_gpuDrivenSkinPaletteBuffer, skinPaletteResized, skinPaletteBytes);

        uint previousSkinPaletteBytes = _gpuDrivenPreviousSkinPaletteBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuDrivenSkinPalette)) ?? 0u;
        PushBufferUpdate(_gpuDrivenPreviousSkinPaletteBuffer, previousSkinPaletteResized, previousSkinPaletteBytes);

        uint invBindBytes = _gpuDrivenBoneInvBindMatricesBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuDrivenBoneInvBindMatrices)) ?? 0u;
        PushBufferUpdate(_gpuDrivenBoneInvBindMatricesBuffer, invBindMatricesResized, invBindBytes);

        _gpuDrivenBonePaletteSignature = signature;
    }

    private void PublishBatchedGpuDrivenBonePaletteSources()
    {
        if (_gpuDrivenSkinPaletteBuffer is null || _gpuDrivenPreviousSkinPaletteBuffer is null)
            return;

        lock (_registrationTransitionSync)
        {
            for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
            {
                GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
                if (!_outputPublicationRequestByComponent.TryGetValue(binding.Component, out GPUPhysicsChainRequest? captured)
                    || !_registeredComponents.TryGetValue(binding.Component, out GPUPhysicsChainRequest? current)
                    || !ReferenceEquals(captured, current))
                    continue;
                uint elementCount = Math.Max(binding.BoneMatrixElementCount, 1u);
                if (!binding.Renderer.SetGpuDrivenSkinPaletteSource(
                    binding.Component,
                    _gpuDrivenSkinPaletteBuffer!,
                    _gpuDrivenPreviousSkinPaletteBuffer,
                    _gpuDrivenPaletteSliceBases[bindingIndex],
                    elementCount,
                    binding.RendererBoneBufferGeneration,
                    binding.DrivesCompleteBonePalette,
                    _publishedOutputPageIndex < 0
                        || !_outputPages[_publishedOutputPageIndex].PaletteHistoryResetRenderers.Contains(binding.Renderer)))
                    XREngine.Debug.LogWarning("A physics-chain palette binding used an old renderer bone set.");
            }
        }
    }

    private static unsafe void CopyMatrixRange(XRDataBuffer? source, List<Matrix4x4> destination, uint baseElement, uint elementCount)
    {
        int start = checked((int)baseElement);

        if (source?.ClientSideSource is null || source.Address == VoidPtr.Zero)
            return;

        uint copyCount = Math.Min(elementCount, source.ElementCount);
        if (copyCount == 0u)
            return;

        Span<Matrix4x4> destinationSpan = CollectionsMarshal.AsSpan(destination).Slice(start, (int)copyCount);
        fixed (Matrix4x4* destinationPtr = destinationSpan)
            Memory.Move(destinationPtr, source.Address, copyCount * (uint)Unsafe.SizeOf<Matrix4x4>());
    }

    private static unsafe void CopySkinPaletteRange(XRDataBuffer? source, List<SkinPaletteMatrix> destination, uint baseElement, uint elementCount)
    {
        int start = checked((int)baseElement);

        if (source?.ClientSideSource is null || source.Address == VoidPtr.Zero)
            return;

        uint copyCount = Math.Min(elementCount, source.ElementCount);
        if (copyCount == 0u)
            return;

        Span<SkinPaletteMatrix> destinationSpan = CollectionsMarshal.AsSpan(destination).Slice(start, (int)copyCount);
        fixed (SkinPaletteMatrix* destinationPtr = destinationSpan)
            Memory.Move(destinationPtr, source.Address, copyCount * (uint)Unsafe.SizeOf<SkinPaletteMatrix>());
    }

    private static void EnsureListCount<T>(List<T> list, int count, T initialValue)
    {
        while (list.Count < count)
            list.Add(initialValue);
    }

    private static void ClearBatchedGpuDrivenBonePaletteSources(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        for (int i = 0; i < requests.Count; ++i)
            requests[i].Component.ClearBatchedGpuDrivenBonePaletteSources();
    }

    #endregion

    #region Dispatch

    private bool ProcessDispatchGroups(IPhysicsChainComputeBackend backend)
    {
        if (!TryBeginSimulationReceipt(out PhysicsChainSimulationReceipt receipt))
            return RecordDispatchFailure("SimulationReceiptBusyOrUnsafe");
        _currentDispatchGroupIsBatched = ContainsBatchedRequest(_activeRequests);
        try
        {
            if (!PrepareFrameArenaCapacity(backend, _activeRequests))
                return RecordDispatchFailure("PrepareFrameArenaCapacity");

            DebugReadbackGpuDrivenBonePaletteIfRequested(backend);

            for (int groupStart = 0; groupStart < _activeRequests.Count;)
            {
                GPUDispatchGroupKey key = GPUDispatchGroupKey.From(_activeRequests[groupStart]);
                _currentDispatchGroupIsBatched = key.DispatchIsolationKey == 0;
                _dispatchGroup.Clear();

                int groupEnd = groupStart;
                while (groupEnd < _activeRequests.Count && CompareKeys(GPUDispatchGroupKey.From(_activeRequests[groupEnd]), key) == 0)
                {
                    _dispatchGroup.Add(_activeRequests[groupEnd]);
                    ++groupEnd;
                }

                if (_dispatchGroup.Count == 0)
                {
                    groupStart = groupEnd;
                    continue;
                }

                if (!TryAcquireInputPage(backend))
                    return RecordDispatchFailure("InputPageBusyOrUnsafe");
                try
                {
                    if (!backend.BeginBatch())
                        return RecordDispatchFailure("BeginTransactionalBatch");

                    bool batchCommitted = false;
                    try
                    {
                        BeginTransformCatalogGroup();
                        _currentInputPage!.HasQueuedWork = true;
                        if (!ProcessDispatchGroup(backend, out string failureStage))
                            return RecordDispatchFailure(failureStage);

                        backend.CommitBatch();
                        batchCommitted = true;
                        CommitTransformCatalogGroup();
                        for (int requestIndex = 0; requestIndex < _dispatchGroup.Count; ++requestIndex)
                        {
                            CompleteResidentSpatialSolveAttempt(_dispatchGroup[requestIndex]);
                            _dispatchGroup[requestIndex].MarkSimulationInputConsumed();
                            RecordSimulationReceipt(receipt, _dispatchGroup[requestIndex]);
                        }
                        _paletteOutputRefreshPending = true;
                        Interlocked.Increment(ref s_dispatchGroupCount);
                    }
                    finally
                    {
                        if (!batchCommitted)
                        {
                            try
                            {
                                backend.RollbackBatch();
                            }
                            finally
                            {
                                RollbackTransformCatalogGroup();
                                for (int requestIndex = 0; requestIndex < _dispatchGroup.Count; ++requestIndex)
                                    _dispatchGroup[requestIndex].InvalidateUploadedInputs();
                                _dynamicHeaderResourceGeneration = -1;
                                _activeWorkResourceGeneration = -1;
                            }
                        }
                    }
                }
                finally
                {
                    FinishInputGroup();
                }

                groupStart = groupEnd;
            }

            if (!PublishSelectiveReadbacksAfterSimulation(backend))
                return false;
            return PublishRegisteredOutputs(backend);
        }
        finally
        {
            try
            {
                SealInputPages(backend);
            }
            finally
            {
                SealSimulationReceipt(backend, receipt);
                _currentDispatchGroupIsBatched = true;
            }
        }
    }

    private bool PublishRegisteredOutputs(IPhysicsChainComputeBackend backend)
    {
        _outputPublicationRequests.Clear();
        _outputPublicationRequestByComponent.Clear();
        lock (_registeredComponentsSync)
        {
            for (int requestIndex = 0; requestIndex < _registeredComponentSnapshot.Count; ++requestIndex)
            {
                GPUPhysicsChainRequest request = _registeredComponentSnapshot[requestIndex];
                if (request.ParticleOffset >= 0 && request.Particles.Count > 0
                    && request.Component.HasGpuDrivenRenderers)
                {
                    _outputPublicationRequests.Add(request);
                    _outputPublicationRequestByComponent.Add(request.Component, request);
                }
            }
        }
        CollectBatchedGpuDrivenBonePaletteBindings(_outputPublicationRequests);
        if (_gpuDrivenPaletteBindings.Count == 0)
        {
            CompleteActiveRequestUpdates();
            _paletteOutputRefreshPending = false;
            return true;
        }

        bool hasCompleteRenderer = false;
        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
            hasCompleteRenderer |= IsCertifiedCompleteBoundsBinding(_gpuDrivenPaletteBindings[bindingIndex]);

        if (hasCompleteRenderer && !TryBeginOutputPage(backend))
            return RecordDispatchFailure("OutputPageBusyOrUnsafe");

        if (!backend.BeginBatch())
        {
            AbandonOutputPage(backend);
            return RecordDispatchFailure("BeginOutputPublicationBatch");
        }

        bool outputCommitted = false;
        try
        {
            if (_inputPublicationBarrierPending &&
                !TryCompletePass(backend, InputPublicationPass))
                return RecordDispatchFailure("InputPageTransformBarrier");
            _inputPublicationBarrierPending = false;
            if (hasCompleteRenderer)
                _outputPages[_writingOutputPageIndex].HasQueuedGpuWork = true;
            if (!PublishBatchedGpuDrivenBoneMatrices(backend, _outputPublicationRequests))
                return RecordDispatchFailure("BatchedPalettePublication");
            if (!TryCompletePass(backend, BonePaletteCompletionPass))
                return RecordDispatchFailure("BonePaletteCompletionBarrier");

            if (hasCompleteRenderer && !CopyPreviousOutputPalette(backend,
                _outputPages[_writingOutputPageIndex]))
                return RecordDispatchFailure("PreviousPalettePublication");

            if (hasCompleteRenderer && !PublishGpuDrivenBounds(backend, _outputPublicationRequests))
                return RecordDispatchFailure(_gpuBoundsFailureStage);

            backend.CommitBatch();
            outputCommitted = true;
            if (hasCompleteRenderer && !CommitOutputPage(backend))
            {
                AbandonOutputPage(backend);
                return RecordDispatchFailure("OutputPageFence");
            }
            if (hasCompleteRenderer)
                FinalizeGpuBoundsSceneOwnership();
            PublishBatchedGpuDrivenBonePaletteSources();
        }
        finally
        {
            if (!outputCommitted)
            {
                backend.RollbackBatch();
                AbandonOutputPage(backend);
            }
        }

        CompleteActiveRequestUpdates();
        _paletteOutputRefreshPending = false;
        return true;
    }

    private void CompleteActiveRequestUpdates()
    {
        lock (_registeredComponentsSync)
        {
            for (int requestIndex = 0; requestIndex < _registeredComponentSnapshot.Count; ++requestIndex)
                _registeredComponentSnapshot[requestIndex].AcknowledgeAcceptedInput();
        }
    }

    private static bool IsCertifiedCompleteBoundsBinding(in GpuDrivenRendererPaletteBinding binding)
        => binding.DrivesCompleteBonePalette
            && RuntimeEngine.Rendering.Settings.AllowSkinning
            && RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader
            && binding.RendererBoneBufferGeneration > 0
            && binding.Renderer.BoneBufferGeneration == binding.RendererBoneBufferGeneration;

    private bool ProcessDispatchGroup(
        IPhysicsChainComputeBackend backend,
        out string failureStage)
    {
        if (!BuildResidentArenaBuffers(backend, _dispatchGroup))
        {
            failureStage = "BuildResidentArenaBuffers";
            return false;
        }

        if (TotalParticleCount > 0 && TotalTreeCount > 0)
        {
            for (int requestIndex = 0; requestIndex < _dispatchGroup.Count; ++requestIndex)
                ObserveResidentSpatialSolveAttempt(_dispatchGroup[requestIndex]);
            // Active IDs and command sizes are GPU-authored. CPU command topology
            // remains reset -> compact -> finalize -> fixed indirect bucket calls.
            if (!DispatchActiveWorkGeneration(backend)
                || !DispatchMainPhysics(backend, applyObjectMove: true))
            {
                failureStage = "SimulationDispatch";
                return false;
            }

            Interlocked.Increment(ref s_dispatchIterationCount);
            if (!TryCompletePass(backend, SimulationCompletionPass))
            {
                failureStage = "SimulationCompletionBarrier";
                return false;
            }
            ++_kernelSimulationBarrierCount;
        }

        if (VerboseLogging)
        {
            XREngine.Debug.Out(
                $"[GPUPhysicsChainDispatcher] ProcessDispatchGroups: Publishing bone matrices for {_dispatchGroup.Count} components. " +
                $"TotalParticles={TotalParticleCount}, ParticlesBufferHash={_particlesBuffer?.GetHashCode():X}, " +
                $"TransformsBufferHash={_transformMatricesBuffer?.GetHashCode():X}");
        }

        if (!QueueAsyncReadback(backend, _dispatchGroup))
        {
            failureStage = "AsyncReadbackEnqueue";
            return false;
        }

        failureStage = string.Empty;
        return true;
    }

    /// <summary>
    /// Rewinds only the resident-arena suballocation layout after the registry crosses an
    /// empty boundary. Physical buffers remain allocated and are reused; the next request
    /// fully republishes its data into slices allocated from offset zero.
    /// </summary>
    internal void ApplyPendingArenaLayoutReset()
    {
        if (Interlocked.Exchange(ref _arenaLayoutResetRequested, 0) == 0)
            return;

        _particleArenaHighWater = 0;
        _particleArenaUploadedHighWater = 0;
        _colliderArenaHighWater = 0;
        _colliderArenaUploadedHighWater = 0;
        TotalParticleCount = 0;
        TotalTreeCount = 0;
        TotalColliderCount = 0;

        _allPerTreeParams.Clear();
        _transformInputCatalog.Clear();
        _instanceMetadata.Clear();
        _treeWorkItems.Clear();
        _dynamicHeaderLayout.Clear();
        _dynamicHeaderResourceGeneration = -1;
        _activeWorkLayout.Clear();
        _activeWorkResourceGeneration = -1;
        _gpuDrivenPaletteBindings.Clear();
        _gpuDrivenBoneMappings.Clear();
        _gpuDrivenSkinPalette.Clear();
        _gpuDrivenBoneInvBindMatrices.Clear();
        ResetGpuBoundsResources();
        ResetStableGpuDrivenPaletteLayout();
        _gpuDrivenBonePaletteSignature = int.MinValue;

        BumpEpoch(ref _readbackArenaGeneration);
        BumpEpoch(ref _readbackLayoutGeneration);
        if (VerboseLogging)
        {
            XREngine.Debug.Out(
                "[GPUPhysicsChainDispatcher] Rewound resident arena suballocations after the registry crossed an empty lifecycle boundary.");
        }
    }

    private bool PrepareFrameArenaCapacity(
        IPhysicsChainComputeBackend backend,
        IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        int requiredTreeHeaderCount = 0;
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            if (!EnsureRequestArenaSlices(request, out bool allocationChanged))
                return false;

            request.PendingArenaAllocationChange |= allocationChanged;
            if (allocationChanged)
                BumpEpoch(ref _readbackLayoutGeneration);
            if (requiredTreeHeaderCount > int.MaxValue - request.Trees.Count)
            {
                XREngine.Debug.PhysicsWarning("[GPUPhysicsChainDispatcher] Dynamic header arena count overflow; GPU work remains pending.");
                return false;
            }
            requiredTreeHeaderCount += request.Trees.Count;
        }

        if (!EnsureResidentArenaCapacity(backend, requiredTreeHeaderCount))
            return false;

        return EnsureActiveWorkCapacity(backend, requests.Count, requiredTreeHeaderCount);
    }

    private static bool HasSingleDispatchGroup(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (requests.Count <= 1)
            return true;

        GPUDispatchGroupKey key = GPUDispatchGroupKey.From(requests[0]);
        for (int i = 1; i < requests.Count; ++i)
            if (CompareKeys(GPUDispatchGroupKey.From(requests[i]), key) != 0)
                return false;

        return true;
    }

    private static bool ContainsBatchedRequest(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        for (int index = 0; index < requests.Count; ++index)
            if (requests[index].DispatchIsolationKey == 0)
                return true;

        return false;
    }


    private static int CompareKeys(in GPUDispatchGroupKey left, in GPUDispatchGroupKey right)
        => left.DispatchIsolationKey.CompareTo(right.DispatchIsolationKey);


    private void ReadbackSynchronously(IPhysicsChainComputeBackend backend, IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        using var profilerState = RuntimeEngine.Profiler.Start("GPUPhysicsChainDispatcher.ReadbackSynchronously");

        if (_particlesBuffer is null || requests.Count == 0)
            return;

        bool anyReadbackRequired = false;
        for (int i = 0; i < requests.Count; ++i)
        {
            if (requests[i].Component.RequiresGpuReadback())
            {
                anyReadbackRequired = true;
                break;
            }
        }

        if (!anyReadbackRequired)
            return;

        if (!TryCompletePass(backend, SimulationCompletionPass))
            return;

        GPUParticleData[] readback = ArrayPool<GPUParticleData>.Shared.Rent(Math.Max(TotalParticleCount, 1));
        try
        {
            ReadParticleDataFromBuffer(
                _particlesBuffer,
                readback.AsSpan(0, TotalParticleCount),
                backend,
                _currentDispatchGroupIsBatched);

            for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
            {
                GPUPhysicsChainRequest request = requests[requestIndex];
                if (!request.Component.RequiresGpuReadback())
                    continue;

                if (request.ParticleOffset + request.Particles.Count > TotalParticleCount)
                    continue;

                request.Component.ApplyReadbackData(readback.AsSpan(request.ParticleOffset, request.Particles.Count), request.ExecutionGeneration, request.SubmissionId);
            }
        }
        finally
        {
            ArrayPool<GPUParticleData>.Shared.Return(readback, clearArray: false);
        }
    }

    private bool QueueAsyncReadback(IPhysicsChainComputeBackend backend, IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (_particlesBuffer is null || requests.Count == 0)
            return true;

        int readbackRequestCount = 0;
        int readbackParticleCount = 0;
        for (int i = 0; i < requests.Count; ++i)
            if (requests[i].Component.RequiresGpuReadback())
            {
                ++readbackRequestCount;
                readbackParticleCount += requests[i].Particles.Count;
            }

        if (readbackRequestCount == 0 || readbackParticleCount <= 0)
            return true;
        ++_readbackEnqueueAttemptCount;
        if (!backend.Capabilities.SupportsReadbackPipeline)
        {
            NotifyAsyncReadbackUnavailable(requests, "backend-readback-capability");
            return RecordReadbackEnqueueFailure("backend-readback-capability");
        }

        XRDataBuffer<GPUParticleData> readbackBuffer = AcquireReadbackBuffer((uint)readbackParticleCount);
        GPUReadbackRequestSnapshot[] snapshots = ArrayPool<GPUReadbackRequestSnapshot>.Shared.Rent(readbackRequestCount);
        int snapshotIndex = 0;
        int destinationParticleOffset = 0;
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            if (!request.Component.RequiresGpuReadback())
                continue;

            int particleCount = request.Particles.Count;
            nint readOffset = request.ParticleOffset * ParticleStateSizeBytes;
            nint writeOffset = destinationParticleOffset * ParticleStateSizeBytes;
            nuint byteCount = (nuint)(particleCount * ParticleStateSizeBytes);
            if (!ValidateGpuCopyRange(_particlesBuffer, readOffset, readbackBuffer, writeOffset, byteCount, "combined->selective-readback", request.RequestId))
            {
                _readbackBufferPool.Push(readbackBuffer);
                ArrayPool<GPUReadbackRequestSnapshot>.Shared.Return(snapshots, clearArray: false);
                NotifyAsyncReadbackUnavailable(requests, "batched-readback-range");
                return RecordReadbackEnqueueFailure("batched-readback-range");
            }

            var copy = new PhysicsChainComputeBufferCopy(_particlesBuffer, readOffset, readbackBuffer, writeOffset, byteCount);
            if (!TryCopyBuffer(backend, copy, "combined-to-selective-readback"))
            {
                _readbackBufferPool.Push(readbackBuffer);
                ArrayPool<GPUReadbackRequestSnapshot>.Shared.Return(snapshots, clearArray: false);
                NotifyAsyncReadbackUnavailable(requests, "batched-readback-buffer-copy");
                return RecordReadbackEnqueueFailure("batched-readback-buffer-copy");
            }
            RecordGpuCopyBytes((long)byteCount, _currentDispatchGroupIsBatched);

            snapshots[snapshotIndex++] = new GPUReadbackRequestSnapshot(
                request.Component,
                destinationParticleOffset,
                particleCount,
                request.ExecutionGeneration,
                request.SubmissionId);
            destinationParticleOffset += particleCount;
        }

        if (!TryCompletePass(backend, SelectiveReadbackTransferCompletionPass))
        {
            _readbackBufferPool.Push(readbackBuffer);
            ArrayPool<GPUReadbackRequestSnapshot>.Shared.Return(snapshots, clearArray: false);
            NotifyAsyncReadbackUnavailable(requests, "batched-readback-completion-barrier");
            return RecordReadbackEnqueueFailure("batched-readback-completion-barrier");
        }
        XRGpuFence? fence = backend.InsertFence();
        if (fence is null)
        {
            _readbackBufferPool.Push(readbackBuffer);
            ArrayPool<GPUReadbackRequestSnapshot>.Shared.Return(snapshots, clearArray: false);
            NotifyAsyncReadbackUnavailable(requests, "batched-readback-fence");
            return RecordReadbackEnqueueFailure("batched-readback-fence");
        }

        _inFlight.Add(new InFlightDispatch(
            readbackBuffer,
            backend,
            fence,
            readbackParticleCount,
            snapshots,
            snapshotIndex,
            _readbackFrameIndex,
            _currentDispatchGroupIsBatched));
        ++_readbackSubmittedCount;
        return true;
    }

    private bool RecordReadbackEnqueueFailure(string reason)
    {
        ++_readbackEnqueueFailureCount;
        _lastReadbackEnqueueFailureReason = reason;
        return false;
    }

    private static void NotifyAsyncReadbackUnavailable(IReadOnlyList<GPUPhysicsChainRequest> requests, string reason)
    {
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            if (request.Component.RequiresGpuReadback())
                request.Component.NotifyGpuReadbackUnavailable(reason);
        }
    }

    private bool HasPendingSelectiveReadback()
    {
        foreach (IPhysicsChainReadbackCoordinator world in _readbackWorlds)
            if (world.HasPendingTransfers)
                return true;
        return false;
    }

    private bool PublishSelectiveReadbacksAfterSimulation(IPhysicsChainComputeBackend backend)
    {
        TrackReadbackWorlds(_activeRequests);
        bool hasActiveReadbackSource = false;
        foreach (IPhysicsChainReadbackCoordinator world in _readbackWorlds)
            if (world.HasActiveSources)
            {
                hasActiveReadbackSource = true;
                break;
            }
        if (!hasActiveReadbackSource)
            return true;

        if (!backend.BeginBatch())
            return RecordDispatchFailure("SelectiveReadbackBatch");
        bool committed = false;
        try
        {
            QueueSelectiveReadbacks(backend);
            backend.CommitBatch();
            committed = true;
            return true;
        }
        finally
        {
            if (!committed)
                backend.RollbackBatch();
        }
    }

    private void QueueSelectiveReadbacks(IPhysicsChainComputeBackend backend)
    {
        if (_particlesBuffer is null || _transformMatricesBuffer is null || _selectiveReadbackGatherProgram is null)
            return;
        if (!EnsureProgramLinked(_selectiveReadbackGatherProgram))
            return;

        PhysicsChainReadbackSourceEpoch epoch = GetReadbackSourceEpoch();
        foreach (IPhysicsChainReadbackCoordinator world in _readbackWorlds)
        {
            if (!_readbackWorldsScheduledThisFrame.Add(world))
                continue;

            Array.Clear(_pendingSelectiveReadbackPlans);
            int planCount = world.BuildPendingReadbackGatherPlans(
                epoch,
                _readbackFrameIndex,
                _pendingSelectiveReadbackPlans);
            for (int planIndex = 0; planIndex < planCount; ++planIndex)
            {
                PhysicsChainReadbackGatherPlan? plan = _pendingSelectiveReadbackPlans[planIndex];
                _pendingSelectiveReadbackPlans[planIndex] = null;
                if (plan is not null)
                    SubmitSelectiveReadbackPlan(world, backend, plan);
            }
        }
    }

    private void TrackReadbackWorlds(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        for (int i = 0; i < requests.Count; ++i)
        {
            IPhysicsChainReadbackCoordinator? world = requests[i].Component.ReadbackCoordinator;
            if (world is not null)
                _readbackWorlds.Add(world);
        }
    }

    private void SubmitSelectiveReadbackPlan(
        IPhysicsChainReadbackCoordinator world,
        IPhysicsChainComputeBackend backend,
        PhysicsChainReadbackGatherPlan plan)
    {
        if (!world.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _))
            return;

        if (!TryFindRequest(world, plan.InstanceHandle, out GPUPhysicsChainRequest? request)
            || !TryBuildGpuGatherItems(plan, request!, _selectiveReadbackGatherItems)
            || !EnsureSelectiveReadbackSlotResources(world, backend, lease.Slot, plan.ElementCount, plan.ByteCount,
                out PhysicsChainSelectiveReadbackSlotResources? resources))
        {
            world.FailReadbackStagingSlot(lease, _readbackFrameIndex);
            return;
        }

        XRDataBuffer<PhysicsChainGpuReadbackGatherItem> itemBuffer = resources!.Items!;
        XRDataBuffer<uint> packedOutput = resources.PackedOutput!;
        XRDataBuffer<uint> staging = resources.MappedStaging!;

        bool committed = false;
        resources.ProducerRenderer = backend.Renderer;
        resources.ProducerBackend = backend;
        resources.HasQueuedWork = true;
        try
        {
            uint itemBytes = itemBuffer.WriteDataRaw(CollectionsMarshal.AsSpan(_selectiveReadbackGatherItems));
            PushBufferUpdate(itemBuffer, fullPush: false, itemBytes);
            RecordCpuUploadBytes(itemBytes, _currentDispatchGroupIsBatched);

            _selectiveReadbackGatherProgram!.Uniform("ItemCount", (uint)_selectiveReadbackGatherItems.Count);
            _selectiveReadbackGatherProgram.Uniform("ParticleCapacity", _particlesBuffer!.ElementCount);
            _selectiveReadbackGatherProgram.Uniform("TransformCapacity", _transformMatricesBuffer!.ElementCount);
            _selectiveReadbackGatherProgram.Uniform("OutputWordCapacity", packedOutput.ElementCount);
            _selectiveReadbackGatherProgram.BindBuffer(_particlesBuffer, 0);
            _selectiveReadbackGatherProgram.BindBuffer(_transformMatricesBuffer, 1);
            _selectiveReadbackGatherProgram.BindBuffer(itemBuffer, 2);
            _selectiveReadbackGatherProgram.BindBuffer(packedOutput, 3);
            uint groups = ((uint)_selectiveReadbackGatherItems.Count + ReadbackGatherLocalSizeX - 1u) / ReadbackGatherLocalSizeX;
            if (!TryDispatchDirect(
                    backend,
                    _selectiveReadbackGatherProgram,
                    Math.Max(groups, 1u),
                    1u,
                    1u,
                    PhysicsChainComputePassKind.SelectiveReadbackGather,
                    resources))
            {
                return;
            }
            if (!TryCompletePass(backend, SelectiveReadbackGatherCompletionPass))
            {
                return;
            }

            nuint exactByteCount = (nuint)plan.ByteCount;
            var copy = new PhysicsChainComputeBufferCopy(packedOutput, 0, staging, 0, exactByteCount, resources);
            if (!ValidateGpuCopyRange(packedOutput, 0, staging, 0, exactByteCount, "selective-gather->mapped-staging", request!.RequestId)
                || !TryCopyBuffer(backend, copy, "selective-gather-to-mapped-staging"))
            {
                return;
            }
            RecordGpuCopyBytes(plan.ByteCount, _currentDispatchGroupIsBatched);
            if (!TryCompletePass(backend, SelectiveReadbackTransferCompletionPass))
            {
                return;
            }

            XRGpuFence? gpuFence = backend.InsertFence();
            if (gpuFence is null)
            {
                return;
            }
            resources.CompletionFence = gpuFence;

            PhysicsChainMappedReadbackStagingSource source = resources.StagingSource;
            PhysicsChainGpuReadbackFence fence = resources.Fence;
            source.Reset(backend, staging, plan.ByteCount);
            fence.Reset(gpuFence, ownsFence: false);
            if (!source.IsValid
                || !world.CommitReadbackStagingSlot(lease, source, fence, _readbackFrameIndex, out _))
            {
                source.Dispose();
                fence.Dispose();
            }
            else
                committed = true;
        }
        finally
        {
            if (!committed)
            {
                if (resources.CompletionFence is null)
                {
                    try { resources.CompletionFence = backend.InsertFence(); }
                    catch (Exception exception) { XREngine.Debug.LogException(exception); }
                    if (resources.CompletionFence is null)
                        resources.Quarantined = true;
                }
                resources.StagingSource.Dispose();
                resources.Fence.Dispose();
                world.FailReadbackStagingSlot(lease, _readbackFrameIndex);
            }
        }
    }

    private bool TryFindRequest(IPhysicsChainReadbackCoordinator world,
        PhysicsChainRuntimeHandle handle, out GPUPhysicsChainRequest? request)
    {
        lock (_registeredComponentsSync)
        {
            for (int i = 0; i < _registeredComponentSnapshot.Count; ++i)
            {
                GPUPhysicsChainRequest candidate = _registeredComponentSnapshot[i];
                if (ReferenceEquals(candidate.Component.ReadbackCoordinator, world)
                    && candidate.Component.RuntimeHandle == handle)
                {
                    request = candidate;
                    return true;
                }
            }
        }

        request = null;
        return false;
    }

    private bool TryBuildGpuGatherItems(
        PhysicsChainReadbackGatherPlan plan,
        GPUPhysicsChainRequest request,
        List<PhysicsChainGpuReadbackGatherItem> destination)
    {
        destination.Clear();
        PhysicsChainRenderInputPacket input = request.AcceptedInput;
        if (!input.HasData || !input.AffineInputValid || request.ParticleOffset < 0
            || request.Particles.Count == 0 || request.PendingArenaAllocationChange || request.HasPendingResidentRetry
            || (long)request.ParticleOffset + request.Particles.Count > int.MaxValue
            || request.UploadedArenaAllocationGeneration != request.ArenaAllocationGeneration
            || request.UploadedParticleStateVersion != request.ParticleStateVersion
            || request.UploadedStaticDataVersion != request.StaticDataVersion
            || request.UploadedTransformDataVersion != request.TransformDataSignature
            || plan.InstanceSourceGeneration != input.ReadbackSourceGeneration
            || plan.InstanceSourceGeneration != request.Component.ReadbackSourceGeneration
            || _particlesBuffer is null || _transformMatricesBuffer is null
            || (ulong)request.ParticleOffset + (uint)request.Particles.Count > _particlesBuffer.ElementCount
            || (ulong)request.ParticleOffset + (uint)request.Transforms.Count > _transformMatricesBuffer.ElementCount)
            return false;
        destination.EnsureCapacity(plan.ElementCount);
        ReadOnlySpan<PhysicsChainReadbackGatherItem> items = plan.Items.Span;
        for (int i = 0; i < items.Length; ++i)
        {
            PhysicsChainReadbackGatherItem item = items[i];
            bool isParticle = item.Kind == PhysicsChainReadbackElementKind.Particle;
            bool isTransform = item.Kind is PhysicsChainReadbackElementKind.Bone
                or PhysicsChainReadbackElementKind.Socket
                or PhysicsChainReadbackElementKind.FullTransform;
            if ((!isParticle && !isTransform)
                || item.SourceIndex < 0
                || item.DestinationByteOffset < 0
                || item.ByteCount <= 0 || item.DestinationByteOffset > plan.ByteCount - item.ByteCount
                || (item.DestinationByteOffset & 3) != 0
                || (item.ByteCount & 3) != 0)
                return false;

            int sourceCount = isParticle ? request.Particles.Count : request.Transforms.Count;
            int expectedBytes = isParticle ? 6 * sizeof(float) : 12 * sizeof(float);
            if (item.ByteCount != expectedBytes || item.SourceIndex >= sourceCount
                || request.ParticleOffset > int.MaxValue - item.SourceIndex
                || !isParticle && (input.Bones.Length != sourceCount || request.Particles.Count != sourceCount))
                return false;

            int childIndex = -1;
            Vector3 restDirection = Vector3.Zero;
            if (isTransform)
            {
                for (int boneIndex = 0; boneIndex < input.Bones.Length; ++boneIndex)
                    if (input.Bones[boneIndex].ParentIndex == item.SourceIndex)
                    {
                        childIndex = boneIndex;
                        restDirection = input.Bones[boneIndex].RestLocalDirection;
                        break;
                    }
                if (!float.IsFinite(restDirection.X) || !float.IsFinite(restDirection.Y) || !float.IsFinite(restDirection.Z))
                    return false;
            }

            destination.Add(new PhysicsChainGpuReadbackGatherItem
            {
                Kind = (uint)item.Kind,
                SourceIndex = (uint)(request.ParticleOffset + item.SourceIndex),
                DestinationWordOffset = (uint)(item.DestinationByteOffset / sizeof(uint)),
                WordCount = (uint)(item.ByteCount / sizeof(uint)),
                ChildSourceIndex = childIndex < 0 ? -1 : checked(request.ParticleOffset + childIndex),
                RestLocalDirection = restDirection,
            });
        }

        return destination.Count == plan.ElementCount;
    }

    private bool EnsureSelectiveReadbackSlotResources(
        IPhysicsChainReadbackCoordinator world,
        IPhysicsChainComputeBackend backend,
        int slotIndex,
        int itemCount,
        int byteCount,
        out PhysicsChainSelectiveReadbackSlotResources? slot)
    {
        slot = null;
        if ((uint)slotIndex >= SelectiveReadbackSlotCount || itemCount <= 0 || byteCount <= 0)
            return false;

        if (!_selectiveReadbackWorldResources.TryGetValue(world, out PhysicsChainSelectiveReadbackWorldResources? bank))
        {
            bank = new PhysicsChainSelectiveReadbackWorldResources(SelectiveReadbackSlotCount);
            _selectiveReadbackWorldResources.Add(world, bank);
        }
        slot = bank.Slots[slotIndex];
        if (!slot.TryBeginReuse(backend))
            return RecordReadbackEnqueueFailure("SelectiveReadbackSlotBusyOrUnsafe");
        uint requiredItems = (uint)itemCount;
        uint requiredWords = (uint)((byteCount + sizeof(uint) - 1) / sizeof(uint));
        EnsureSelectiveBufferCapacity(ref slot.Items, $"PhysicsChainReadbackItems{slotIndex}", requiredItems, EBufferUsage.StreamDraw);
        EnsureSelectiveBufferCapacity(ref slot.PackedOutput, $"PhysicsChainReadbackPacked{slotIndex}", requiredWords, EBufferUsage.StreamCopy);
        EnsureSelectiveBufferCapacity(ref slot.MappedStaging, $"PhysicsChainReadbackStaging{slotIndex}", requiredWords, EBufferUsage.StreamRead, mappedReadback: true);

        XRDataBuffer<uint> staging = slot.MappedStaging!;
        return backend.EnsureGpuBufferReady(staging);
    }

    private static bool EnsureSelectiveBufferCapacity<T>(
        ref XRDataBuffer<T>? buffer,
        string name,
        uint requiredElements,
        EBufferUsage usage,
        bool mappedReadback = false)
        where T : unmanaged
    {
        if (buffer is not null && buffer.ElementCount >= requiredElements)
            return false;

        buffer?.Dispose();
        buffer = new XRDataBuffer<T>(name, EBufferTarget.ShaderStorageBuffer, XRMath.NextPowerOfTwo(Math.Max(requiredElements, 1u)))
        {
            DisposeOnPush = false,
            Usage = usage,
        };
        if (mappedReadback)
        {
            buffer.DefaultMemoryPolicy = XRBufferMemoryPolicy.GpuToCpuReadback;
            buffer.StorageFlags = EBufferMapStorageFlags.Read | EBufferMapStorageFlags.ClientStorage;
            buffer.RangeFlags = EBufferMapRangeFlags.Read;
        }
        return true;
    }

    private void PollSelectiveReadbackTransfersOnce()
    {
        if (_lastReadbackPollFrame == _readbackFrameIndex)
            return;

        _lastReadbackPollFrame = _readbackFrameIndex;
        PhysicsChainReadbackSourceEpoch epoch = GetReadbackSourceEpoch();
        foreach (IPhysicsChainReadbackCoordinator world in _readbackWorlds)
            world.PollReadbackTransfers(_readbackFrameIndex, epoch);
    }

    private void RetireInactiveReadbackWorlds()
    {
        _readbackWorldsToRetire.Clear();
        foreach (IPhysicsChainReadbackCoordinator world in _readbackWorlds)
            if (!world.HasActiveSources && !world.HasPendingTransfers)
                _readbackWorldsToRetire.Add(world);

        for (int i = 0; i < _readbackWorldsToRetire.Count; ++i)
        {
            IPhysicsChainReadbackCoordinator world = _readbackWorldsToRetire[i];
            _readbackWorlds.Remove(world);
            if (_selectiveReadbackWorldResources.Remove(world, out PhysicsChainSelectiveReadbackWorldResources? bank))
                _retiredSelectiveReadbackWorldResources.Add(bank);
        }

        for (int index = _retiredSelectiveReadbackWorldResources.Count - 1; index >= 0; --index)
        {
            PhysicsChainSelectiveReadbackWorldResources bank = _retiredSelectiveReadbackWorldResources[index];
            if (!bank.CanRetire())
                continue;
            bank.Dispose();
            _retiredSelectiveReadbackWorldResources.RemoveAt(index);
        }
    }

    private PhysicsChainReadbackSourceEpoch GetReadbackSourceEpoch()
        => new(_readbackBackendGeneration, _readbackArenaGeneration, _readbackLayoutGeneration);

    private static void BumpEpoch(ref uint generation)
    {
        unchecked { ++generation; }
        if (generation == 0u)
            generation = 1u;
    }

    private bool DispatchActiveWorkGeneration(IPhysicsChainComputeBackend backend)
    {
        if (_treeWorkItems.Count == 0)
            return true;

        using var profilerState = RuntimeEngine.Profiler.Start("GPUPhysicsChainDispatcher.ActiveWork");

        if (_activeWorkProgram is null
            || _instanceMetadataBuffer is null
            || _treeWorkItemBuffer is null
            || _activeTreeIdBuffer is null
            || _activeWorkCounterBuffer is null
            || _indirectDispatchArgumentBuffer is null)
            return false;

        _activeWorkProgram.Uniform("CandidateCount", (uint)_treeWorkItems.Count);
        _activeWorkProgram.Uniform("InstanceCount", (uint)_instanceMetadata.Count);
        _activeWorkProgram.Uniform("CapacityPerBucket", (uint)_activeListCapacityPerBucket);
        _activeWorkProgram.Uniform("FrameIndex", _activeWorkFrameIndex);
        _activeWorkProgram.BindBuffer(_instanceMetadataBuffer, 0);
        _activeWorkProgram.BindBuffer(_treeWorkItemBuffer, 1);
        _activeWorkProgram.BindBuffer(_activeTreeIdBuffer, 2);
        _activeWorkProgram.BindBuffer(_activeWorkCounterBuffer, 3);
        _activeWorkProgram.BindBuffer(_indirectDispatchArgumentBuffer, 4);

        _activeWorkProgram.Uniform("PassPhase", 0u);
        if (!TryDispatchDirect(backend, _activeWorkProgram, 1u, 1u, 1u, PhysicsChainComputePassKind.ActiveWorkReset))
            return false;
        if (!TryCompletePass(backend, ActiveWorkResetCompletionPass))
            return false;

        _activeWorkProgram.Uniform("PassPhase", 1u);
        uint compactionGroups = ((uint)_treeWorkItems.Count + LocalSizeX - 1u) / LocalSizeX;
        if (!TryDispatchDirect(
                backend,
                _activeWorkProgram,
                Math.Max(compactionGroups, 1u),
                1u,
                1u,
                PhysicsChainComputePassKind.ActiveWorkCompaction))
            return false;
        if (!TryCompletePass(backend, ActiveWorkCompactionCompletionPass))
            return false;

        _activeWorkProgram.Uniform("PassPhase", 2u);
        if (!TryDispatchDirect(backend, _activeWorkProgram, 1u, 1u, 1u, PhysicsChainComputePassKind.IndirectArgumentGeneration))
            return false;
        if (!TryCompletePass(backend, IndirectArgumentCompletionPass))
            return false;

        _activeWorkDispatchPassCount += 3;
        _activeWorkStorageBarrierCount += 3;
        ++_activeWorkCommandBarrierCount;
        return true;
    }

    private bool DispatchMainPhysics(IPhysicsChainComputeBackend backend, bool applyObjectMove)
        => DispatchSpecializedPhysics(backend, applyObjectMove);

    #endregion

    #region Readback

    private static bool ReadParticleDataFromBuffer(
        XRDataBuffer buffer,
        Span<GPUParticleData> readback,
        IPhysicsChainComputeBackend backend,
        bool isBatched)
    {
        if (readback.IsEmpty)
            return true;

        Span<byte> bytes = MemoryMarshal.AsBytes(readback);
        if (!backend.TryReadBuffer(buffer, bytes))
            return false;

        XRBufferWriteTelemetry.RecordUpload(XRBufferResolvedRoute.Readback, bytes.Length);
        RecordCpuReadbackBytes(bytes.Length, isBatched);
        return true;
    }

    private static void DistributeReadbackToComponents(ReadOnlySpan<GPUReadbackRequestSnapshot> requests, GPUParticleData[] readbackData, int particleCount)
    {
        for (int requestIndex = 0; requestIndex < requests.Length; ++requestIndex)
        {
            GPUReadbackRequestSnapshot request = requests[requestIndex];
            if (request.ParticleOffset + request.ParticleCount > particleCount)
                continue;

            var segment = new ArraySegment<GPUParticleData>(readbackData, request.ParticleOffset, request.ParticleCount);
            request.Component.ApplyReadbackData(segment, request.ExecutionGeneration, request.SubmissionId);
        }
    }

    #endregion

    #region Lifecycle

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (_enabled)
        {
            PublishBackendStatus(GPUPhysicsChainBackendStatus.NotEvaluated);
            return;
        }

        Reset();
        PublishBackendStatus(new GPUPhysicsChainBackendStatus(
            GPUPhysicsChainBackendState.Disabled,
            "None",
            CanDispatchGpu: false,
            CpuFallbackUsed: false,
            "Batched GPU physics-chain dispatch is disabled."));
    }

    public void Reset()
    {
        RetireSimulationReceipts();
        RetireInputPages();
        RetireOutputPages();
        _transformInputCatalog.Clear();
        _outputPublicationRequestByComponent.Clear();
        _activeRequests.Clear();
        _dispatchGroup.Clear();
        _paletteOutputRefreshPending = false;
        _backendEvaluated = false;
        _evaluatedRenderer = null;
        _computeBackend = null;

        for (int i = 0; i < _inFlight.Count; ++i)
        {
            InFlightDispatch entry = _inFlight[i];
            entry.Fence.Dispose();
            _readbackBufferPool.Push(entry.ReadbackBuffer);
            ArrayPool<GPUReadbackRequestSnapshot>.Shared.Return(entry.Requests, clearArray: false);
        }

        _inFlight.Clear();
        DisposeDeferredArenaResources();

        lock (_registeredComponentsSync)
        {
            for (int i = 0; i < _registeredComponentSnapshot.Count; ++i)
            {
                GPUPhysicsChainRequest request = _registeredComponentSnapshot[i];
                request.Component.ClearBatchedGpuDrivenBonePaletteSources();
                request.NeedsUpdate = false;
            }
        }

        _gpuDrivenPaletteBindings.Clear();
        _gpuDrivenBoneMappings.Clear();
        _gpuDrivenSkinPalette.Clear();
        _gpuDrivenBoneInvBindMatrices.Clear();
        ResetGpuBoundsResources();
        ResetStableGpuDrivenPaletteLayout();
        _gpuDrivenBonePaletteSignature = int.MinValue;
        BumpEpoch(ref _readbackBackendGeneration);
        BumpEpoch(ref _readbackArenaGeneration);
        BumpEpoch(ref _readbackLayoutGeneration);
    }

    public void Dispose()
    {
        Reset();
        DisposeInputPages();

        _particlesBuffer?.Dispose();
        _particleStaticBuffer?.Dispose();
        _transformMatricesBuffer?.Dispose();
        _collidersBuffer?.Dispose();
        _perTreeParamsBuffer?.Dispose();
        _instanceMetadataBuffer?.Dispose();
        _treeWorkItemBuffer?.Dispose();
        _activeTreeIdBuffer?.Dispose();
        _activeWorkCounterBuffer?.Dispose();
        _indirectDispatchArgumentBuffer?.Dispose();
        _gpuDrivenBonePaletteMappingsBuffer?.Dispose();
        DisposeUnpagedPaletteBuffers();
        DisposeSpecializedKernelResources();
        DisposeGpuBoundsResources();
        DisposeGlobalDebugResources();
        _gpuDrivenBoneInvBindMatricesBuffer?.Dispose();
        _particlesBuffer = null;
        _particleStaticBuffer = null;
        _transformMatricesBuffer = null;
        _collidersBuffer = null;
        _perTreeParamsBuffer = null;
        _instanceMetadataBuffer = null;
        _treeWorkItemBuffer = null;
        _activeTreeIdBuffer = null;
        _activeWorkCounterBuffer = null;
        _indirectDispatchArgumentBuffer = null;
        _gpuDrivenBonePaletteMappingsBuffer = null;
        _gpuDrivenSkinPaletteBuffer = null;
        _gpuDrivenPreviousSkinPaletteBuffer = null;
        _gpuDrivenBoneInvBindMatricesBuffer = null;
        _particleArenaHighWater = 0;
        _particleArenaUploadedHighWater = 0;
        _colliderArenaHighWater = 0;
        _colliderArenaUploadedHighWater = 0;
        _arenaLayoutResetRequested = 0;
        _arenaResourceGeneration = 0;
        _dynamicHeaderLayout.Clear();
        _dynamicHeaderResourceGeneration = -1;
        _activeWorkLayout.Clear();
        _activeWorkResourceGeneration = -1;
        _mainPassBindings = default;
        _mainPassBindingsValid = false;
        _gpuDrivenBonePaletteSignature = int.MinValue;

        while (_readbackBufferPool.Count > 0)
            _readbackBufferPool.Pop().Dispose();
        foreach (PhysicsChainSelectiveReadbackWorldResources bank in _selectiveReadbackWorldResources.Values)
            bank.Dispose();
        _selectiveReadbackWorldResources.Clear();
        foreach (PhysicsChainSelectiveReadbackWorldResources bank in _retiredSelectiveReadbackWorldResources)
            bank.Dispose();
        _retiredSelectiveReadbackWorldResources.Clear();

        _mainPhysicsProgram?.Destroy();
        _activeWorkProgram?.Destroy();
        _gpuBonePaletteProgram?.Destroy();
        _selectiveReadbackGatherProgram?.Destroy();
        _activeWorkShader?.Destroy();
        _gpuBonePaletteShader?.Destroy();
        _selectiveReadbackGatherShader?.Destroy();
        _mainPhysicsProgram = null;
        _activeWorkProgram = null;
        _gpuBonePaletteProgram = null;
        _selectiveReadbackGatherProgram = null;
        _mainPhysicsShader = null;
        _activeWorkShader = null;
        _gpuBonePaletteShader = null;
        _selectiveReadbackGatherShader = null;

        lock (_registeredComponentsSync)
            _registeredComponentSnapshot.Clear();

        _registeredComponents.Clear();
        _initialized = false;
    }

    #endregion
}

/// <summary>
/// Represents a registered GPU physics chain component's data for batched processing.
/// </summary>
public partial class GPUPhysicsChainRequest(IPhysicsChainComputeSource component)
{
    private static int s_nextRequestId;

    public IPhysicsChainComputeSource Component { get; } = component;
    public int RequestId { get; } = Interlocked.Increment(ref s_nextRequestId);
    internal readonly object InputGate = new();
    internal PhysicsChainRenderInputPacket PendingInput = new();
    internal PhysicsChainRenderInputPacket AcceptedInput = new();
    internal ulong NextInputSequence;
    internal ulong AcknowledgedInputSequence;
    private ulong _simulationInputSequence;
    private bool _retryAcceptedInput;
    private int _residentInputsRetryPending;
    private ulong _reportedInvalidAffineInputSequence;

    /// <summary>Accepts one complete pending input without exposing producer writes.</summary>
    internal bool TryAcceptInput(out ulong rejectedSequence,
        out int invalidTransformIndex, out bool transformCountMismatch)
    {
        rejectedSequence = 0u;
        invalidTransformIndex = -1;
        transformCountMismatch = false;
        lock (InputGate)
        {
            if (PendingInput.HasData && PendingInput.Sequence > AcceptedInput.Sequence &&
                !PendingInput.AffineInputValid)
            {
                if (_reportedInvalidAffineInputSequence != PendingInput.Sequence)
                {
                    _reportedInvalidAffineInputSequence = PendingInput.Sequence;
                    rejectedSequence = PendingInput.Sequence;
                    invalidTransformIndex = PendingInput.FirstInvalidAffineInputIndex;
                    transformCountMismatch = PendingInput.Transforms.Length != PendingInput.Particles.Length;
                }
                return false;
            }
            if (Interlocked.Exchange(ref _residentInputsRetryPending, 0) != 0)
            {
                InvalidateUploadedInputs();
                _retryAcceptedInput = true;
                NeedsUpdate = true;
            }
            if (!NeedsUpdate)
                return false;

            if (PendingInput.HasData && PendingInput.Sequence > AcceptedInput.Sequence)
            {
                (PendingInput, AcceptedInput) = (AcceptedInput, PendingInput);
                PendingInput.CopySchedulingFrom(AcceptedInput);
                ApplyAcceptedInput();
            }

            return AcceptedInput.HasData &&
                (_retryAcceptedInput || AcceptedInput.Sequence > _simulationInputSequence);
        }
    }

    /// <summary>Acknowledges only the input generation used by this dispatch.</summary>
    internal void AcknowledgeAcceptedInput()
    {
        lock (InputGate)
        {
            AcknowledgedInputSequence = AcceptedInput.Sequence;
            NeedsUpdate = PendingInput.Sequence > AcknowledgedInputSequence;
        }
    }

    /// <summary>Consumes simulation once even if output publication must wait.</summary>
    internal void MarkSimulationInputConsumed()
    {
        lock (InputGate)
        {
            _simulationInputSequence = AcceptedInput.Sequence;
            _retryAcceptedInput = false;
        }
    }

    /// <summary>Retries resident inputs after their submitted native work fails.</summary>
    internal void RetryAcceptedInput()
        => Interlocked.Exchange(ref _residentInputsRetryPending, 1);

    internal bool HasPendingResidentRetry => Volatile.Read(ref _residentInputsRetryPending) != 0;

    private void ApplyAcceptedInput()
    {
        PhysicsChainRenderInputPacket input = AcceptedInput;
        bool headerChanged = !DynamicHeaderInitialized ||
            DeltaTime != input.DeltaTime || ObjectScale != input.ObjectScale || Weight != input.Weight ||
            Force != input.Force || Gravity != input.Gravity || ObjectMove != input.ObjectMove ||
            FreezeAxis != input.FreezeAxis || LoopCount != input.LoopCount || TimeVar != input.TimeVar ||
            Colliders.Count != input.Colliders.Length || Trees.Count != input.Trees.Length;
        if (headerChanged)
            ++DynamicHeaderVersion;

        bool schedulingChanged = !SchedulingMetadataInitialized ||
            Enabled != input.Enabled || Relevant != input.Relevant || SleepState != input.SleepState ||
            QualityTier != input.QualityTier || Cadence != input.Cadence || Phase != input.Phase ||
            FeatureMask != input.FeatureMask || LoopCount != input.LoopCount;
        if (schedulingChanged)
            ++SchedulingMetadataVersion;

        Particles = input.Particles;
        ParticleStaticData = input.ParticleStaticData;
        Trees = input.Trees;
        Transforms = input.Transforms;
        Colliders = input.Colliders;
        DeltaTime = input.DeltaTime;
        ObjectScale = input.ObjectScale;
        Weight = input.Weight;
        Force = input.Force;
        Gravity = input.Gravity;
        ObjectMove = input.ObjectMove;
        FreezeAxis = input.FreezeAxis;
        LoopCount = input.LoopCount;
        TimeVar = input.TimeVar;
        UpdateMode = input.UpdateMode;
        DispatchIsolationKey = input.DispatchIsolationKey;
        ExecutionGeneration = input.ExecutionGeneration;
        SubmissionId = input.SubmissionId;
        StaticDataVersion = input.StaticDataVersion;
        TreeDataVersion = input.TreeDataVersion;
        ParticleStateVersion = input.ParticleStateVersion;
        TransformDataSignature = input.TransformDataSignature;
        ColliderDataSignature = input.ColliderDataSignature;
        BoneStructureSignature = input.BoneStructureSignature;
        SpatialInput = input.SpatialInput;
        LastKnownParticleCount = input.Particles.Length;
        SkipUpdate = input.LoopCount <= 0;
        Enabled = input.Enabled;
        Relevant = input.Relevant;
        SleepState = input.SleepState;
        QualityTier = input.QualityTier;
        Cadence = input.Cadence;
        Phase = input.Phase;
        FeatureMask = input.FeatureMask;
        SchedulingMetadataInitialized = true;
        DynamicHeaderInitialized = true;
    }

    // These views point only to the accepted bank during a render dispatch.
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleData> Particles { get; set; } = [];
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleStaticData> ParticleStaticData { get; set; } = [];
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleTreeData> Trees { get; set; } = [];
    public IReadOnlyList<Matrix4x4> Transforms { get; set; } = [];
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUColliderData> Colliders { get; set; } = [];

    private GPUPhysicsChainDispatcher.GPUParticleStaticData[] _adjustedParticleStaticBacking = [];

    // Per-component parameters
    public float DeltaTime;
    public float ObjectScale;
    public float Weight;
    public Vector3 Force;
    public Vector3 Gravity;
    public Vector3 ObjectMove;
    public int FreezeAxis;
    public int LoopCount;
    public float TimeVar;
    public int UpdateMode;
    public int DispatchIsolationKey;
    public int ExecutionGeneration;
    public long SubmissionId;
    public int StaticDataVersion;
    public int TreeDataVersion;
    public int ParticleStateVersion;
    public int TransformDataSignature;
    public int ColliderDataSignature;
    public int BoneStructureSignature;
    public PhysicsChainGpuSpatialInput SpatialInput;
    public bool NeedsUpdate;
    public bool SkipUpdate;
    public uint Enabled = 1u;
    public uint Relevant = 1u;
    public uint SleepState;
    public uint QualityTier;
    public uint Cadence = 1u;
    public uint Phase;
    public uint FeatureMask;
    public bool SchedulingMetadataInitialized;

    // Stable grow-only arena slices and their uploaded versions.
    public int ParticleOffset = -1;
    public int TreeOffset;
    public int ColliderOffset = -1;
    public int LastKnownParticleCount;
    public int ParticleArenaCapacity;
    public int ColliderArenaCapacity;
    public int ArenaAllocationGeneration;
    public int UploadedArenaAllocationGeneration = int.MinValue;
    public int UploadedParticleStateVersion = int.MinValue;
    public int UploadedStaticDataVersion = int.MinValue;
    public int UploadedTransformDataVersion = int.MinValue;
    public int UploadedColliderDataVersion = int.MinValue;
    public bool PendingArenaAllocationChange;
    public bool DynamicHeaderInitialized;
    public int DynamicHeaderVersion;
    public int UploadedDynamicHeaderVersion = int.MinValue;
    public int SchedulingMetadataVersion;
    public int UploadedSchedulingMetadataVersion = int.MinValue;
    public int UploadedTreeWorkStaticVersion = int.MinValue;
    public int UploadedTreeWorkArenaGeneration = int.MinValue;
    public int UploadedTreeWorkCount = int.MinValue;
    public int UploadedHeaderTreeVersion = int.MinValue;
    public int UploadedHeaderArenaGeneration = int.MinValue;
    public int UploadedHeaderColliderCount = int.MinValue;

    /// <summary>
    /// Preserves resident suballocation ownership when snapshot deserialization replaces a
    /// component instance with the same persistent identity. Uploaded-version baselines are
    /// intentionally invalidated because the replacement owns new CPU snapshots.
    /// </summary>
    internal void AdoptArenaAllocationFrom(GPUPhysicsChainRequest source)
    {
        ParticleOffset = source.ParticleOffset;
        ParticleArenaCapacity = source.ParticleArenaCapacity;
        ColliderOffset = source.ColliderOffset;
        ColliderArenaCapacity = source.ColliderArenaCapacity;
        ArenaAllocationGeneration = source.ArenaAllocationGeneration;
        ResidentParticleBounds = source.ResidentParticleBounds;
        ResidentBasisStretch = source.ResidentBasisStretch;
        ResidentSpatialValid = source.ResidentSpatialValid;
        ResidentSpatialInitialized = source.ResidentSpatialInitialized;

        InvalidateUploadedInputs();
        PendingArenaAllocationChange = ParticleOffset >= 0 || ColliderArenaCapacity > 0;
    }

    /// <summary>Requires a full input publication after uncertain native execution.</summary>
    internal void InvalidateUploadedInputs()
    {
        UploadedArenaAllocationGeneration = int.MinValue;
        UploadedParticleStateVersion = int.MinValue;
        UploadedStaticDataVersion = int.MinValue;
        UploadedTransformDataVersion = int.MinValue;
        UploadedColliderDataVersion = int.MinValue;
        UploadedDynamicHeaderVersion = int.MinValue;
        UploadedSchedulingMetadataVersion = int.MinValue;
        UploadedTreeWorkStaticVersion = int.MinValue;
        UploadedTreeWorkArenaGeneration = int.MinValue;
        UploadedTreeWorkCount = int.MinValue;
        UploadedHeaderTreeVersion = int.MinValue;
        UploadedHeaderArenaGeneration = int.MinValue;
        UploadedHeaderColliderCount = int.MinValue;
    }

    internal Span<GPUPhysicsChainDispatcher.GPUParticleStaticData> GetAdjustedStaticData()
    {
        int count = ParticleStaticData.Count;
        if (_adjustedParticleStaticBacking.Length != count)
            _adjustedParticleStaticBacking = new GPUPhysicsChainDispatcher.GPUParticleStaticData[count];

        return _adjustedParticleStaticBacking;
    }
}

internal readonly record struct GPUDispatchGroupKey(int DispatchIsolationKey)
{
    public static GPUDispatchGroupKey From(GPUPhysicsChainRequest request)
        => new(request.DispatchIsolationKey);
}

internal readonly record struct GPUReadbackRequestSnapshot(
    IPhysicsChainComputeSource Component,
    int ParticleOffset,
    int ParticleCount,
    int ExecutionGeneration,
    long SubmissionId);

internal readonly record struct InFlightDispatch(
    XRDataBuffer<GPUPhysicsChainDispatcher.GPUParticleData> ReadbackBuffer,
    IPhysicsChainComputeBackend Backend,
    XRGpuFence Fence,
    int ParticleCount,
    GPUReadbackRequestSnapshot[] Requests,
    int RequestCount,
    long SubmittedFrame,
    bool IsBatched);
