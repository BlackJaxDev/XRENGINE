using Silk.NET.OpenGL;
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
using XREngine.Rendering.OpenGL;

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

/// <summary>
/// Centralized dispatcher that batches all GPU physics chain components into a single compute dispatch per frame.
/// This maximizes GPU parallelization by processing all physics chains in one dispatch.
/// </summary>
public sealed class GPUPhysicsChainDispatcher
{
    private static readonly bool VerboseLogging = false;
    private const uint LocalSizeX = 128u;
    private const int MaxIterationsPerFrame = 3;
    private static readonly int ParticleStateSizeBytes = Unsafe.SizeOf<GPUParticleData>();
    private static readonly int ParticleStaticSizeBytes = Unsafe.SizeOf<GPUParticleStaticData>();
    private static readonly int PerTreeParamsSizeBytes = Unsafe.SizeOf<GPUPerTreeParams>();
    private static readonly int ColliderSizeBytes = Unsafe.SizeOf<GPUColliderData>();
    private static readonly int Matrix4x4SizeBytes = Unsafe.SizeOf<Matrix4x4>();

    private static long s_cpuUploadBytes;
    private static long s_gpuCopyBytes;
    private static long s_cpuReadbackBytes;
    private static long s_standaloneCpuUploadBytes;
    private static long s_standaloneCpuReadbackBytes;
    private static long s_batchedCpuUploadBytes;
    private static long s_batchedGpuCopyBytes;
    private static long s_batchedCpuReadbackBytes;
    private static long s_hierarchyRecalcTicks;
    private static int s_dispatchGroupCount;
    private static int s_dispatchIterationCount;
    private static long s_residentParticleBytes;

    private static GPUPhysicsChainDispatcher? _instance;
    public static GPUPhysicsChainDispatcher Instance => _instance ??= new GPUPhysicsChainDispatcher();

    private bool _enabled = true;
    private bool _initialized;

    // Registered components
    private readonly ConcurrentDictionary<PhysicsChainComponent, GPUPhysicsChainRequest> _registeredComponents = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly object _registeredComponentsSync = new();
    private readonly List<GPUPhysicsChainRequest> _registeredComponentSnapshot = [];
    private readonly List<GPUPhysicsChainRequest> _activeRequests = new();
    private readonly List<GPUPhysicsChainRequest> _dispatchGroup = [];
    private readonly List<InFlightDispatch> _inFlight = [];
    private readonly Stack<XRDataBuffer> _readbackBufferPool = new();

    // Shared GPU resources
    private XRShader? _mainPhysicsShader;
    private XRShader? _skipUpdateShader;
    private XRShader? _gpuBonePaletteShader;
    private XRRenderProgram? _mainPhysicsProgram;
    private XRRenderProgram? _skipUpdateProgram;
    private XRRenderProgram? _gpuBonePaletteProgram;

    // Combined buffers for all components
    private XRDataBuffer? _particlesBuffer;
    private XRDataBuffer? _particleStaticBuffer;
    private XRDataBuffer? _transformMatricesBuffer;
    private XRDataBuffer? _collidersBuffer;
    private XRDataBuffer? _perTreeParamsBuffer;
    private XRDataBuffer? _gpuDrivenBonePaletteMappingsBuffer;
    private XRDataBuffer? _gpuDrivenBoneMatricesBuffer;
    private XRDataBuffer? _gpuDrivenBoneInvBindMatricesBuffer;

    // Combined data lists
    private readonly List<GPUParticleStaticData> _allParticleStaticData = [];
    private readonly List<Matrix4x4> _allTransformMatrices = [];
    private readonly List<GPUColliderData> _allColliders = [];
    private readonly List<GPUPerTreeParams> _allPerTreeParams = [];
    private readonly List<GpuDrivenRendererPaletteBinding> _gpuDrivenPaletteBindings = [];
    private readonly List<GPUDrivenBoneMappingData> _gpuDrivenBoneMappings = [];
    private readonly List<Matrix4x4> _gpuDrivenBoneMatrices = [];
    private readonly List<Matrix4x4> _gpuDrivenBoneInvBindMatrices = [];
    private readonly List<GPUPhysicsChainRequest> _authoritativeParticleRequests = [];
    private int _staticParticleSignature = int.MinValue;
    private int _transformSignature = int.MinValue;
    private int _colliderSignature = int.MinValue;
    private int _gpuDrivenBonePaletteSignature = int.MinValue;
    private ulong _authoritativeParticleLayoutSignature;
    private int _authoritativeCombinedGeneration;

    // Statistics
    public int RegisteredComponentCount => _registeredComponents.Count;
    public int TotalParticleCount { get; private set; }
    public int TotalColliderCount { get; private set; }

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
        Interlocked.Exchange(ref s_dispatchGroupCount, 0);
        Interlocked.Exchange(ref s_dispatchIterationCount, 0);
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

    private static void AddResidentParticleBytes(long bytes)
    {
        if (bytes != 0)
            Interlocked.Add(ref s_residentParticleBytes, bytes);
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
        public int UpdateMode;
        public int _pad1;
        public int _pad2;
        public int _pad3;
        public int _pad4; // std430 array stride rounds to 16-byte alignment (vec3 = 16); 92→96 bytes
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUParticleTreeData
    {
        public Vector3 RestGravity;
        public float _pad0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUColliderData
    {
        public Vector4 Center;
        public Vector4 Params;
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

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUDrivenBoneMappingData
    {
        public int ParticleIndex;
        public int ChildParticleIndex;
        public int BoneMatrixIndex;
        public int Flags;
        public Vector3 RestLocalDirection;
        public float _pad0;
    }

    internal readonly record struct GpuDrivenRendererPaletteBinding(
        PhysicsChainComponent Component,
        XRMeshRenderer Renderer,
        GPUDrivenBoneMappingData[] Mappings,
        int ParticleBaseOffset,
        uint BoneMatrixElementCount,
        int BindingGeneration);

    #endregion

    #region Registration

    /// <summary>
    /// Registers a GPU physics chain component for batched processing.
    /// </summary>
    public void Register(PhysicsChainComponent component)
    {
        var request = new GPUPhysicsChainRequest(component);

        // Solution 4: Ensure resident buffer state is properly reset for (re)activation
        // These should already be default values for a new request, but we explicitly set them
        // to ensure correct behavior when components are deactivated and reactivated.
        request.ResidentParticlesInitialized = false;
        request.ResidentParticleStateVersion = int.MinValue;
        request.CombinedParticleStateVersion = int.MinValue;
        request.ActiveCombinedGeneration = -1;
        request.ResidentStaticDataVersion = int.MinValue;
        request.ActiveTransformSignature = int.MinValue;
        request.ActiveColliderSignature = int.MinValue;

        if (_registeredComponents.TryAdd(component, request))
        {
            lock (_registeredComponentsSync)
                _registeredComponentSnapshot.Add(request);

            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] Register: Component registered. ComponentHash={component.GetHashCode():X}, RequestId={request.RequestId}, TotalRegistered={_registeredComponents.Count}");
        }
        else
        {
            XREngine.Debug.LogWarning($"[GPUPhysicsChainDispatcher] Register: Component already registered (TryAdd failed). ComponentHash={component.GetHashCode():X}. This may indicate a double-registration bug.");
        }
    }

    /// <summary>
    /// Unregisters a GPU physics chain component.
    /// </summary>
    public void Unregister(PhysicsChainComponent component)
    {
        if (_registeredComponents.TryRemove(component, out GPUPhysicsChainRequest? request))
        {
            bool wasInAuthoritativeGroup = request.ActiveCombinedGeneration == _authoritativeCombinedGeneration;
            int particleCount = Math.Max(request.Particles.Count, request.LastKnownParticleCount);

            lock (_registeredComponentsSync)
                _registeredComponentSnapshot.Remove(request);
            RemoveRequestFromAuthoritativeGroup(request);
            DisposeResidentParticlesBuffer(request);

            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] Unregister: Component unregistered. ComponentHash={component.GetHashCode():X}, RequestId={request.RequestId}, WasInAuthoritativeGroup={wasInAuthoritativeGroup}, ParticleCount={particleCount}, TotalRegistered={_registeredComponents.Count}");

            if (VerboseLogging && wasInAuthoritativeGroup && particleCount > 0)
            {
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] Unregister: Component was in authoritative group with {particleCount} particles. Resident buffers were disposed; the component will recreate resident state if it is reactivated. ComponentHash={component.GetHashCode():X}");
            }
        }
        else
        {
            XREngine.Debug.LogWarning($"[GPUPhysicsChainDispatcher] Unregister: Component was not registered (TryRemove failed). ComponentHash={component.GetHashCode():X}. This may indicate an unbalanced register/unregister.");
        }
    }

