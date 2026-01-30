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
    private readonly List<GPUPhysicsChainRequest> _activeRequests = [];

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

    // Async readback
    private IntPtr _gpuFence = IntPtr.Zero;
    private bool _readbackPending;
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
    public void Register(GPUPhysicsChainComponent component)
    {
        var request = new GPUPhysicsChainRequest(component);
        _registeredComponents.TryAdd(component.GetHashCode(), request);
    }

    /// <summary>
    /// Unregisters a GPU physics chain component.
    /// </summary>
    public void Unregister(GPUPhysicsChainComponent component)
    {
        _registeredComponents.TryRemove(component.GetHashCode(), out _);
    }

    /// <summary>
    /// Checks if a component is registered for batched processing.
    /// </summary>
    public bool IsRegistered(GPUPhysicsChainComponent component)
        => _registeredComponents.ContainsKey(component.GetHashCode());

    #endregion

    #region Main Processing

    /// <summary>
    /// Submits particle and collider data for a component. Called during component's Prepare phase.
    /// </summary>
    public void SubmitData(
        GPUPhysicsChainComponent component,
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

        // First, apply any pending readback from previous frame
        TryApplyAsyncReadback(renderer);

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

        // Determine max iterations needed
        int maxIterations = 1;
        foreach (var req in _activeRequests)
        {
            if (!req.SkipUpdate && req.LoopCount > maxIterations)
                maxIterations = Math.Min(req.LoopCount, MaxIterationsPerFrame);
        }

        // Build combined buffers
        BuildCombinedBuffers();

        // Dispatch iterations
        for (int iter = 0; iter < maxIterations; iter++)
        {
            DispatchMainPhysics(renderer, iter == 0);

            // Memory barrier between iterations
            if (iter < maxIterations - 1)
                renderer.RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        }

        // Handle skip-update components (those with loopCount == 0)
        bool hasSkipUpdates = _activeRequests.Any(r => r.SkipUpdate);
        if (hasSkipUpdates)
            DispatchSkipUpdate(renderer);

        // Request async readback
        RequestAsyncReadback(renderer);

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

        _mainPhysicsShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain.comp", EShaderType.Compute);
        _skipUpdateShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/SkipUpdateParticles.comp", EShaderType.Compute);

        _mainPhysicsProgram = new XRRenderProgram(true, false, _mainPhysicsShader);
        _skipUpdateProgram = new XRRenderProgram(true, false, _skipUpdateShader);

        _initialized = true;
    }

    private void BuildCombinedBuffers()
    {
        _allParticles.Clear();
        _allParticleTrees.Clear();
        _allTransformMatrices.Clear();
        _allColliders.Clear();

        int particleOffset = 0;
        int treeOffset = 0;
        int colliderOffset = 0;

        foreach (var request in _activeRequests)
        {
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
    }

    private static void EnsureBufferCapacity(ref XRDataBuffer? buffer, string name, uint elementCount, uint componentCount)
    {
        if (elementCount == 0)
            elementCount = 1;

        if (buffer is null || buffer.ElementCount < elementCount)
        {
            buffer?.Dispose();
            buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, elementCount, EComponentType.Float, componentCount, false, false);
            buffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent;
            buffer.RangeFlags |= EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
            buffer.DisposeOnPush = false;
            buffer.Usage = EBufferUsage.DynamicDraw;
        }
    }

    #endregion

    #region Dispatch

    private void DispatchMainPhysics(OpenGLRenderer renderer, bool applyObjectMove)
    {
        if (_mainPhysicsProgram is null || _particlesBuffer is null)
            return;

        // For batched dispatch, we use a simplified approach:
        // All components share the same buffers, but we set per-component uniforms
        // In practice, since all data is combined, we use averaged/common parameters

        // Get representative parameters from first active request
        var firstReq = _activeRequests.FirstOrDefault();
        if (firstReq is null)
            return;

        _mainPhysicsProgram.Uniform("DeltaTime", firstReq.TimeVar);
        _mainPhysicsProgram.Uniform("ObjectScale", firstReq.ObjectScale);
        _mainPhysicsProgram.Uniform("Weight", firstReq.Weight);
        _mainPhysicsProgram.Uniform("Force", new Vector4(firstReq.Force, 0));
        _mainPhysicsProgram.Uniform("Gravity", new Vector4(firstReq.Gravity, 0));
        _mainPhysicsProgram.Uniform("ObjectMove", applyObjectMove ? new Vector4(firstReq.ObjectMove, 0) : Vector4.Zero);
        _mainPhysicsProgram.Uniform("FreezeAxis", firstReq.FreezeAxis);
        _mainPhysicsProgram.Uniform("ColliderCount", TotalColliderCount);

        _mainPhysicsProgram.BindBuffer(_particlesBuffer, 0);
        _mainPhysicsProgram.BindBuffer(_particleTreesBuffer!, 1);
        _mainPhysicsProgram.BindBuffer(_transformMatricesBuffer!, 2);
        _mainPhysicsProgram.BindBuffer(_collidersBuffer!, 3);

        uint threadGroupsX = ((uint)TotalParticleCount + LocalSizeX - 1) / LocalSizeX;
        _mainPhysicsProgram.DispatchCompute(threadGroupsX, 1, 1);
    }

    private void DispatchSkipUpdate(OpenGLRenderer renderer)
    {
        if (_skipUpdateProgram is null || _particlesBuffer is null)
            return;

        var firstReq = _activeRequests.FirstOrDefault(r => r.SkipUpdate);
        if (firstReq is null)
            return;

        _skipUpdateProgram.Uniform("ObjectMove", new Vector4(firstReq.ObjectMove, 0));
        _skipUpdateProgram.Uniform("Weight", firstReq.Weight);

        _skipUpdateProgram.BindBuffer(_particlesBuffer, 0);
        _skipUpdateProgram.BindBuffer(_transformMatricesBuffer!, 2);

        uint threadGroupsX = ((uint)TotalParticleCount + LocalSizeX - 1) / LocalSizeX;
        _skipUpdateProgram.DispatchCompute(threadGroupsX, 1, 1);
    }

    #endregion

    #region Async Readback

    private void RequestAsyncReadback(OpenGLRenderer renderer)
    {
        if (_particlesBuffer is null || TotalParticleCount == 0)
            return;

        // Clean up existing fence
        if (_gpuFence != IntPtr.Zero)
        {
            renderer.RawGL.DeleteSync(_gpuFence);
            _gpuFence = IntPtr.Zero;
        }

        // Ensure buffer is mapped
        if (_particlesBuffer.ActivelyMapping.Count == 0)
            _particlesBuffer.MapBufferData();

        _gpuFence = renderer.RawGL.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);

        if (_readbackData is null || _readbackData.Length != TotalParticleCount)
            _readbackData = new GPUParticleData[TotalParticleCount];

        _readbackPending = true;
    }

    private void TryApplyAsyncReadback(OpenGLRenderer renderer)
    {
        if (!_readbackPending || _gpuFence == IntPtr.Zero || _readbackData is null)
            return;

        var status = renderer.RawGL.ClientWaitSync(_gpuFence, 0u, 0u);
        if (status != GLEnum.AlreadySignaled && status != GLEnum.ConditionSatisfied)
            return;

        renderer.RawGL.DeleteSync(_gpuFence);
        _gpuFence = IntPtr.Zero;
        _readbackPending = false;

        // Read data from GPU
        ReadParticleDataFromBuffer();

        // Distribute results back to components
        DistributeReadbackToComponents();
    }

    private void ReadParticleDataFromBuffer()
    {
        if (_particlesBuffer is null || _readbackData is null)
            return;

        var mappedAddresses = _particlesBuffer.GetMappedAddresses().ToArray();
        if (mappedAddresses.Length == 0 || !mappedAddresses[0].IsValid)
        {
            var clientSource = _particlesBuffer.ClientSideSource;
            if (clientSource is not null)
            {
                uint stride = _particlesBuffer.ElementSize;
                for (int i = 0; i < _readbackData.Length && i < TotalParticleCount; i++)
                    _readbackData[i] = Marshal.PtrToStructure<GPUParticleData>(clientSource.Address[(uint)i, stride]);
            }
            return;
        }

        unsafe
        {
            IntPtr mappedPtr = (IntPtr)mappedAddresses[0].Pointer;
            uint stride = (uint)Marshal.SizeOf<GPUParticleData>();
            for (int i = 0; i < _readbackData.Length && i < TotalParticleCount; i++)
                _readbackData[i] = Marshal.PtrToStructure<GPUParticleData>(mappedPtr + (int)(i * stride));
        }
    }

    private void DistributeReadbackToComponents()
    {
        if (_readbackData is null)
            return;

        foreach (var request in _activeRequests)
        {
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
        var renderer = AbstractRenderer.Current as OpenGLRenderer;
        if (renderer is not null && _gpuFence != IntPtr.Zero)
        {
            renderer.RawGL.DeleteSync(_gpuFence);
            _gpuFence = IntPtr.Zero;
        }

        _readbackPending = false;
        _readbackData = null;
        _activeRequests.Clear();

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
public class GPUPhysicsChainRequest(GPUPhysicsChainComponent component)
{
    public GPUPhysicsChainComponent Component { get; } = component;

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
    public bool NeedsUpdate;
    public bool SkipUpdate;

    // Offsets into combined buffers
    public int ParticleOffset;
    public int TreeOffset;
    public int ColliderOffset;
}
