using Silk.NET.OpenGL;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Centralized dispatcher that batches all GPU physics chain components into a single compute dispatch per frame.
/// This maximizes GPU parallelization by processing all physics chains in one dispatch.
/// </summary>
public sealed class GPUPhysicsChainDispatcher
{
    private const uint LocalSizeX = 128u;
    private const int MaxIterationsPerFrame = 3;

    private static GPUPhysicsChainDispatcher? _instance;
    public static GPUPhysicsChainDispatcher Instance => _instance ??= new GPUPhysicsChainDispatcher();

    private bool _enabled = true;
    private bool _initialized;

    // Registered components
    private readonly ConcurrentDictionary<int, GPUPhysicsChainRequest> _registeredComponents = new();
    private readonly List<GPUPhysicsChainRequest> _activeRequests = new();
    private readonly List<GPUPhysicsChainRequest> _dispatchGroup = [];
    private readonly List<InFlightDispatch> _inFlight = [];
    private readonly Stack<XRDataBuffer> _readbackBufferPool = new();

    // Shared GPU resources
    private XRShader? _mainPhysicsShader;
    private XRShader? _skipUpdateShader;
    private XRRenderProgram? _mainPhysicsProgram;
    private XRRenderProgram? _skipUpdateProgram;

    // Combined buffers for all components
    private XRDataBuffer? _particlesBuffer;
    private XRDataBuffer? _particleStaticBuffer;
    private XRDataBuffer? _particleTreesBuffer;
    private XRDataBuffer? _transformMatricesBuffer;
    private XRDataBuffer? _collidersBuffer;

    // Combined data lists
    private readonly List<GPUParticleData> _allParticles = [];
    private readonly List<GPUParticleStaticData> _allParticleStaticData = [];
    private readonly List<GPUParticleTreeData> _allParticleTrees = [];
    private readonly List<Matrix4x4> _allTransformMatrices = [];
    private readonly List<GPUColliderData> _allColliders = [];
    private int _staticParticleSignature = int.MinValue;

    // Statistics
    public int RegisteredComponentCount => _registeredComponents.Count;
    public int TotalParticleCount { get; private set; }
    public int TotalColliderCount { get; private set; }