    /// <summary>
    /// Checks if a component is registered for batched processing.
    /// </summary>
    public bool IsRegistered(PhysicsChainComponent component)
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

    /// <summary>
    /// Submits particle and collider data for a component. Called during component's Prepare phase.
    /// </summary>
    public void SubmitData(
        PhysicsChainComponent component,
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
        int colliderDataSignature)
    {
        if (!_registeredComponents.TryGetValue(component, out var request))
            return;

        // Snapshot into request-owned arrays so the render thread never iterates
        // the component's live mutable lists. Only allocates when element count changes.
        request.Particles = SnapshotInto(particles, ref request._particlesBacking);
        request.ParticleStaticData = SnapshotInto(particleStaticData, ref request._particleStaticBacking);
        request.Trees = SnapshotInto(trees, ref request._treesBacking);
        request.Transforms = SnapshotInto(transforms, ref request._transformsBacking);
        request.Colliders = SnapshotInto(colliders, ref request._collidersBacking);

        request.DeltaTime = deltaTime;
        request.ObjectScale = objectScale;
        request.Weight = weight;
        request.Force = force;
        request.Gravity = gravity;
        request.ObjectMove = objectMove;
        request.FreezeAxis = freezeAxis;
        request.LoopCount = loopCount;
        request.TimeVar = timeVar;
        request.UpdateMode = (int)component.UpdateMode;
        request.DispatchIsolationKey = component.UseBatchedDispatcher ? 0 : request.RequestId;
        request.ExecutionGeneration = executionGeneration;
        request.SubmissionId = submissionId;
        request.StaticDataVersion = staticDataVersion;
        request.ParticleStateVersion = particleStateVersion;
        request.TransformDataSignature = transformDataSignature;
        request.ColliderDataSignature = colliderDataSignature;
        request.LastKnownParticleCount = particles.Count;
        request.NeedsUpdate = true;
        request.SkipUpdate = loopCount <= 0;
    }

    public bool TryGetRenderParticleBuffers(PhysicsChainComponent component, out XRDataBuffer? particleBuffer, out XRDataBuffer? particleStaticBuffer, out int particleOffset, out int particleCount)
    {
        particleBuffer = null;
        particleStaticBuffer = null;
        particleOffset = 0;
        particleCount = 0;

        if (!_registeredComponents.TryGetValue(component, out GPUPhysicsChainRequest? request))
        {
            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] TryGetRenderParticleBuffers: Component not registered. ComponentHash={component.GetHashCode():X}");
            return false;
        }

        bool generationMatch = request.ActiveCombinedGeneration == _authoritativeCombinedGeneration;
        bool hasParticlesBuffer = _particlesBuffer is not null;
        bool hasStaticBuffer = _particleStaticBuffer is not null;

