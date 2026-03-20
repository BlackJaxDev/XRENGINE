using Silk.NET.OpenGL;
using System.Collections.Concurrent;
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

    // Shared GPU resources
    private XRShader? _mainPhysicsShader;
    private XRShader? _skipUpdateShader;
    private XRRenderProgram? _mainPhysicsProgram;
    private XRRenderProgram? _skipUpdateProgram;

    // Combined buffers for all components
    private XRDataBuffer? _particlesBuffer;
    private XRDataBuffer? _particleTreesBuffer;
    private XRDataBuffer? _transformMatricesBuffer;
    private XRDataBuffer? _collidersBuffer;

    // Combined data lists
    private readonly List<GPUParticleData> _allParticles = [];
    private readonly List<GPUParticleTreeData> _allParticleTrees = [];
    private readonly List<Matrix4x4> _allTransformMatrices = [];
    private readonly List<GPUColliderData> _allColliders = [];

    private GPUParticleData[]? _readbackData;

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
        public Vector3 TransformLocalPosition;
        public float _pad3;
        public int ParentIndex;
        public float Damping;
        public float Elasticity;
        public float Stiffness;
        public float Inert;
        public float Friction;
        public float Radius;
        public float BoneLength;
        public int IsColliding;
        public int _pad4;
        public int _pad5;
        public int _pad6;
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
        float timeVar)
    {
        if (!_registeredComponents.TryGetValue(component.GetHashCode(), out var request))
            return;

        request.Particles.Clear();
        request.Particles.AddRange(particles);
        request.Trees.Clear();
        request.Trees.AddRange(trees);
        request.Transforms.Clear();
        request.Transforms.AddRange(transforms);
        request.Colliders.Clear();
        request.Colliders.AddRange(colliders);

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
        request.NeedsUpdate = true;
        request.SkipUpdate = loopCount <= 0;
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

        if (_activeRequests.Count == 0)
            return;

        EnsureInitialized();

        ProcessDispatchGroups(renderer);

        // Mark all requests as processed
        foreach (var req in _activeRequests)
            req.NeedsUpdate = false;
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
            {
                var adjusted = particle;
                if (adjusted.ParentIndex >= 0)
                    adjusted.ParentIndex += particleOffset;
                _allParticles.Add(adjusted);
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
        EnsureBufferCapacity(ref _particlesBuffer, "Particles", (uint)_allParticles.Count, 24); // 24 floats per particle
        EnsureBufferCapacity(ref _particleTreesBuffer, "ParticleTrees", (uint)_allParticleTrees.Count, 28); // 28 floats per tree
        EnsureBufferCapacity(ref _transformMatricesBuffer, "TransformMatrices", (uint)_allTransformMatrices.Count, 16);
        EnsureBufferCapacity(ref _collidersBuffer, "Colliders", (uint)Math.Max(_allColliders.Count, 1), 12);

        // Upload data
        _particlesBuffer?.SetDataRaw(_allParticles);
        _particlesBuffer?.PushData();

        _particleTreesBuffer?.SetDataRaw(_allParticleTrees);
        _particleTreesBuffer?.PushData();

        _transformMatricesBuffer?.SetDataRaw(_allTransformMatrices);
        _transformMatricesBuffer?.PushData();

        _collidersBuffer?.SetDataRaw(_allColliders);
        _collidersBuffer?.PushData();

        if (_readbackData is null || _readbackData.Length != TotalParticleCount)
            _readbackData = new GPUParticleData[TotalParticleCount];
    }

    private static void EnsureBufferCapacity(ref XRDataBuffer? buffer, string name, uint elementCount, uint componentCount)
    {
        if (elementCount == 0)
            elementCount = 1;

        if (buffer is null || buffer.ElementCount < elementCount)
        {
            buffer?.Dispose();
            buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, elementCount, EComponentType.Float, componentCount, false, false);
            buffer.DisposeOnPush = false;
            buffer.Usage = EBufferUsage.DynamicDraw;
        }
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

            renderer.RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
            ReadParticleDataFromBuffer(renderer);
            DistributeReadbackToComponents(_dispatchGroup);
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
        _mainPhysicsProgram.BindBuffer(_particleTreesBuffer!, 1);
        _mainPhysicsProgram.BindBuffer(_transformMatricesBuffer!, 2);
        _mainPhysicsProgram.BindBuffer(_collidersBuffer!, 3);

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
        _skipUpdateProgram.BindBuffer(_transformMatricesBuffer!, 2);

        uint threadGroupsX = ((uint)TotalParticleCount + LocalSizeX - 1) / LocalSizeX;
        _skipUpdateProgram.DispatchCompute(threadGroupsX, 1, 1);
    }

    #endregion

    #region Readback

    private void ReadParticleDataFromBuffer(OpenGLRenderer renderer)
    {
        if (_particlesBuffer is null || _readbackData is null)
            return;

        // Get the GL buffer ID via the API wrapper
        var glBuffer = _particlesBuffer.APIWrappers.FirstOrDefault() as OpenGLRenderer.GLDataBuffer;
        if (glBuffer is null || !glBuffer.TryGetBindingId(out uint bufferId) || bufferId == 0)
            return;

        // Use GetBufferSubData — works with any buffer type without persistent mapping.
        unsafe
        {
            nuint byteSize = (nuint)(_readbackData.Length * Marshal.SizeOf<GPUParticleData>());
            fixed (GPUParticleData* ptr = _readbackData)
            {
                renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, bufferId);
                renderer.RawGL.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, byteSize, ptr);
                renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
            }
        }
    }

    private void DistributeReadbackToComponents(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (_readbackData is null)
            return;

        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            int start = request.ParticleOffset;
            int count = request.Particles.Count;

            if (start + count > _readbackData.Length)
                continue;

            var segment = new ArraySegment<GPUParticleData>(_readbackData, start, count);
            request.Component.ApplyReadbackData(segment);
        }
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
        _readbackData = null;
        _activeRequests.Clear();
        _dispatchGroup.Clear();

        foreach (var kvp in _registeredComponents)
            kvp.Value.NeedsUpdate = false;
    }

    public void Dispose()
    {
        Reset();

        _particlesBuffer?.Dispose();
        _particleTreesBuffer?.Dispose();
        _transformMatricesBuffer?.Dispose();
        _collidersBuffer?.Dispose();

        _mainPhysicsProgram?.Destroy();
        _skipUpdateProgram?.Destroy();

        _registeredComponents.Clear();
        _initialized = false;
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
    public List<GPUPhysicsChainDispatcher.GPUParticleData> Particles { get; } = [];
    public List<GPUPhysicsChainDispatcher.GPUParticleTreeData> Trees { get; } = [];
    public List<Matrix4x4> Transforms { get; } = [];
    public List<GPUPhysicsChainDispatcher.GPUColliderData> Colliders { get; } = [];

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