    #region GPU Data Structures

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUParticleData
    {
        public Vector3 Position;
        public float _pad0;
        public Vector3 PrevPosition;
        public float _pad1;
        public Vector3 TransformPosition;
        public float _pad2;
        public int IsColliding;
        public Vector3 _pad3;
        public Vector3 PreviousPhysicsPosition;
        public float _pad4;
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
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUParticleTreeData
    {
        public Vector3 LocalGravity;
        public float _pad0;
        public Vector3 RestGravity;
        public float _pad1;
        public int ParticleStart;
        public int ParticleCount;
        public float _pad2;
        public float _pad3;
        public Matrix4x4 RootWorldToLocal;
        public float BoneTotalLength;
        public int _pad4;
        public int _pad5;
        public int _pad6;
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

    #endregion

    #region Registration

    /// <summary>
    /// Registers a GPU physics chain component for batched processing.
    /// </summary>
    public void Register(PhysicsChainComponent component)
    {
        var request = new GPUPhysicsChainRequest(component);
        _registeredComponents.TryAdd(component.GetHashCode(), request);
    }

    /// <summary>
    /// Unregisters a GPU physics chain component.
    /// </summary>
    public void Unregister(PhysicsChainComponent component)
    {
        _registeredComponents.TryRemove(component.GetHashCode(), out _);
    }

    /// <summary>
    /// Checks if a component is registered for batched processing.
    /// </summary>
    public bool IsRegistered(PhysicsChainComponent component)
        => _registeredComponents.ContainsKey(component.GetHashCode());

    #endregion

    #region Main Processing

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
        int staticDataVersion)
    {
        if (!_registeredComponents.TryGetValue(component.GetHashCode(), out var request))
            return;

        request.Particles = particles;
    request.ParticleStaticData = particleStaticData;
        request.Trees = trees;
        request.Transforms = transforms;
        request.Colliders = colliders;

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
        request.UpdateRate = component.UpdateRate;
        request.DispatchIsolationKey = component.UseBatchedDispatcher ? 0 : component.GetHashCode();
        request.ExecutionGeneration = executionGeneration;
        request.SubmissionId = submissionId;
        request.StaticDataVersion = staticDataVersion;
        request.NeedsUpdate = true;
        request.SkipUpdate = loopCount <= 0;
    }

    public bool TryGetRenderParticleBuffers(PhysicsChainComponent component, out XRDataBuffer? particleBuffer, out XRDataBuffer? particleStaticBuffer, out int particleOffset, out int particleCount)
    {
        particleBuffer = null;
        particleStaticBuffer = null;
        particleOffset = 0;
        particleCount = 0;

        if (!_registeredComponents.TryGetValue(component.GetHashCode(), out GPUPhysicsChainRequest? request) || _particlesBuffer is null || _particleStaticBuffer is null)
            return false;

        particleBuffer = _particlesBuffer;
        particleStaticBuffer = _particleStaticBuffer;
        particleOffset = request.ParticleOffset;
        particleCount = request.Particles.Count;
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
        foreach (var kvp in _registeredComponents)
        {
            if (kvp.Value.NeedsUpdate)
                _activeRequests.Add(kvp.Value);
        }

        _activeRequests.Sort(static (left, right) => left.Component.GetHashCode().CompareTo(right.Component.GetHashCode()));

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
                ReadParticleDataFromBuffer(entry.ReadbackBuffer, entry.ParticleCount, renderer, readback);
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

    private void BuildCombinedBuffers(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        _allParticles.Clear();
        int staticParticleSignature = ComputeStaticParticleSignature(requests);
        bool rebuildStaticParticleData = _particleStaticBuffer is null || _staticParticleSignature != staticParticleSignature;
        if (rebuildStaticParticleData)
            _allParticleStaticData.Clear();
        _allParticleTrees.Clear();
        _allTransformMatrices.Clear();
        _allColliders.Clear();

        int particleOffset = 0;
        int treeOffset = 0;
        int colliderOffset = 0;

        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            request.ParticleOffset = particleOffset;
            request.TreeOffset = treeOffset;
            request.ColliderOffset = colliderOffset;

            // Adjust parent indices to global space
            foreach (var particle in request.Particles)
                _allParticles.Add(particle);

            if (rebuildStaticParticleData)
            {
                foreach (var particleStatic in request.ParticleStaticData)
                {
                    var adjusted = particleStatic;
                    if (adjusted.ParentIndex >= 0)
                        adjusted.ParentIndex += particleOffset;
                    _allParticleStaticData.Add(adjusted);
                }
            }

            // Adjust tree particle starts to global space
            foreach (var tree in request.Trees)
            {
                var adjusted = tree;
                adjusted.ParticleStart += particleOffset;
                _allParticleTrees.Add(adjusted);
            }

            _allTransformMatrices.AddRange(request.Transforms);
            _allColliders.AddRange(request.Colliders);

            particleOffset += request.Particles.Count;
            treeOffset += request.Trees.Count;
            colliderOffset += request.Colliders.Count;
        }

        TotalParticleCount = _allParticles.Count;
        TotalColliderCount = _allColliders.Count;

        // Resize/create buffers as needed
        bool particlesResized = EnsureBufferCapacity(ref _particlesBuffer, "Particles", (uint)_allParticles.Count, 20); // 20 components per particle state (including PreviousPhysicsPosition)
        bool particleStaticResized = EnsureBufferCapacity(ref _particleStaticBuffer, "ParticleStatic", (uint)Math.Max(_allParticles.Count, 1), 12); // 12 components per particle static data
        EnsureBufferCapacity(ref _particleTreesBuffer, "ParticleTrees", (uint)_allParticleTrees.Count, 28); // 28 floats per tree
        EnsureBufferCapacity(ref _transformMatricesBuffer, "TransformMatrices", (uint)_allTransformMatrices.Count, 16);
        EnsureBufferCapacity(ref _collidersBuffer, "Colliders", (uint)Math.Max(_allColliders.Count, 1), 12);

        // Upload data
        _particlesBuffer?.SetDataRaw(_allParticles);
        _particlesBuffer?.PushData();

        if (rebuildStaticParticleData || particleStaticResized || particlesResized)
        {
            _particleStaticBuffer?.SetDataRaw(_allParticleStaticData);
            _particleStaticBuffer?.PushData();
            _staticParticleSignature = staticParticleSignature;
        }

        _particleTreesBuffer?.SetDataRaw(_allParticleTrees);
        _particleTreesBuffer?.PushData();

        _transformMatricesBuffer?.SetDataRaw(_allTransformMatrices);
        _transformMatricesBuffer?.PushData();

        _collidersBuffer?.SetDataRaw(_allColliders);
        _collidersBuffer?.PushData();

    }

    private static bool EnsureBufferCapacity(ref XRDataBuffer? buffer, string name, uint elementCount, uint componentCount)
    {
        if (elementCount == 0)
            elementCount = 1;

        if (buffer is null || buffer.ElementCount < elementCount)
        {
            buffer?.Dispose();
            buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, elementCount, EComponentType.Float, componentCount, false, false);
            buffer.DisposeOnPush = false;
            buffer.Usage = EBufferUsage.DynamicDraw;
            return true;
        }

        return false;
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
            Resizable = false,
        };
        // Allocate the GL buffer on the GPU so CopyNamedBufferSubData has a valid destination
        created.SetDataRaw(new GPUParticleData[elementCount]);
        created.PushData();
        return created;
    }

    #endregion

    #region Dispatch

    private void ProcessDispatchGroups(OpenGLRenderer renderer)
    {
        GPUDispatchGroupKey[] keys = _activeRequests.Select(static request => GPUDispatchGroupKey.From(request)).Distinct().ToArray();
        for (int keyIndex = 0; keyIndex < keys.Length; ++keyIndex)
        {
            GPUDispatchGroupKey key = keys[keyIndex];
            _dispatchGroup.Clear();

            for (int requestIndex = 0; requestIndex < _activeRequests.Count; ++requestIndex)
            {
                GPUPhysicsChainRequest request = _activeRequests[requestIndex];
                if (GPUDispatchGroupKey.From(request) == key)
                    _dispatchGroup.Add(request);
            }

            if (_dispatchGroup.Count == 0)
                continue;

            BuildCombinedBuffers(_dispatchGroup);

            if (key.SkipUpdate)
            {
                DispatchSkipUpdate(_dispatchGroup[0]);
            }
            else
            {
                int maxIterations = Math.Clamp(key.LoopCount, 1, MaxIterationsPerFrame);
                for (int iteration = 0; iteration < maxIterations; ++iteration)
                {
                    DispatchMainPhysics(_dispatchGroup[0], iteration == 0);
                    if (iteration < maxIterations - 1)
                        renderer.RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
                }
            }

            ReadbackSynchronously(renderer, _dispatchGroup);
        }
    }

    private void ReadbackSynchronously(OpenGLRenderer renderer, IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (_particlesBuffer is null || requests.Count == 0)
            return;

        renderer.RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

        GPUParticleData[] readback = ArrayPool<GPUParticleData>.Shared.Rent(Math.Max(TotalParticleCount, 1));
        try
        {
            ReadParticleDataFromBuffer(_particlesBuffer, TotalParticleCount, renderer, readback);

            for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
            {
                GPUPhysicsChainRequest request = requests[requestIndex];
                if (request.ParticleOffset + request.Particles.Count > TotalParticleCount)
                    continue;

                var segment = new ArraySegment<GPUParticleData>(readback, request.ParticleOffset, request.Particles.Count);
                request.Component.ApplyReadbackData(segment, request.ExecutionGeneration, request.SubmissionId);
            }
        }
        finally
        {
            ArrayPool<GPUParticleData>.Shared.Return(readback, clearArray: false);
        }
    }

    private void DispatchMainPhysics(GPUPhysicsChainRequest request, bool applyObjectMove)
    {
        if (_mainPhysicsProgram is null || _particlesBuffer is null)
            return;

        _mainPhysicsProgram.Uniform("DeltaTime", request.TimeVar);
        _mainPhysicsProgram.Uniform("ObjectScale", request.ObjectScale);
        _mainPhysicsProgram.Uniform("Weight", request.Weight);
        _mainPhysicsProgram.Uniform("Force", request.Force);
        _mainPhysicsProgram.Uniform("Gravity", request.Gravity);
        _mainPhysicsProgram.Uniform("ObjectMove", applyObjectMove ? request.ObjectMove : Vector3.Zero);
        _mainPhysicsProgram.Uniform("FreezeAxis", request.FreezeAxis);
        _mainPhysicsProgram.Uniform("ColliderCount", TotalColliderCount);
        _mainPhysicsProgram.Uniform("UpdateMode", request.UpdateMode);
        _mainPhysicsProgram.Uniform("UpdateRate", request.UpdateRate);

        _mainPhysicsProgram.BindBuffer(_particlesBuffer, 0);
        _mainPhysicsProgram.BindBuffer(_particleStaticBuffer!, 1);
        _mainPhysicsProgram.BindBuffer(_particleTreesBuffer!, 2);
        _mainPhysicsProgram.BindBuffer(_transformMatricesBuffer!, 3);
        _mainPhysicsProgram.BindBuffer(_collidersBuffer!, 4);

        uint threadGroupsX = ((uint)TotalParticleCount + LocalSizeX - 1) / LocalSizeX;
        _mainPhysicsProgram.DispatchCompute(threadGroupsX, 1, 1);
    }

    private void DispatchSkipUpdate(GPUPhysicsChainRequest request)
    {
        if (_skipUpdateProgram is null || _particlesBuffer is null)
            return;

        _skipUpdateProgram.Uniform("ObjectMove", request.ObjectMove);
        _skipUpdateProgram.Uniform("Weight", request.Weight);

        _skipUpdateProgram.BindBuffer(_particlesBuffer, 0);
        _skipUpdateProgram.BindBuffer(_particleStaticBuffer!, 1);
        _skipUpdateProgram.BindBuffer(_transformMatricesBuffer!, 3);

        uint threadGroupsX = ((uint)TotalParticleCount + LocalSizeX - 1) / LocalSizeX;
        _skipUpdateProgram.DispatchCompute(threadGroupsX, 1, 1);
    }

    #endregion

    #region Readback

    private static unsafe void ReadParticleDataFromBuffer(XRDataBuffer buffer, int particleCount, OpenGLRenderer renderer, GPUParticleData[] readback)
    {
        if (particleCount == 0)
            return;

        if (buffer.APIWrappers.FirstOrDefault() is not OpenGLRenderer.GLDataBuffer glBuffer
            || !glBuffer.TryGetBindingId(out uint bufferId)
            || bufferId == 0)
            return;

        nuint byteSize = (nuint)(particleCount * Unsafe.SizeOf<GPUParticleData>());
        fixed (GPUParticleData* ptr = readback)
        {
            renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, bufferId);
            renderer.RawGL.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, byteSize, ptr);
            renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
        }
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

        foreach (var kvp in _registeredComponents)
            kvp.Value.NeedsUpdate = false;
    }

    public void Dispose()
    {
        Reset();

        _particlesBuffer?.Dispose();
        _particleStaticBuffer?.Dispose();
        _particleTreesBuffer?.Dispose();
        _transformMatricesBuffer?.Dispose();
        _collidersBuffer?.Dispose();

        while (_readbackBufferPool.Count > 0)
            _readbackBufferPool.Pop().Dispose();

        _mainPhysicsProgram?.Destroy();
        _skipUpdateProgram?.Destroy();

        _registeredComponents.Clear();
        _initialized = false;
    }

    private static int ComputeStaticParticleSignature(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        var hash = new HashCode();
        for (int i = 0; i < requests.Count; ++i)
        {
            GPUPhysicsChainRequest request = requests[i];
            hash.Add(request.Component.GetHashCode());
            hash.Add(request.StaticDataVersion);
            hash.Add(request.Particles.Count);
            hash.Add(request.ParticleStaticData.Count);
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
    public PhysicsChainComponent Component { get; } = component;

    // Per-component particle data
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleData> Particles { get; set; } = [];
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleStaticData> ParticleStaticData { get; set; } = [];
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleTreeData> Trees { get; set; } = [];
    public IReadOnlyList<Matrix4x4> Transforms { get; set; } = [];
    public IReadOnlyList<GPUPhysicsChainDispatcher.GPUColliderData> Colliders { get; set; } = [];

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
    public float UpdateRate;
    public int DispatchIsolationKey;
    public int ExecutionGeneration;
    public long SubmissionId;
    public int StaticDataVersion;
    public bool NeedsUpdate;
    public bool SkipUpdate;

    // Offsets into combined buffers
    public int ParticleOffset;
    public int TreeOffset;
    public int ColliderOffset;
}

internal readonly record struct GPUDispatchGroupKey(
    int DispatchIsolationKey,
    bool SkipUpdate,
    int LoopCount,
    int UpdateMode,
    int FreezeAxis,
    int TimeVarBits,
    int UpdateRateBits,
    int ObjectScaleBits,
    int WeightBits,
    int ForceXBits,
    int ForceYBits,
    int ForceZBits,
    int GravityXBits,
    int GravityYBits,
    int GravityZBits,
    int ObjectMoveXBits,
    int ObjectMoveYBits,
    int ObjectMoveZBits)
{
    public static GPUDispatchGroupKey From(GPUPhysicsChainRequest request)
        => new(
            request.DispatchIsolationKey,
            request.SkipUpdate,
            request.LoopCount,
            request.UpdateMode,
            request.FreezeAxis,
            BitConverter.SingleToInt32Bits(request.TimeVar),
            BitConverter.SingleToInt32Bits(request.UpdateRate),
            BitConverter.SingleToInt32Bits(request.ObjectScale),
            BitConverter.SingleToInt32Bits(request.Weight),
            BitConverter.SingleToInt32Bits(request.Force.X),
            BitConverter.SingleToInt32Bits(request.Force.Y),
            BitConverter.SingleToInt32Bits(request.Force.Z),
            BitConverter.SingleToInt32Bits(request.Gravity.X),
            BitConverter.SingleToInt32Bits(request.Gravity.Y),
            BitConverter.SingleToInt32Bits(request.Gravity.Z),
            BitConverter.SingleToInt32Bits(request.ObjectMove.X),
            BitConverter.SingleToInt32Bits(request.ObjectMove.Y),
            BitConverter.SingleToInt32Bits(request.ObjectMove.Z));
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