        if (generationMatch && hasParticlesBuffer && hasStaticBuffer)
        {
            particleBuffer = _particlesBuffer;
            particleStaticBuffer = _particleStaticBuffer;
            particleOffset = request.ParticleOffset;
            particleCount = request.Particles.Count > 0 ? request.Particles.Count : request.LastKnownParticleCount;
            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] TryGetRenderParticleBuffers: Using COMBINED buffer. ComponentHash={component.GetHashCode():X}, ParticleOffset={particleOffset}, ParticleCount={particleCount}, CombinedBufferHash={_particlesBuffer?.GetHashCode():X}");
            return particleCount > 0;
        }

        if (VerboseLogging)
            XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] TryGetRenderParticleBuffers: Falling back to resident buffer. ComponentHash={component.GetHashCode():X}, GenerationMatch={generationMatch} (request={request.ActiveCombinedGeneration}, auth={_authoritativeCombinedGeneration}), HasParticlesBuffer={hasParticlesBuffer}, HasStaticBuffer={hasStaticBuffer}");

        if (request.ResidentParticlesBuffer is null || request.ResidentParticleStaticBuffer is null)
        {
            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] TryGetRenderParticleBuffers: Resident buffers are unavailable. ComponentHash={component.GetHashCode():X}");
            return false;
        }

        particleBuffer = request.ResidentParticlesBuffer;
        particleStaticBuffer = request.ResidentParticleStaticBuffer;
        particleOffset = 0;
        particleCount = request.Particles.Count > 0 ? request.Particles.Count : request.LastKnownParticleCount;
        if (VerboseLogging)
            XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] TryGetRenderParticleBuffers: Using RESIDENT buffer. ComponentHash={component.GetHashCode():X}, ParticleOffset=0 (resident), ParticleCount={particleCount}, ResidentBufferHash={request.ResidentParticlesBuffer?.GetHashCode():X}");
        return particleCount > 0;
    }

    /// <summary>
    /// Process all registered components in a single batched dispatch.
    /// Should be called from the render thread (e.g., GlobalPreRender or GlobalPostRender).
    /// </summary>
    public void ProcessDispatches()
    {
        if (!_enabled)
            return;

        var renderer = AbstractRenderer.Current as OpenGLRenderer;
        if (renderer is null)
            return;

        // Collect active requests
        _activeRequests.Clear();
        lock (_registeredComponentsSync)
        {
            _activeRequests.EnsureCapacity(_registeredComponentSnapshot.Count);
            for (int i = 0; i < _registeredComponentSnapshot.Count; ++i)
            {
                GPUPhysicsChainRequest request = _registeredComponentSnapshot[i];
                if (request.NeedsUpdate)
                    _activeRequests.Add(request);
            }
        }

        _activeRequests.Sort(CompareRequestsByDispatchGroup);

        if (_activeRequests.Count == 0)
            return;

        EnsureInitialized();

        ProcessDispatchGroups(renderer);

        // Mark all requests as processed
        foreach (var req in _activeRequests)
            req.NeedsUpdate = false;
    }

    public void ProcessCompletions()
    {
        if (!_enabled)
            return;

        var renderer = AbstractRenderer.Current as OpenGLRenderer;
        if (renderer is null)
            return;

        for (int i = _inFlight.Count - 1; i >= 0; --i)
        {
            InFlightDispatch entry = _inFlight[i];
            if (!IsFenceComplete(entry.Fence, renderer))
                continue;

            GPUParticleData[] readback = ArrayPool<GPUParticleData>.Shared.Rent(Math.Max(entry.ParticleCount, 1));
            try
            {
                ReadParticleDataFromBuffer(entry.ReadbackBuffer, readback.AsSpan(0, entry.ParticleCount), renderer);
                DistributeReadbackToComponents(entry.Requests, readback, entry.ParticleCount);
            }
            finally
            {
                ArrayPool<GPUParticleData>.Shared.Return(readback, clearArray: false);
            }

            if (entry.Fence != IntPtr.Zero)
                renderer.RawGL.DeleteSync(entry.Fence);

            _readbackBufferPool.Push(entry.ReadbackBuffer);
            _inFlight.RemoveAt(i);
        }
    }

    #endregion

    #region Buffer Management

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        _mainPhysicsShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/PhysicsChain.comp", EShaderType.Compute);
        _skipUpdateShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/SkipUpdateParticles.comp", EShaderType.Compute);

        _mainPhysicsProgram = new XRRenderProgram(true, false, _mainPhysicsShader);
        _skipUpdateProgram = new XRRenderProgram(true, false, _skipUpdateShader);

        _initialized = true;
    }

    private void EnsureGpuBonePaletteProgram()
    {
        if (_gpuBonePaletteProgram is not null)
            return;

        _gpuBonePaletteShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/PhysicsChainBonePalette.comp", EShaderType.Compute);
        _gpuBonePaletteProgram = new XRRenderProgram(true, false, _gpuBonePaletteShader);
    }

    private void BuildCombinedBuffers(OpenGLRenderer renderer, IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        int staticParticleSignature = ComputeStaticParticleSignature(requests);
        int transformSignature = ComputeTransformSignature(requests);
        int colliderSignature = ComputeColliderSignature(requests);
        bool rebuildStaticParticleData = _particleStaticBuffer is null || _staticParticleSignature != staticParticleSignature;
        bool rebuildTransforms = _transformMatricesBuffer is null || _transformSignature != transformSignature;
        bool rebuildColliders = _collidersBuffer is null || _colliderSignature != colliderSignature;
        ulong particleLayoutSignature = ComputeParticleLayoutSignature(requests);
        bool hasCpuParticleStateChanges = HasCpuParticleStateChanges(requests);
        bool canReuseAuthoritativeParticles = _authoritativeParticleLayoutSignature == particleLayoutSignature && !hasCpuParticleStateChanges;

        if (_authoritativeParticleRequests.Count > 0 && !canReuseAuthoritativeParticles)
            SyncAuthoritativeParticlesToResidents(renderer);

        if (rebuildStaticParticleData)
            _allParticleStaticData.Clear();
        if (rebuildTransforms)
            _allTransformMatrices.Clear();
        if (rebuildColliders)
            _allColliders.Clear();
        _allPerTreeParams.Clear();

        int particleOffset = 0;
        int treeOffset = 0;
        int colliderOffset = 0;
        int dirtyTransformStart = -1;
        int dirtyTransformEndExclusive = -1;
        int dirtyColliderStart = -1;
        int dirtyColliderEndExclusive = -1;

        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            request.ParticleOffset = particleOffset;
            request.TreeOffset = treeOffset;
            request.ColliderOffset = colliderOffset;

            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] BuildCombinedBuffers: Assigning offsets. RequestIndex={requestIndex}, RequestId={request.RequestId}, ComponentHash={request.Component.GetHashCode():X}, ParticleOffset={particleOffset}, ParticleCount={request.Particles.Count}, TreeOffset={treeOffset}, ColliderOffset={colliderOffset}");

            EnsureResidentParticleBuffer(request);
            EnsureResidentStaticBuffer(request);

            if (rebuildStaticParticleData)
            {
                foreach (var particleStatic in request.ParticleStaticData)
                {
                    var adjusted = particleStatic;
                    if (adjusted.ParentIndex >= 0)
                        adjusted.ParentIndex += particleOffset;
                    adjusted.TreeIndex += treeOffset;
                    _allParticleStaticData.Add(adjusted);
                }
            }

            // Build per-tree params for the packed dispatch layout.
            foreach (var tree in request.Trees)
            {
                _allPerTreeParams.Add(new GPUPerTreeParams
                {
                    DeltaTime = request.TimeVar,
                    ObjectScale = request.ObjectScale,
                    Weight = request.Weight,
                    FreezeAxis = request.FreezeAxis,
                    Force = request.Force,
                    ColliderCount = request.Colliders.Count,
                    Gravity = request.Gravity,
                    ColliderOffset = colliderOffset,
                    ObjectMove = request.ObjectMove,
                    RestGravity = tree.RestGravity,
                    UpdateMode = request.UpdateMode,
                });
            }

            if (rebuildTransforms)
                _allTransformMatrices.AddRange(request.Transforms);
            else if (request.ActiveTransformSignature != request.TransformDataSignature)
                OverwriteCombinedRange(_allTransformMatrices, request.Transforms, particleOffset, ref dirtyTransformStart, ref dirtyTransformEndExclusive);
            if (rebuildColliders)
                _allColliders.AddRange(request.Colliders);
            else if (request.ActiveColliderSignature != request.ColliderDataSignature)
                OverwriteCombinedRange(_allColliders, request.Colliders, colliderOffset, ref dirtyColliderStart, ref dirtyColliderEndExclusive);

            request.ActiveTransformSignature = request.TransformDataSignature;
            request.ActiveColliderSignature = request.ColliderDataSignature;

            particleOffset += request.Particles.Count;
            treeOffset += request.Trees.Count;
            colliderOffset += request.Colliders.Count;
        }

        TotalParticleCount = particleOffset;
        TotalColliderCount = colliderOffset;

        // Resize/create buffers as needed
        bool particlesResized = EnsureBufferCapacity(ref _particlesBuffer, "Particles", (uint)Math.Max(TotalParticleCount, 1), 16);
        bool particleStaticResized = EnsureBufferCapacity(ref _particleStaticBuffer, "ParticleStatic", (uint)Math.Max(TotalParticleCount, 1), 16);
        bool transformMatricesResized = EnsureBufferCapacity(ref _transformMatricesBuffer, "TransformMatrices", (uint)_allTransformMatrices.Count, 16);
        bool collidersResized = EnsureBufferCapacity(ref _collidersBuffer, "Colliders", (uint)Math.Max(TotalColliderCount, 1), 12);
        bool perTreeParamsResized = EnsureBufferCapacity(ref _perTreeParamsBuffer, "PerTreeParams", (uint)Math.Max(_allPerTreeParams.Count, 1), 24);

        if (rebuildStaticParticleData || particleStaticResized || particlesResized)
        {
            uint staticBytes = _particleStaticBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_allParticleStaticData)) ?? 0u;
            bool fullStaticPush = particleStaticResized || particlesResized;
            PushBufferUpdate(_particleStaticBuffer, fullStaticPush, staticBytes);
            _staticParticleSignature = staticParticleSignature;
            RecordCpuUploadBytes(fullStaticPush ? _particleStaticBuffer?.Length ?? 0u : staticBytes, isBatched: true);
        }

        if (rebuildTransforms || transformMatricesResized)
        {
            uint transformBytes = _transformMatricesBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_allTransformMatrices)) ?? 0u;
            bool fullTransformPush = transformMatricesResized;
            PushBufferUpdate(_transformMatricesBuffer, fullTransformPush, transformBytes);
            RecordCpuUploadBytes(fullTransformPush ? _transformMatricesBuffer?.Length ?? 0u : transformBytes, isBatched: true);
            _transformSignature = transformSignature;
        }
        else if (dirtyTransformStart >= 0)
        {
            int dirtyTransformLength = dirtyTransformEndExclusive - dirtyTransformStart;
            uint transformBytes = _transformMatricesBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_allTransformMatrices).Slice(dirtyTransformStart, dirtyTransformLength), (uint)dirtyTransformStart) ?? 0u;
            PushBufferUpdate(_transformMatricesBuffer, fullPush: false, transformBytes, dirtyTransformStart * Matrix4x4SizeBytes);
            RecordCpuUploadBytes(transformBytes, isBatched: true);
            _transformSignature = transformSignature;
        }

        if (rebuildColliders || collidersResized)
        {
            uint colliderBytes = _collidersBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_allColliders)) ?? 0u;
            bool fullColliderPush = collidersResized;
            PushBufferUpdate(_collidersBuffer, fullColliderPush, colliderBytes);
            _colliderSignature = colliderSignature;
            RecordCpuUploadBytes(fullColliderPush ? _collidersBuffer?.Length ?? 0u : colliderBytes, isBatched: true);
        }
        else if (dirtyColliderStart >= 0)
        {
            int dirtyColliderLength = dirtyColliderEndExclusive - dirtyColliderStart;
            uint colliderBytes = _collidersBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_allColliders).Slice(dirtyColliderStart, dirtyColliderLength), (uint)dirtyColliderStart) ?? 0u;
            PushBufferUpdate(_collidersBuffer, fullPush: false, colliderBytes, dirtyColliderStart * ColliderSizeBytes);
            _colliderSignature = colliderSignature;
            RecordCpuUploadBytes(colliderBytes, isBatched: true);
        }

        uint perTreeBytes = _perTreeParamsBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_allPerTreeParams)) ?? 0u;
        bool fullPerTreePush = perTreeParamsResized;
        PushBufferUpdate(_perTreeParamsBuffer, fullPerTreePush, perTreeBytes);
        RecordCpuUploadBytes(fullPerTreePush ? _perTreeParamsBuffer?.Length ?? 0u : perTreeBytes, isBatched: true);

        if (!canReuseAuthoritativeParticles)
        {
            EnsureCombinedParticlesBufferAllocated(renderer);
            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] BuildCombinedBuffers: Copying resident particles into combined buffer. RequestCount={requests.Count}, LayoutSignatureMatch={_authoritativeParticleLayoutSignature == particleLayoutSignature}, HasCpuChanges={hasCpuParticleStateChanges}");
            CopyResidentParticlesIntoCombinedBuffer(renderer, requests);
        }
        else
        {
            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] BuildCombinedBuffers: REUSING authoritative particles (no copy needed). RequestCount={requests.Count}, AuthoritativeLayoutSig={_authoritativeParticleLayoutSignature:X}, CurrentLayoutSig={particleLayoutSignature:X}");
        }

        MarkAuthoritativeParticleGroup(requests, particleLayoutSignature);
    }

    private void EnsureResidentParticleBuffer(GPUPhysicsChainRequest request)
    {
        int particleCount = Math.Max(request.Particles.Count, 1);
        if (request.ResidentParticlesBuffer is null)
        {
            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] EnsureResidentParticleBuffer: Creating NEW resident buffer. RequestId={request.RequestId}, ParticleCount={particleCount}, ComponentHash={request.Component.GetHashCode():X}");

            request.ResidentParticlesBuffer = new XRDataBuffer(
                $"PhysicsChainResidentParticles_{request.RequestId:X}",
                EBufferTarget.ShaderStorageBuffer,
                XRMath.NextPowerOfTwo((uint)particleCount),
                EComponentType.Float,
                16,
                false,
                false)
            {
                DisposeOnPush = false,
                Usage = EBufferUsage.DynamicDraw,
            };
            request.ResidentParticleCapacity = (int)request.ResidentParticlesBuffer.ElementCount;
            request.ResidentParticleStateVersion = int.MinValue;
            request.ResidentParticlesInitialized = false;
            AddResidentParticleBytes((long)request.ResidentParticleCapacity * ParticleStateSizeBytes);
        }
        else if (request.ResidentParticlesBuffer.EnsureRawCapacity<GPUParticleData>((uint)particleCount))
        {
            int previousCapacity = request.ResidentParticleCapacity;
            request.ResidentParticleCapacity = (int)request.ResidentParticlesBuffer.ElementCount;
            request.ResidentParticleStateVersion = int.MinValue;
            request.ResidentParticlesInitialized = false;
            AddResidentParticleBytes((long)(request.ResidentParticleCapacity - previousCapacity) * ParticleStateSizeBytes);

            XREngine.Debug.LogWarning($"[GPUPhysicsChainDispatcher] EnsureResidentParticleBuffer: Resident buffer RESIZED (capacity change). RequestId={request.RequestId}, PreviousCapacity={previousCapacity}, NewCapacity={request.ResidentParticleCapacity}, ParticleCount={particleCount}. ResidentParticlesInitialized reset to false.");
        }

        if (!request.ResidentParticlesInitialized || request.ResidentParticleStateVersion != request.ParticleStateVersion)
        {
            bool wasUninitialized = !request.ResidentParticlesInitialized;
            bool versionMismatch = request.ResidentParticleStateVersion != request.ParticleStateVersion;

            if (request._particlesBacking.Length < request.Particles.Count)
            {
                XREngine.Debug.LogWarning($"[GPUPhysicsChainDispatcher] EnsureResidentParticleBuffer: CRITICAL - _particlesBacking array ({request._particlesBacking.Length}) is smaller than Particles.Count ({request.Particles.Count})! Data may be stale or incomplete. RequestId={request.RequestId}");
            }

            uint particleBytes = request.ResidentParticlesBuffer!.WriteDataRaw(request._particlesBacking.AsSpan(0, request.Particles.Count));
            bool fullParticlePush = !request.ResidentParticlesInitialized;
            PushBufferUpdate(request.ResidentParticlesBuffer, fullParticlePush, particleBytes);
            request.ResidentParticlesInitialized = true;
            request.ResidentParticleStateVersion = request.ParticleStateVersion;
            RecordCpuUploadBytes(fullParticlePush ? request.ResidentParticlesBuffer.Length : particleBytes, isBatched: true);

            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] EnsureResidentParticleBuffer: Uploaded particle data. RequestId={request.RequestId}, ParticleCount={request.Particles.Count}, WasUninitialized={wasUninitialized}, VersionMismatch={versionMismatch}, FullPush={fullParticlePush}, BytesWritten={particleBytes}");
        }
    }

    private void EnsureResidentStaticBuffer(GPUPhysicsChainRequest request)
    {
        int particleCount = Math.Max(request.ParticleStaticData.Count, 1);
        if (request.ResidentParticleStaticBuffer is null)
        {
            request.ResidentParticleStaticBuffer = new XRDataBuffer(
                $"PhysicsChainResidentStatic_{request.RequestId:X}",
                EBufferTarget.ShaderStorageBuffer,
                XRMath.NextPowerOfTwo((uint)particleCount),
                EComponentType.Float,
                16,
                false,
                false)
            {
                DisposeOnPush = false,
                Usage = EBufferUsage.StaticDraw,
            };
            request.ResidentParticleStaticCapacity = (int)request.ResidentParticleStaticBuffer.ElementCount;
            request.ResidentStaticDataVersion = int.MinValue;
        }
        else if (request.ResidentParticleStaticBuffer.EnsureRawCapacity<GPUParticleStaticData>((uint)particleCount))
        {
            request.ResidentParticleStaticCapacity = (int)request.ResidentParticleStaticBuffer.ElementCount;
            request.ResidentStaticDataVersion = int.MinValue;
        }

        if (request.ResidentStaticDataVersion != request.StaticDataVersion)
        {
            uint staticBytes = request.ResidentParticleStaticBuffer!.WriteDataRaw(request._particleStaticBacking.AsSpan(0, request.ParticleStaticData.Count));
            bool fullStaticPush = request.ResidentStaticDataVersion == int.MinValue;
            PushBufferUpdate(request.ResidentParticleStaticBuffer, fullStaticPush, staticBytes);
            request.ResidentStaticDataVersion = request.StaticDataVersion;
            RecordCpuUploadBytes(fullStaticPush ? request.ResidentParticleStaticBuffer.Length : staticBytes, isBatched: true);
        }
    }

    private void EnsureCombinedParticlesBufferAllocated(OpenGLRenderer renderer)
    {
        if (_particlesBuffer is null)
            return;

        TryGetBufferIdForGpuCopy(renderer, _particlesBuffer, out _);
    }

    private void CopyResidentParticlesIntoCombinedBuffer(OpenGLRenderer renderer, IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (VerboseLogging)
            XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] CopyResidentParticlesIntoCombinedBuffer: BEGIN. RequestCount={requests.Count}, CombinedBufferHash={_particlesBuffer?.GetHashCode():X}");

        if (_particlesBuffer is null)
        {
            XREngine.Debug.LogWarning("[GPUPhysicsChainDispatcher] CopyResidentParticlesIntoCombinedBuffer: _particlesBuffer is null, cannot copy.");
            return;
        }

        if (!TryGetBufferIdForGpuCopy(renderer, _particlesBuffer, out uint destinationBufferId) || destinationBufferId == 0)
        {
            XREngine.Debug.LogWarning("[GPUPhysicsChainDispatcher] CopyResidentParticlesIntoCombinedBuffer: Failed to get destination buffer ID.");
            return;
        }

        int copiedCount = 0;
        int skippedCount = 0;

        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            XRDataBuffer? residentBuffer = request.ResidentParticlesBuffer;
            if (residentBuffer is null || request.Particles.Count == 0)
            {
                if (VerboseLogging && request.Particles.Count > 0)
                    XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] CopyResidentParticlesIntoCombinedBuffer: Skipping request with unavailable resident buffer. RequestId={request.RequestId}, ParticleCount={request.Particles.Count}");
                ++skippedCount;
                continue;
            }

            if (!TryGetBufferIdForGpuCopy(renderer, residentBuffer, out uint sourceBufferId) || sourceBufferId == 0)
            {
                XREngine.Debug.LogWarning($"[GPUPhysicsChainDispatcher] CopyResidentParticlesIntoCombinedBuffer: Failed to get source buffer ID. RequestId={request.RequestId}");
                ++skippedCount;
                continue;
            }

            nuint byteCount = (nuint)(request.Particles.Count * ParticleStateSizeBytes);
            nint writeOffset = request.ParticleOffset * ParticleStateSizeBytes;
            if (!ValidateGpuCopyRange(residentBuffer, 0, _particlesBuffer, writeOffset, byteCount, "resident->combined", request.RequestId))
            {
                ++skippedCount;
                continue;
            }

            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] CopyResidentParticlesIntoCombinedBuffer: Copying. RequestId={request.RequestId}, ComponentHash={request.Component.GetHashCode():X}, ParticleOffset={request.ParticleOffset}, ParticleCount={request.Particles.Count}, ByteCount={byteCount}, WriteOffset={writeOffset}, SourceBufferId={sourceBufferId}, DestBufferId={destinationBufferId}");
            renderer.RawGL.CopyNamedBufferSubData(sourceBufferId, destinationBufferId, 0, writeOffset, byteCount);
            RecordGpuCopyBytes((long)byteCount, isBatched: true);
            ++copiedCount;
        }

        if (VerboseLogging)
            XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] CopyResidentParticlesIntoCombinedBuffer: END. Copied={copiedCount}, Skipped={skippedCount}");
        if (VerboseLogging && skippedCount > 0)
            XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] CopyResidentParticlesIntoCombinedBuffer: Copied {copiedCount} requests, skipped {skippedCount} requests.");
    }

    private void CopyCombinedParticlesBackToResidentBuffers(OpenGLRenderer renderer, IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (_particlesBuffer is null)
            return;

        if (!TryGetBufferIdForGpuCopy(renderer, _particlesBuffer, out uint sourceBufferId) || sourceBufferId == 0)
            return;

        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            XRDataBuffer? residentBuffer = request.ResidentParticlesBuffer;
            if (residentBuffer is null || request.Particles.Count == 0)
                continue;

            if (!TryGetBufferIdForGpuCopy(renderer, residentBuffer, out uint destinationBufferId) || destinationBufferId == 0)
                continue;

            nuint byteCount = (nuint)(request.Particles.Count * ParticleStateSizeBytes);
            nint readOffset = request.ParticleOffset * ParticleStateSizeBytes;
            if (!ValidateGpuCopyRange(_particlesBuffer, readOffset, residentBuffer, 0, byteCount, "combined->resident", request.RequestId))
                continue;

            renderer.RawGL.CopyNamedBufferSubData(sourceBufferId, destinationBufferId, readOffset, 0, byteCount);
            RecordGpuCopyBytes((long)byteCount, isBatched: true);
        }
    }

    private static bool TryGetBufferId(XRDataBuffer buffer, out uint bufferId)
    {
        bufferId = 0;
        foreach (var wrapper in buffer.APIWrappers)
        {
            if (wrapper is OpenGLRenderer.GLDataBuffer glBuffer
                && glBuffer.TryGetBindingId(out bufferId)
                && bufferId != 0)
                return true;
        }
        return false;
    }

    private static bool TryGetBufferIdForGpuCopy(OpenGLRenderer renderer, XRDataBuffer buffer, out uint bufferId)
    {
        bufferId = 0;
        if (renderer.GetOrCreateAPIRenderObject(buffer, generateNow: true) is OpenGLRenderer.GLDataBuffer glBuffer)
        {
            glBuffer.EnsureStorageAllocatedForGpuCopy();
            return glBuffer.TryGetBindingId(out bufferId) && bufferId != 0;
        }

        return TryGetBufferId(buffer, out bufferId);
    }

    private static bool ValidateGpuCopyRange(
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

    private static void DisposeResidentParticlesBuffer(GPUPhysicsChainRequest request)
    {
        if (request.ResidentParticlesBuffer is not null)
        {
            AddResidentParticleBytes(-((long)request.ResidentParticleCapacity * ParticleStateSizeBytes));
            request.ResidentParticlesBuffer.Dispose();
            request.ResidentParticlesBuffer = null;
            request.ResidentParticleCapacity = 0;
            request.ResidentParticleStateVersion = int.MinValue;
            request.ResidentParticlesInitialized = false;
        }

        request.ResidentParticleStaticBuffer?.Dispose();
        request.ResidentParticleStaticBuffer = null;
        request.ResidentParticleStaticCapacity = 0;
        request.ResidentStaticDataVersion = int.MinValue;
    }

    private static bool EnsureBufferCapacity(ref XRDataBuffer? buffer, string name, uint elementCount, uint componentCount)
    {
        if (elementCount == 0)
            elementCount = 1;

        if (buffer is null)
        {
            buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, XRMath.NextPowerOfTwo(elementCount), EComponentType.Float, componentCount, false, false);
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
            buffer.PushData();
        else if (byteLength > 0)
            buffer.PushSubData(byteOffset, byteLength);
    }

    private static void OverwriteCombinedRange<T>(List<T> destination, IReadOnlyList<T> source, int destinationOffset, ref int dirtyStart, ref int dirtyEndExclusive)
    {
        if (source.Count == 0)
            return;

        int endExclusive = destinationOffset + source.Count;
        for (int i = 0; i < source.Count; ++i)
            destination[destinationOffset + i] = source[i];

        if (dirtyStart < 0 || destinationOffset < dirtyStart)
            dirtyStart = destinationOffset;
        if (dirtyEndExclusive < endExclusive)
            dirtyEndExclusive = endExclusive;
    }

    private XRDataBuffer AcquireReadbackBuffer(uint particleCount)
    {
        uint elementCount = Math.Max(particleCount, 1u);
        while (_readbackBufferPool.Count > 0)
        {
            XRDataBuffer buffer = _readbackBufferPool.Pop();
            if (buffer.ElementCount >= elementCount)
                return buffer;

            buffer.Dispose();
        }

        var created = new XRDataBuffer("PhysicsChainReadback", EBufferTarget.ShaderStorageBuffer, elementCount, EComponentType.Float, 16, false, false)
        {
            DisposeOnPush = false,
            Usage = EBufferUsage.StreamRead,
        };
        // Allocate the GL buffer on the GPU so CopyNamedBufferSubData has a valid destination
        created.SetDataRaw(new GPUParticleData[elementCount]);
        created.PushData();
        return created;
    }

    private bool PublishBatchedGpuDrivenBoneMatrices(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        using var profilerState = Engine.Profiler.Start("GPUPhysicsChainDispatcher.PublishBatchedGpuDrivenBoneMatrices");

        if (!Engine.Rendering.Settings.CalculateSkinningInComputeShader)
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

        int signature = ComputeGpuDrivenBonePaletteSignature(_gpuDrivenPaletteBindings);
        bool needsRebuild = signature != _gpuDrivenBonePaletteSignature
            || _gpuDrivenBonePaletteMappingsBuffer is null
            || _gpuDrivenBoneMatricesBuffer is null
            || _gpuDrivenBoneInvBindMatricesBuffer is null;

        if (needsRebuild)
            RebuildBatchedGpuDrivenBonePaletteBuffers(signature);

        if (_gpuDrivenBoneMappings.Count == 0
            || _gpuDrivenBonePaletteMappingsBuffer is null
            || _gpuDrivenBoneMatricesBuffer is null
            || _gpuDrivenBoneInvBindMatricesBuffer is null)
            return false;

        _gpuBonePaletteProgram.Uniform("particleBaseOffset", 0);
        _gpuBonePaletteProgram.Uniform("mappingCount", _gpuDrivenBoneMappings.Count);
        _gpuBonePaletteProgram.BindBuffer(_particlesBuffer, 0);
        _gpuBonePaletteProgram.BindBuffer(_transformMatricesBuffer, 1);
        _gpuBonePaletteProgram.BindBuffer(_gpuDrivenBonePaletteMappingsBuffer, 2);
        _gpuBonePaletteProgram.BindBuffer(_gpuDrivenBoneMatricesBuffer, 3);

        uint groupsX = (uint)(_gpuDrivenBoneMappings.Count + 63) / 64u;
        _gpuBonePaletteProgram.DispatchCompute(Math.Max(groupsX, 1u), 1u, 1u);
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
            hash.Add(binding.BindingGeneration);
        }

        return hash.ToHashCode();
    }

    private void RebuildBatchedGpuDrivenBonePaletteBuffers(int signature)
    {
        _gpuDrivenBoneMappings.Clear();
        _gpuDrivenBoneMatrices.Clear();
        _gpuDrivenBoneInvBindMatrices.Clear();

        uint boneCursor = 0u;
        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            uint baseElement = boneCursor;
            uint elementCount = Math.Max(binding.BoneMatrixElementCount, 1u);

            AppendMatrixRange(binding.Renderer.BoneMatricesBuffer, _gpuDrivenBoneMatrices, elementCount);
            AppendMatrixRange(binding.Renderer.BoneInvBindMatricesBuffer, _gpuDrivenBoneInvBindMatrices, elementCount);

            for (int mappingIndex = 0; mappingIndex < binding.Mappings.Length; ++mappingIndex)
            {
                GPUDrivenBoneMappingData mapping = binding.Mappings[mappingIndex];
                mapping.ParticleIndex += binding.ParticleBaseOffset;
                if (mapping.ChildParticleIndex >= 0)
                    mapping.ChildParticleIndex += binding.ParticleBaseOffset;
                mapping.BoneMatrixIndex += (int)baseElement;
                _gpuDrivenBoneMappings.Add(mapping);
            }

            boneCursor += elementCount;
        }

        bool mappingsResized = EnsureBufferCapacity(
            ref _gpuDrivenBonePaletteMappingsBuffer,
            "PhysicsChainGlobalBonePaletteMappings",
            (uint)Math.Max(_gpuDrivenBoneMappings.Count, 1),
            8);
        bool matricesResized = EnsureBufferCapacity(
            ref _gpuDrivenBoneMatricesBuffer,
            "PhysicsChainGlobalBoneMatrices",
            (uint)Math.Max(_gpuDrivenBoneMatrices.Count, 1),
            16);
        bool invBindMatricesResized = EnsureBufferCapacity(
            ref _gpuDrivenBoneInvBindMatricesBuffer,
            "PhysicsChainGlobalBoneInvBindMatrices",
            (uint)Math.Max(_gpuDrivenBoneInvBindMatrices.Count, 1),
            16);

        uint mappingBytes = _gpuDrivenBonePaletteMappingsBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuDrivenBoneMappings)) ?? 0u;
        PushBufferUpdate(_gpuDrivenBonePaletteMappingsBuffer, mappingsResized, mappingBytes);

        uint matrixBytes = _gpuDrivenBoneMatricesBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuDrivenBoneMatrices)) ?? 0u;
        PushBufferUpdate(_gpuDrivenBoneMatricesBuffer, matricesResized, matrixBytes);

        uint invBindBytes = _gpuDrivenBoneInvBindMatricesBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuDrivenBoneInvBindMatrices)) ?? 0u;
        PushBufferUpdate(_gpuDrivenBoneInvBindMatricesBuffer, invBindMatricesResized, invBindBytes);

        boneCursor = 0u;
        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            uint elementCount = Math.Max(binding.BoneMatrixElementCount, 1u);
            binding.Renderer.SetGpuDrivenBoneMatrixSource(
                binding.Component,
                _gpuDrivenBoneMatricesBuffer!,
                _gpuDrivenBoneInvBindMatricesBuffer!,
                boneCursor,
                elementCount);
            boneCursor += elementCount;
        }

        _gpuDrivenBonePaletteSignature = signature;
    }

    private static unsafe void AppendMatrixRange(XRDataBuffer? source, List<Matrix4x4> destination, uint elementCount)
    {
        int start = destination.Count;
        for (uint i = 0; i < elementCount; ++i)
            destination.Add(Matrix4x4.Identity);

        if (source?.ClientSideSource is null || source.Address == VoidPtr.Zero)
            return;

        uint copyCount = Math.Min(elementCount, source.ElementCount);
        if (copyCount == 0u)
            return;

        Span<Matrix4x4> destinationSpan = CollectionsMarshal.AsSpan(destination).Slice(start, (int)copyCount);
        fixed (Matrix4x4* destinationPtr = destinationSpan)
            Memory.Move(destinationPtr, source.Address, copyCount * (uint)Unsafe.SizeOf<Matrix4x4>());
    }

    private static void ClearBatchedGpuDrivenBonePaletteSources(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        for (int i = 0; i < requests.Count; ++i)
            requests[i].Component.ClearBatchedGpuDrivenBonePaletteSources();
    }

    #endregion

    #region Dispatch

    private void ProcessDispatchGroups(OpenGLRenderer renderer)
    {
        bool canUseBatchedBonePalette = HasSingleDispatchGroup(_activeRequests);
        if (!canUseBatchedBonePalette)
            ClearBatchedGpuDrivenBonePaletteSources(_activeRequests);

        for (int groupStart = 0; groupStart < _activeRequests.Count;)
        {
            GPUDispatchGroupKey key = GPUDispatchGroupKey.From(_activeRequests[groupStart]);
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

            BuildCombinedBuffers(renderer, _dispatchGroup);
            Interlocked.Increment(ref s_dispatchGroupCount);

            if (key.SkipUpdate)
            {
                DispatchSkipUpdate(_dispatchGroup[0]);
                Interlocked.Increment(ref s_dispatchIterationCount);
            }
            else
            {
                int maxIterations = Math.Clamp(key.LoopCount, 1, MaxIterationsPerFrame);
                for (int iteration = 0; iteration < maxIterations; ++iteration)
                {
                    DispatchMainPhysics(_dispatchGroup[0], iteration == 0);
                    Interlocked.Increment(ref s_dispatchIterationCount);
                    if (iteration < maxIterations - 1)
                        renderer.RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
                }
            }

            renderer.RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] ProcessDispatchGroups: Publishing bone matrices for {_dispatchGroup.Count} components. TotalParticles={TotalParticleCount}, ParticlesBufferHash={_particlesBuffer?.GetHashCode():X}, TransformsBufferHash={_transformMatricesBuffer?.GetHashCode():X}");
            bool batchedBonePalettePublished = canUseBatchedBonePalette && PublishBatchedGpuDrivenBoneMatrices(_dispatchGroup);
            for (int requestIndex = 0; requestIndex < _dispatchGroup.Count; ++requestIndex)
            {
                GPUPhysicsChainRequest request = _dispatchGroup[requestIndex];
                if (request.Component.HasGpuDrivenRenderers)
                {
                    if (VerboseLogging)
                        XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] ProcessDispatchGroups: Calling PublishGpuDrivenBoneMatrices. RequestIndex={requestIndex}, RequestId={request.RequestId}, ComponentHash={request.Component.GetHashCode():X}, ParticleOffset={request.ParticleOffset}, ParticleCount={request.Particles.Count}");
                    request.Component.PublishGpuDrivenBoneMatrices(
                        _particlesBuffer,
                        _transformMatricesBuffer,
                        request.ParticleOffset,
                        includeCompletePalettes: !batchedBonePalettePublished);
                }
            }

            renderer.RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
            QueueAsyncReadback(renderer, _dispatchGroup);
            groupStart = groupEnd;
        }
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

    private static ulong ComputeParticleLayoutSignature(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        const ulong offsetBasis = 1469598103934665603UL;
        const ulong prime = 1099511628211UL;

        ulong signature = offsetBasis;
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            signature ^= (uint)request.RequestId;
            signature *= prime;
            signature ^= (uint)request.Particles.Count;
            signature *= prime;
        }

        return signature;
    }

    private static bool HasCpuParticleStateChanges(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            if (!request.ResidentParticlesInitialized || request.CombinedParticleStateVersion != request.ParticleStateVersion)
                return true;
        }

        return false;
    }

    private void MarkAuthoritativeParticleGroup(IReadOnlyList<GPUPhysicsChainRequest> requests, ulong layoutSignature)
    {
        _authoritativeParticleRequests.Clear();
        _authoritativeParticleLayoutSignature = layoutSignature;
        unchecked
        {
            ++_authoritativeCombinedGeneration;
        }

        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            request.ActiveCombinedGeneration = _authoritativeCombinedGeneration;
            request.CombinedParticleStateVersion = request.ParticleStateVersion;
            _authoritativeParticleRequests.Add(request);
        }
    }

    private void SyncAuthoritativeParticlesToResidents(OpenGLRenderer renderer)
    {
        if (VerboseLogging)
            XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] SyncAuthoritativeParticlesToResidents: BEGIN. AuthoritativeCount={_authoritativeParticleRequests.Count}");
        if (_authoritativeParticleRequests.Count > 0)
            CopyCombinedParticlesBackToResidentBuffers(renderer, _authoritativeParticleRequests);

        for (int i = 0; i < _authoritativeParticleRequests.Count; ++i)
            _authoritativeParticleRequests[i].ActiveCombinedGeneration = -1;

        _authoritativeParticleRequests.Clear();
        _authoritativeParticleLayoutSignature = 0UL;
        if (VerboseLogging)
            XREngine.Debug.Out("[GPUPhysicsChainDispatcher] SyncAuthoritativeParticlesToResidents: END. Cleared authoritative group.");
    }

    private void RemoveRequestFromAuthoritativeGroup(GPUPhysicsChainRequest request)
    {
        if (request.ActiveCombinedGeneration != _authoritativeCombinedGeneration)
        {
            if (VerboseLogging)
                XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] RemoveRequestFromAuthoritativeGroup: Request not in authoritative group (generation mismatch). RequestId={request.RequestId}, ComponentHash={request.Component.GetHashCode():X}, RequestGen={request.ActiveCombinedGeneration}, AuthGen={_authoritativeCombinedGeneration}");
            return;
        }

        request.ActiveCombinedGeneration = -1;
        _authoritativeParticleRequests.Remove(request);
        if (VerboseLogging)
            XREngine.Debug.Out($"[GPUPhysicsChainDispatcher] RemoveRequestFromAuthoritativeGroup: Removed from authoritative group. RequestId={request.RequestId}, ComponentHash={request.Component.GetHashCode():X}, RemainingInAuthGroup={_authoritativeParticleRequests.Count}");
        if (_authoritativeParticleRequests.Count == 0)
        {
            _authoritativeParticleLayoutSignature = 0UL;
            if (VerboseLogging)
                XREngine.Debug.Out("[GPUPhysicsChainDispatcher] RemoveRequestFromAuthoritativeGroup: Authoritative group is now EMPTY. Signature reset to 0.");
        }
    }

    private static int CompareRequestsByDispatchGroup(GPUPhysicsChainRequest left, GPUPhysicsChainRequest right)
    {
        int compare = CompareKeys(GPUDispatchGroupKey.From(left), GPUDispatchGroupKey.From(right));
        if (compare != 0)
            return compare;

        return left.RequestId.CompareTo(right.RequestId);
    }

    private static int CompareKeys(in GPUDispatchGroupKey left, in GPUDispatchGroupKey right)
    {
        int compare = left.DispatchIsolationKey.CompareTo(right.DispatchIsolationKey);
        if (compare != 0)
            return compare;

        compare = left.SkipUpdate.CompareTo(right.SkipUpdate);
        if (compare != 0)
            return compare;

        return left.LoopCount.CompareTo(right.LoopCount);
    }

    private void ReadbackSynchronously(OpenGLRenderer renderer, IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        using var profilerState = Engine.Profiler.Start("GPUPhysicsChainDispatcher.ReadbackSynchronously");

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

        renderer.RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

        GPUParticleData[] readback = ArrayPool<GPUParticleData>.Shared.Rent(Math.Max(TotalParticleCount, 1));
        try
        {
            ReadParticleDataFromBuffer(_particlesBuffer, readback.AsSpan(0, TotalParticleCount), renderer);

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

    private void QueueAsyncReadback(OpenGLRenderer renderer, IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (_particlesBuffer is null || requests.Count == 0)
            return;

        int readbackRequestCount = 0;
        for (int i = 0; i < requests.Count; ++i)
            if (requests[i].Component.RequiresGpuReadback())
                ++readbackRequestCount;

        if (readbackRequestCount == 0)
            return;

        XRDataBuffer readbackBuffer = AcquireReadbackBuffer((uint)TotalParticleCount);
        if (!TryGetBufferIdForGpuCopy(renderer, _particlesBuffer, out uint sourceBufferId)
            || !TryGetBufferIdForGpuCopy(renderer, readbackBuffer, out uint destinationBufferId))
        {
            _readbackBufferPool.Push(readbackBuffer);
            NotifyAsyncReadbackUnavailable(requests, "batched-readback-buffer");
            return;
        }

        nuint byteCount = (nuint)(Math.Max(TotalParticleCount, 0) * ParticleStateSizeBytes);
        if (byteCount > 0)
        {
            if (!ValidateGpuCopyRange(_particlesBuffer, 0, readbackBuffer, 0, byteCount, "combined->readback", 0))
            {
                _readbackBufferPool.Push(readbackBuffer);
                NotifyAsyncReadbackUnavailable(requests, "batched-readback-range");
                return;
            }

            renderer.RawGL.CopyNamedBufferSubData(sourceBufferId, destinationBufferId, 0, 0, byteCount);
            RecordGpuCopyBytes((long)byteCount, isBatched: true);
        }

        IntPtr fence = renderer.RawGL.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        if (fence == IntPtr.Zero)
        {
            _readbackBufferPool.Push(readbackBuffer);
            NotifyAsyncReadbackUnavailable(requests, "batched-readback-fence");
            return;
        }

        var snapshots = new GPUReadbackRequestSnapshot[readbackRequestCount];
        int snapshotIndex = 0;
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            if (!request.Component.RequiresGpuReadback())
                continue;

            snapshots[snapshotIndex++] = new GPUReadbackRequestSnapshot(
                request.Component,
                request.ParticleOffset,
                request.Particles.Count,
                request.ExecutionGeneration,
                request.SubmissionId);
        }

        _inFlight.Add(new InFlightDispatch(readbackBuffer, fence, TotalParticleCount, snapshots));
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

    private void DispatchMainPhysics(GPUPhysicsChainRequest request, bool applyObjectMove)
    {
        if (_mainPhysicsProgram is null || _particlesBuffer is null)
            return;
        if (TotalParticleCount <= 0)
            return;

        _mainPhysicsProgram.Uniform("ApplyObjectMove", applyObjectMove ? 1 : 0);
        _mainPhysicsProgram.Uniform("ParticleCount", TotalParticleCount);

        _mainPhysicsProgram.BindBuffer(_particlesBuffer, 0);
        _mainPhysicsProgram.BindBuffer(_particleStaticBuffer!, 1);
        _mainPhysicsProgram.BindBuffer(_transformMatricesBuffer!, 3);
        _mainPhysicsProgram.BindBuffer(_collidersBuffer!, 4);
        _mainPhysicsProgram.BindBuffer(_perTreeParamsBuffer!, 5);

        uint threadGroupsX = ((uint)TotalParticleCount + LocalSizeX - 1) / LocalSizeX;
        _mainPhysicsProgram.DispatchCompute(threadGroupsX, 1, 1);
    }

    private void DispatchSkipUpdate(GPUPhysicsChainRequest request)
    {
        if (_skipUpdateProgram is null || _particlesBuffer is null)
            return;
        if (TotalParticleCount <= 0)
            return;

        _skipUpdateProgram.Uniform("ParticleCount", TotalParticleCount);
        _skipUpdateProgram.BindBuffer(_particlesBuffer, 0);
        _skipUpdateProgram.BindBuffer(_particleStaticBuffer!, 1);
        _skipUpdateProgram.BindBuffer(_transformMatricesBuffer!, 3);
        _skipUpdateProgram.BindBuffer(_perTreeParamsBuffer!, 5);

        uint threadGroupsX = ((uint)TotalParticleCount + LocalSizeX - 1) / LocalSizeX;
        _skipUpdateProgram.DispatchCompute(threadGroupsX, 1, 1);
    }

    #endregion

    #region Readback

    private static unsafe void ReadParticleDataFromBuffer(XRDataBuffer buffer, Span<GPUParticleData> readback, OpenGLRenderer renderer)
    {
        if (readback.IsEmpty)
            return;

        if (!TryGetBufferId(buffer, out uint bufferId))
            return;

        nuint byteSize = (nuint)(readback.Length * Unsafe.SizeOf<GPUParticleData>());
        fixed (GPUParticleData* ptr = readback)
        {
            renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, bufferId);
            renderer.RawGL.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, byteSize, ptr);
            renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
        }

        RecordCpuReadbackBytes((long)byteSize, isBatched: true);
    }

    private static void DistributeReadbackToComponents(IReadOnlyList<GPUReadbackRequestSnapshot> requests, GPUParticleData[] readbackData, int particleCount)
    {
        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUReadbackRequestSnapshot request = requests[requestIndex];
            if (request.ParticleOffset + request.ParticleCount > particleCount)
                continue;

            var segment = new ArraySegment<GPUParticleData>(readbackData, request.ParticleOffset, request.ParticleCount);
            request.Component.ApplyReadbackData(segment, request.ExecutionGeneration, request.SubmissionId);
        }
    }

    private static bool IsFenceComplete(IntPtr fence, OpenGLRenderer renderer)
    {
        if (fence == IntPtr.Zero)
            return false;

        GLEnum status = renderer.RawGL.ClientWaitSync(fence, 0u, 0u);
        return status == GLEnum.AlreadySignaled || status == GLEnum.ConditionSatisfied;
    }

    #endregion

    #region Lifecycle

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!_enabled)
            Reset();
    }

    public void Reset()
    {
        _activeRequests.Clear();
        _dispatchGroup.Clear();

        var renderer = AbstractRenderer.Current as OpenGLRenderer;
        for (int i = 0; i < _inFlight.Count; ++i)
        {
            InFlightDispatch entry = _inFlight[i];
            if (renderer is not null && entry.Fence != IntPtr.Zero)
                renderer.RawGL.DeleteSync(entry.Fence);
            _readbackBufferPool.Push(entry.ReadbackBuffer);
        }

        _inFlight.Clear();

        lock (_registeredComponentsSync)
        {
            for (int i = 0; i < _registeredComponentSnapshot.Count; ++i)
            {
                GPUPhysicsChainRequest request = _registeredComponentSnapshot[i];
                request.Component.ClearBatchedGpuDrivenBonePaletteSources();
                DisposeResidentParticlesBuffer(request);
                request.NeedsUpdate = false;
            }
        }

        _gpuDrivenPaletteBindings.Clear();
        _gpuDrivenBoneMappings.Clear();
        _gpuDrivenBoneMatrices.Clear();
        _gpuDrivenBoneInvBindMatrices.Clear();
        _gpuDrivenBonePaletteSignature = int.MinValue;
    }

    public void Dispose()
    {
        Reset();

        _particlesBuffer?.Dispose();
        _particleStaticBuffer?.Dispose();
        _transformMatricesBuffer?.Dispose();
        _collidersBuffer?.Dispose();
        _perTreeParamsBuffer?.Dispose();
        _gpuDrivenBonePaletteMappingsBuffer?.Dispose();
        _gpuDrivenBoneMatricesBuffer?.Dispose();
        _gpuDrivenBoneInvBindMatricesBuffer?.Dispose();
        _particlesBuffer = null;
        _particleStaticBuffer = null;
        _transformMatricesBuffer = null;
        _collidersBuffer = null;
        _perTreeParamsBuffer = null;
        _gpuDrivenBonePaletteMappingsBuffer = null;
        _gpuDrivenBoneMatricesBuffer = null;
        _gpuDrivenBoneInvBindMatricesBuffer = null;
        _authoritativeParticleRequests.Clear();
        _authoritativeParticleLayoutSignature = 0UL;
        _staticParticleSignature = int.MinValue;
        _transformSignature = int.MinValue;
        _colliderSignature = int.MinValue;
        _gpuDrivenBonePaletteSignature = int.MinValue;

        while (_readbackBufferPool.Count > 0)
            _readbackBufferPool.Pop().Dispose();

        _mainPhysicsProgram?.Destroy();
        _skipUpdateProgram?.Destroy();
        _gpuBonePaletteProgram?.Destroy();
        _gpuBonePaletteShader?.Destroy();
        _mainPhysicsProgram = null;
        _skipUpdateProgram = null;
        _gpuBonePaletteProgram = null;
        _mainPhysicsShader = null;
        _skipUpdateShader = null;
        _gpuBonePaletteShader = null;

        lock (_registeredComponentsSync)
        {
            for (int i = 0; i < _registeredComponentSnapshot.Count; ++i)
                DisposeResidentParticlesBuffer(_registeredComponentSnapshot[i]);

            _registeredComponentSnapshot.Clear();
        }

        _registeredComponents.Clear();
        _initialized = false;
    }

    private static int ComputeStaticParticleSignature(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        var hash = new HashCode();
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            hash.Add(request.RequestId);
            hash.Add(request.StaticDataVersion);
            hash.Add(request.Particles.Count);
            hash.Add(request.ParticleStaticData.Count);
        }

        return hash.ToHashCode();
    }

    private static int ComputeColliderSignature(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        var hash = new HashCode();
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            hash.Add(request.RequestId);
            hash.Add(request.Colliders.Count);
            hash.Add(request.ColliderDataSignature);
        }

        return hash.ToHashCode();
    }

    private static int ComputeTransformSignature(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        var hash = new HashCode();
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            hash.Add(request.RequestId);
            hash.Add(request.Transforms.Count);
            hash.Add(request.TransformDataSignature);
        }

        return hash.ToHashCode();
    }

    #endregion
}

/// <summary>
/// Represents a registered GPU physics chain component's data for batched processing.
/// </summary>
public class GPUPhysicsChainRequest(PhysicsChainComponent component)
{
    private static int s_nextRequestId;

    public PhysicsChainComponent Component { get; } = component;
    public int RequestId { get; } = Interlocked.Increment(ref s_nextRequestId);

    // Per-component particle data — backed by request-owned snapshot arrays.
    // SubmitData copies from the component's live lists into these arrays so the
    // render thread never holds references to mutable collections that can be
    // cleared during component deactivation on the update thread.
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleData> Particles { get; set; } = [];
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleStaticData> ParticleStaticData { get; set; } = [];
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleTreeData> Trees { get; set; } = [];
    public IReadOnlyList<Matrix4x4> Transforms { get; set; } = [];
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUColliderData> Colliders { get; set; } = [];

    // Snapshot backing arrays — grow-only; shared between SubmitData (write) and
    // the IReadOnlyList properties above (read). See SnapshotInto<T>.
    internal GPUPhysicsChainDispatcher.GPUParticleData[] _particlesBacking = [];
    internal GPUPhysicsChainDispatcher.GPUParticleStaticData[] _particleStaticBacking = [];
    internal GPUPhysicsChainDispatcher.GPUParticleTreeData[] _treesBacking = [];
    internal Matrix4x4[] _transformsBacking = [];
    internal GPUPhysicsChainDispatcher.GPUColliderData[] _collidersBacking = [];

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
    public int ParticleStateVersion;
    public int TransformDataSignature;
    public int ColliderDataSignature;
    public bool NeedsUpdate;
    public bool SkipUpdate;

    // Offsets into combined buffers
    public int ParticleOffset;
    public int TreeOffset;
    public int ColliderOffset;
    public int LastKnownParticleCount;
    public XRDataBuffer? ResidentParticlesBuffer;
    public int ResidentParticleCapacity;
    public int ResidentParticleStateVersion = int.MinValue;
    public int CombinedParticleStateVersion = int.MinValue;
    public int ActiveCombinedGeneration = -1;
    public int ActiveTransformSignature = int.MinValue;
    public int ActiveColliderSignature = int.MinValue;
    public bool ResidentParticlesInitialized;
    public XRDataBuffer? ResidentParticleStaticBuffer;
    public int ResidentParticleStaticCapacity;
    public int ResidentStaticDataVersion = int.MinValue;
}

internal readonly record struct GPUDispatchGroupKey(
    int DispatchIsolationKey,
    bool SkipUpdate,
    int LoopCount)
{
    public static GPUDispatchGroupKey From(GPUPhysicsChainRequest request)
        => new(
            request.DispatchIsolationKey,
            request.SkipUpdate,
            request.LoopCount);
}

internal readonly record struct GPUReadbackRequestSnapshot(
    PhysicsChainComponent Component,
    int ParticleOffset,
    int ParticleCount,
    int ExecutionGeneration,
    long SubmissionId);

internal readonly record struct InFlightDispatch(
    XRDataBuffer ReadbackBuffer,
    IntPtr Fence,
    int ParticleCount,
    GPUReadbackRequestSnapshot[] Requests);
