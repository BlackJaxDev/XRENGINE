using Extensions;
using Silk.NET.OpenGL;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using XREngine.Components.Animation;
using XREngine.Scene.Transforms;
using static XREngine.Engine;

namespace XREngine.Components;

/// <summary>
/// GPU-accelerated physics chain component that performs physics calculations on the compute shader
/// </summary>
public class GPUPhysicsChainComponent : XRComponent, IRenderable
{
    #region Properties

    [Description("Transform to use as root for the physics chain")]
    public Transform? Root;

    [Description("Multiple transforms to use as roots for the physics chain")]
    public List<Transform>? Roots;

    [Description("Transforms to exclude from the physics chain")]
    public List<TransformBase>? Exclusions;

    [Description("Transforms or objects used for distance checks")]
    public TransformBase? ReferenceObject;

    [Description("Update mode for physics calculations")]
    public EUpdateMode UpdateMode = EUpdateMode.Default;

    [Range(0, 1)]
    [Description("Blend weight for physics effects")]
    public float BlendWeight = 1.0f;

    [Description("Updates per second, 0 for every frame")]
    public float UpdateRate;

    [Range(0, 1)]
    [Description("Damping factor to reduce velocity")]
    public float Damping = 0.1f;

    [Range(0, 1)]
    [Description("Elasticity factor to pull back to rest position")]
    public float Elasticity = 0.1f;

    [Range(0, 1)]
    [Description("Stiffness factor to maintain initial shape")]
    public float Stiffness = 0.1f;

    [Range(0, 1)]
    [Description("Inertia factor affecting response to parent movement")]
    public float Inert;

    [Range(0, 1)]
    [Description("Friction factor on collisions")]
    public float Friction = 0.5f;

    [Description("Radius of particles for collision")]
    public float Radius = 0.2f;

    [Description("End bone length")]
    public float EndLength;

    [Description("End bone offset")]
    public Vector3 EndOffset;

    [Description("Gravity vector")]
    public Vector3 Gravity = new(0, -9.8f, 0);

    [Description("Additional force vector")]
    public Vector3 Force;

    [Description("Axis to freeze movement on")]
    public EFreezeAxis FreezeAxis;

    [Description("Enable/disable distant objects for optimization")]
    public bool DistantDisable;

    [Description("Distance at which to disable physics")]
    public float DistanceToObject = 20.0f;

    [Description("Run physics calculations on multiple threads")]
    public bool Multithread = true;

    /// <summary>
    /// Optional root bone transform for character locomotion.
    /// When set, physics calculations can be made relative to this transform's movement
    /// instead of pure world space, which is useful for character controllers.
    /// </summary>
    [Description("Root bone transform for character locomotion-relative physics")]
    public TransformBase? RootBone;

    /// <summary>
    /// Controls how much the RootBone's movement affects physics calculations.
    /// 0 = World space (RootBone movement ignored), 1 = Fully relative to RootBone.
    /// This is useful for preventing physics chains from lagging behind when a character
    /// controller moves the character rapidly (e.g., teleporting, dashing).
    /// </summary>
    [Range(0, 1)]
    [Description("How much root bone movement affects physics (0=world space, 1=relative to root)")]
    public float RootInertia = 0.0f;

    /// <summary>
    /// Smooths the velocity applied to physics chains to reduce jitter at high velocities.
    /// 0 = No smoothing (raw velocity), 1 = Maximum smoothing (very dampened response).
    /// This helps prevent violent shaking when the root transform moves very fast.
    /// </summary>
    [Range(0, 1)]
    [Description("Velocity smoothing to reduce jitter at high speeds (0=none, 1=max)")]
    public float VelocitySmoothing = 0.0f;

    // Distribution curves
    public AnimationCurve? DampingDistrib;
    public AnimationCurve? ElasticityDistrib;
    public AnimationCurve? StiffnessDistrib;
    public AnimationCurve? InertDistrib;
    public AnimationCurve? FrictionDistrib;
    public AnimationCurve? RadiusDistrib;

    [Description("Colliders to check for physics interactions")]
    public List<PhysicsChainColliderBase>? Colliders;

    #endregion

    #region Internal Data Structures

    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleData
    {
        public Vector3 Position;
        public Vector3 PrevPosition;
        public Vector3 TransformPosition;
        public Vector3 TransformLocalPosition;
        public int ParentIndex;
        public float Damping;
        public float Elasticity;
        public float Stiffness;
        public float Inert;
        public float Friction;
        public float Radius;
        public float BoneLength;
        public int IsColliding;
        // Padding to ensure alignment
        public float Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleTreeData
    {
        public Vector3 LocalGravity;
        public Vector3 RestGravity;
        public int ParticleStart;
        public int ParticleCount;
        public Matrix4x4 RootWorldToLocal;
        public float BoneTotalLength;
        // Padding to ensure alignment
        public Vector3 Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ColliderData
    {
        public Vector4 Center;  // xyz: position, w: radius (or unused for plane)
        public Vector4 Params;  // Type-specific: sphere=unused, capsule=end.xyz, box=halfext.xyz, plane=normal.xyz+bound.w
        public int Type;        // 0: Sphere, 1: Capsule, 2: Box, 3: Plane
        public Vector3 Padding;
    }

    private class ParticleTree(Transform root)
    {
        public Transform Root = root;
        public Vector3 LocalGravity;
        public Vector3 RestGravity = Vector3.Zero;
        public List<Particle> Particles = [];
        public float BoneTotalLength;
        public Matrix4x4 RootWorldToLocalMatrix => Root.InverseWorldMatrix;
    }

    private class Particle(Transform? transform, int parentIndex)
    {
        public Transform? Transform = transform;
        public int ParentIndex = parentIndex;
        public int ChildCount;
        public Vector3 Position;
        public Vector3 PrevPosition;
        public Vector3 TransformPosition;
        public Vector3 TransformLocalPosition;
        public Matrix4x4 TransformLocalToWorldMatrix;
        public Vector3 InitLocalPosition;
        public Quaternion InitLocalRotation;
        public Vector3 EndOffset;
        public float BoneLength;
        public float Damping;
        public float Elasticity;
        public float Stiffness;
        public float Inert;
        public float Friction;
        public float Radius;
        public bool IsColliding;
    }

    #endregion

    #region Private Fields

    private XRRenderProgram? _mainPhysicsProgram;
    private XRRenderProgram? _skipUpdateParticlesProgram;
    private XRShader? _mainPhysicsShader;
    private XRShader? _skipUpdateParticlesShader;

    private XRDataBuffer? _particlesBuffer;
    private XRDataBuffer? _particleTreesBuffer;
    private XRDataBuffer? _transformMatricesBuffer;
    private XRDataBuffer? _collidersBuffer;
    
    private readonly List<ParticleTree> _particleTrees = [];
    private readonly List<PhysicsChainColliderBase> _effectiveColliders = [];
    private readonly List<ParticleData> _particlesData = [];
    private readonly List<ParticleTreeData> _particleTreesData = [];
    private readonly List<Matrix4x4> _transformMatrices = [];
    private readonly List<ColliderData> _collidersData = [];

    private int _totalParticleCount;
    private int _prepareFrame;
    private int _updateCount;
    private int _preUpdateCount;
    private float _deltaTime;
    private float _time;
    private float _weight;
    private float _objectScale = 1.0f;
    private Vector3 _objectPrevPosition;
    private Vector3 _objectMove;
    private bool _distantDisabled;
    private bool _buffersInitialized;
    
    // Root bone tracking for character locomotion
    private Vector3 _rootBonePrevPosition;
    private Vector3 _smoothedObjectMove;
    
    // Async GPU readback state
    private IntPtr _gpuFence = IntPtr.Zero;
    private bool _readbackPending;
    private ParticleData[]? _readbackData;

    /// <summary>
    /// When true, this component uses the centralized GPUPhysicsChainDispatcher for batched processing.
    /// This improves performance when multiple GPU physics chains are active.
    /// </summary>
    [Description("Use centralized batched dispatcher for better GPU utilization with multiple physics chains")]
    public bool UseBatchedDispatcher { get; set; } = true;

    // RenderInfo3D for executing GPU work on the render thread via PreRender pass
    private RenderInfo3D? _gpuWorkRenderInfo;
    private RenderCommandMethod3D? _gpuWorkRenderCommand;
    
    // Pending GPU work parameters (set on update thread, consumed on render thread)
    private volatile bool _hasPendingGPUWork;
    private int _pendingLoop;
    private float _pendingTimeVar;
    private readonly object _pendingWorkLock = new();

    #endregion

    #region Component Lifecycle

    protected internal override void OnComponentActivated()
    {
        // Only load shaders if not using batched dispatcher
        if (!UseBatchedDispatcher)
        {
            // Load compute shaders
            _mainPhysicsShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain.comp", EShaderType.Compute);
            _skipUpdateParticlesShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/SkipUpdateParticles.comp", EShaderType.Compute);

            // Create render programs
            _mainPhysicsProgram = new XRRenderProgram(true, false, _mainPhysicsShader);
            _skipUpdateParticlesProgram = new XRRenderProgram(true, false, _skipUpdateParticlesShader);
            
            // Create RenderInfo3D with PreRender pass to execute GPU work on the render thread
            _gpuWorkRenderCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.PreRender, ExecutePendingGPUWork);
            _gpuWorkRenderInfo = RenderInfo3D.New(this, _gpuWorkRenderCommand);
        }
        else
        {
            // Register with the batched dispatcher
            Rendering.Compute.GPUPhysicsChainDispatcher.Instance.Register(this);
        }

        SetupParticles();
        RegisterTick(ETickGroup.PostPhysics, ETickOrder.Animation, FixedUpdate);
        RegisterTick(ETickGroup.Normal, ETickOrder.Animation, Update);
        RegisterTick(ETickGroup.Late, ETickOrder.Animation, LateUpdate);
        ResetParticlesPosition();
        InitializeRootBoneTracking();
        OnValidate();
    }

    protected internal override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        
        if (UseBatchedDispatcher)
            Rendering.Compute.GPUPhysicsChainDispatcher.Instance.Unregister(this);
        
        InitTransforms();
        CleanupBuffers();
        CleanupPrograms();
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        OnValidate();
    }

    private void OnValidate()
    {
        UpdateRate = MathF.Max(UpdateRate, 0);
        Damping = Damping.Clamp(0, 1);
        Elasticity = Elasticity.Clamp(0, 1);
        Stiffness = Stiffness.Clamp(0, 1);
        Inert = Inert.Clamp(0, 1);
        Friction = Friction.Clamp(0, 1);
        Radius = MathF.Max(Radius, 0);

        if (!IsEditor || !IsPlaying)
            return;
        
        if (IsRootChanged())
        {
            InitTransforms();
            SetupParticles();
        }
        else
            UpdateParameters();
    }

    #endregion

    #region Update Methods

    private void FixedUpdate()
    {
        if (UpdateMode == EUpdateMode.FixedUpdate)
            PreUpdate();
    }

    private void Update()
    {
        if (UpdateMode != EUpdateMode.FixedUpdate)
            PreUpdate();
        
        ++_updateCount;
    }

    private void LateUpdate()
    {
        if (_preUpdateCount == 0)
            return;

        if (_updateCount > 0)
        {
            _updateCount = 0;
            ++_prepareFrame;
        }

        SetWeight(BlendWeight);
        
        CheckDistance();
        if (IsNeedUpdate())
        {
            Prepare();
            UpdateParticles();
            ApplyParticlesToTransforms();
        }

        _preUpdateCount = 0;
    }

    private void PreUpdate()
    {
        if (IsNeedUpdate())
            InitTransforms();
        
        ++_preUpdateCount;
    }

    private bool IsNeedUpdate()
        => _weight > 0 && !(DistantDisable && _distantDisabled);

    #endregion

    #region Physics Methods

    private void Prepare()
    {
        _deltaTime = Delta;
        switch (UpdateMode)
        {
            case EUpdateMode.Undilated:
                _deltaTime = UndilatedDelta;
                break;
            case EUpdateMode.FixedUpdate:
                _deltaTime = FixedDelta * _preUpdateCount;
                break;
        }

        var translation = Transform.WorldTranslation;
        _objectScale = MathF.Abs(Transform.LossyWorldScale.X);
        
        // Calculate base object movement
        Vector3 rawObjectMove = translation - _objectPrevPosition;
        _objectPrevPosition = translation;

        // Handle root bone relative movement if configured
        if (RootBone is not null && RootInertia > 0.0f)
        {
            RootBone.RecalculateMatrices();
            Vector3 rootBonePos = RootBone.WorldTranslation;
            Vector3 rootBoneMove = rootBonePos - _rootBonePrevPosition;
            _rootBonePrevPosition = rootBonePos;

            // Blend between world-space movement and root-relative movement
            // At RootInertia=1, we subtract the root bone's movement from the chain's perception of movement
            // This makes the chain move "with" the root bone rather than lagging behind
            rawObjectMove -= rootBoneMove * RootInertia;
        }

        // Apply velocity smoothing to reduce jitter at high velocities
        if (VelocitySmoothing > 0.0f)
        {
            // Exponential moving average for smooth velocity
            // Higher smoothing = more dampened response
            float smoothFactor = 1.0f - VelocitySmoothing * 0.9f; // Map 0-1 to 1-0.1
            _smoothedObjectMove = Vector3.Lerp(_smoothedObjectMove, rawObjectMove, smoothFactor);
            _objectMove = _smoothedObjectMove;
        }
        else
        {
            _objectMove = rawObjectMove;
            _smoothedObjectMove = rawObjectMove;
        }

        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            pt.RestGravity = pt.Root.TransformDirection(pt.LocalGravity);

            for (int j = 0; j < pt.Particles.Count; ++j)
            {
                Particle p = pt.Particles[j];
                if (p.Transform is not null)
                {
                    p.TransformPosition = p.Transform.WorldTranslation;
                    p.TransformLocalPosition = p.Transform.LocalTranslation;
                    p.TransformLocalToWorldMatrix = p.Transform.WorldMatrix;
                }
            }
        }

        _effectiveColliders.Clear();

        if (Colliders is null)
            return;
        
        for (int i = 0; i < Colliders.Count; ++i)
        {
            PhysicsChainColliderBase c = Colliders[i];
            if (c is null || !c.IsActive)
                continue;

            _effectiveColliders.Add(c);
            if (c.PrepareFrame == _prepareFrame)
                continue;
            
            c.Prepare();
            c.PrepareFrame = _prepareFrame;
        }
    }

    private void UpdateParticles()
    {
        if (_particleTrees.Count <= 0)
            return;

        int loop = 1;
        float timeVar = 1.0f;
        float dt = _deltaTime;

        // Match CPU behavior for different update modes
        if (UpdateMode == EUpdateMode.Default)
        {
            // Default mode: use frame delta, optionally scaled by UpdateRate
            if (UpdateRate > 0.0f)
                timeVar = dt * UpdateRate;
        }
        else if (UpdateMode == EUpdateMode.FixedUpdate)
        {
            // FixedUpdate mode: use fixed timestep, potentially multiple iterations
            if (UpdateRate > 0.0f)
            {
                float frameTime = 1.0f / UpdateRate;
                _time += dt;
                loop = 0;

                while (_time >= frameTime)
                {
                    _time -= frameTime;
                    if (++loop >= 3) // Cap at 3 iterations to prevent spiral of death
                    {
                        _time = 0;
                        break;
                    }
                }
                
                // Use fixed timestep for each iteration
                timeVar = frameTime;
            }
            else
            {
                // No UpdateRate specified, use the accumulated fixed delta from PreUpdate count
                // This matches CPU behavior where FixedUpdate uses FixedDelta * _preUpdateCount
                timeVar = dt; // dt is already set to FixedDelta * _preUpdateCount in Prepare()
            }
        }
        else if (UpdateMode == EUpdateMode.Undilated)
        {
            // Undilated mode: similar to default but uses undilated delta (already set in Prepare())
            if (UpdateRate > 0.0f)
            {
                float frameTime = 1.0f / UpdateRate;
                _time += dt;
                loop = 0;

                while (_time >= frameTime)
                {
                    _time -= frameTime;
                    if (++loop >= 3)
                    {
                        _time = 0;
                        break;
                    }
                }
                
                timeVar = frameTime;
            }
        }

        // If using batched dispatcher, submit data and let dispatcher handle the rest
        if (UseBatchedDispatcher)
        {
            SubmitToBatchedDispatcher(loop, timeVar);
            return;
        }

        // Standalone mode: store pending work to be executed on render thread via PreRender pass
        lock (_pendingWorkLock)
        {
            _pendingLoop = loop;
            _pendingTimeVar = timeVar;
            _hasPendingGPUWork = true;
        }
    }

    /// <summary>
    /// Called during PreRender pass on the render thread to execute pending GPU physics work.
    /// </summary>
    private void ExecutePendingGPUWork()
    {
        if (!_hasPendingGPUWork)
            return;

        int loop;
        float timeVar;
        lock (_pendingWorkLock)
        {
            if (!_hasPendingGPUWork)
                return;
            
            loop = _pendingLoop;
            timeVar = _pendingTimeVar;
            _hasPendingGPUWork = false;
        }

        ExecuteStandaloneGPUWork(loop, timeVar);
    }

    /// <summary>
    /// Executes the standalone GPU physics work on the render thread.
    /// This must run on the render thread because it makes GL calls.
    /// </summary>
    private void ExecuteStandaloneGPUWork(int loop, float timeVar)
    {
        // Check if component is still active
        if (!IsActive)
            return;

        TryApplyAsyncReadback();

        if (!_buffersInitialized)
            InitializeBuffers();
        else
            UpdateBufferData();

        if (loop > 0)
        {
            // For multiple iterations, we need memory barriers between dispatches
            // to ensure parent particles are updated before children read them
            for (int i = 0; i < loop; ++i)
            {
                DispatchMainPhysics(timeVar, i == 0);
                
                // Insert memory barrier between iterations to ensure coherent reads
                if (i < loop - 1)
                    InsertShaderStorageBarrier();
            }
        }
        else
        {
            DispatchSkipUpdateParticles();
        }

        // Request async readback for next frame
        RequestAsyncReadback();
    }

    private void SubmitToBatchedDispatcher(int loopCount, float timeVar)
    {
        // Prepare GPU data structures for the dispatcher
        PrepareGPUData();

        // Convert to dispatcher's data format
        var particles = new List<Rendering.Compute.GPUPhysicsChainDispatcher.GPUParticleData>();
        foreach (var pd in _particlesData)
        {
            particles.Add(new Rendering.Compute.GPUPhysicsChainDispatcher.GPUParticleData
            {
                Position = pd.Position,
                PrevPosition = pd.PrevPosition,
                TransformPosition = pd.TransformPosition,
                TransformLocalPosition = pd.TransformLocalPosition,
                ParentIndex = pd.ParentIndex,
                Damping = pd.Damping,
                Elasticity = pd.Elasticity,
                Stiffness = pd.Stiffness,
                Inert = pd.Inert,
                Friction = pd.Friction,
                Radius = pd.Radius,
                BoneLength = pd.BoneLength,
                IsColliding = pd.IsColliding
            });
        }

        var trees = new List<Rendering.Compute.GPUPhysicsChainDispatcher.GPUParticleTreeData>();
        foreach (var td in _particleTreesData)
        {
            trees.Add(new Rendering.Compute.GPUPhysicsChainDispatcher.GPUParticleTreeData
            {
                LocalGravity = td.LocalGravity,
                RestGravity = td.RestGravity,
                ParticleStart = td.ParticleStart,
                ParticleCount = td.ParticleCount,
                RootWorldToLocal = td.RootWorldToLocal,
                BoneTotalLength = td.BoneTotalLength
            });
        }

        var colliders = new List<Rendering.Compute.GPUPhysicsChainDispatcher.GPUColliderData>();
        foreach (var cd in _collidersData)
        {
            colliders.Add(new Rendering.Compute.GPUPhysicsChainDispatcher.GPUColliderData
            {
                Center = cd.Center,
                Params = cd.Params,
                Type = cd.Type
            });
        }

        Rendering.Compute.GPUPhysicsChainDispatcher.Instance.SubmitData(
            this,
            particles,
            trees,
            _transformMatrices,
            colliders,
            _deltaTime,
            _objectScale,
            _weight,
            Force,
            Gravity,
            _objectMove,
            (int)FreezeAxis,
            loopCount,
            timeVar);
    }

    /// <summary>
    /// Called by the batched dispatcher to apply readback data to this component's particles.
    /// </summary>
    public void ApplyReadbackData(ArraySegment<Rendering.Compute.GPUPhysicsChainDispatcher.GPUParticleData> readbackData)
    {
        int particleIndex = 0;
        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            for (int j = 0; j < pt.Particles.Count; ++j)
            {
                if (particleIndex < readbackData.Count)
                {
                    var data = readbackData[particleIndex];
                    Particle p = pt.Particles[j];
                    
                    p.Position = data.Position;
                    p.PrevPosition = data.PrevPosition;
                    p.IsColliding = data.IsColliding != 0;
                    
                    particleIndex++;
                }
            }
        }
    }

    /// <summary>
    /// Inserts a shader storage memory barrier to ensure all writes from previous
    /// compute dispatch are visible to subsequent dispatches.
    /// </summary>
    private void InsertShaderStorageBarrier()
    {
        var renderer = AbstractRenderer.Current as OpenGLRenderer;
        renderer?.RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
    }

    private void TryApplyAsyncReadback()
    {
        if (!_readbackPending || _gpuFence == IntPtr.Zero)
            return;

        var renderer = AbstractRenderer.Current as OpenGLRenderer;
        if (renderer is null)
            return;

        // Check if the fence has signaled (non-blocking)
        var status = renderer.RawGL.ClientWaitSync(_gpuFence, 0u, 0u);
        if (status != GLEnum.AlreadySignaled && status != GLEnum.ConditionSatisfied)
            return; // Not ready yet, will try again next frame

        // Fence signaled - apply the readback data
        renderer.RawGL.DeleteSync(_gpuFence);
        _gpuFence = IntPtr.Zero;
        _readbackPending = false;

        if (_readbackData != null && _particlesBuffer != null)
        {
            // Read particle data from the mapped buffer
            ReadParticleDataFromBuffer();
            ApplyReadbackDataToParticles();
        }
    }

    private void ReadParticleDataFromBuffer()
    {
        if (_particlesBuffer is null || _readbackData is null)
            return;

        // Get the mapped address from the buffer
        var mappedAddresses = _particlesBuffer.GetMappedAddresses().ToArray();
        if (mappedAddresses.Length == 0 || !mappedAddresses[0].IsValid)
        {
            // Fall back to client-side source if not mapped
            var clientSource = _particlesBuffer.ClientSideSource;
            if (clientSource is not null)
            {
                uint stride = _particlesBuffer.ElementSize;
                for (int i = 0; i < _readbackData.Length && i < _totalParticleCount; i++)
                {
                    _readbackData[i] = Marshal.PtrToStructure<ParticleData>(clientSource.Address[(uint)i, stride]);
                }
            }
            return;
        }

        // Read from mapped GPU memory
        unsafe
        {
            IntPtr mappedPtr = (IntPtr)mappedAddresses[0].Pointer;
            uint stride = (uint)Marshal.SizeOf<ParticleData>();
            for (int i = 0; i < _readbackData.Length && i < _totalParticleCount; i++)
            {
                _readbackData[i] = Marshal.PtrToStructure<ParticleData>(mappedPtr + (int)(i * stride));
            }
        }
    }

    private void ApplyReadbackDataToParticles()
    {
        if (_readbackData is null)
            return;

        int particleIndex = 0;
        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            for (int j = 0; j < pt.Particles.Count; ++j)
            {
                if (particleIndex < _readbackData.Length)
                {
                    ParticleData data = _readbackData[particleIndex];
                    Particle p = pt.Particles[j];
                    
                    p.Position = data.Position;
                    p.PrevPosition = data.PrevPosition;
                    p.IsColliding = data.IsColliding != 0;
                    
                    particleIndex++;
                }
            }
        }
    }

    private void RequestAsyncReadback()
    {
        if (_particlesBuffer is null)
            return;

        var renderer = AbstractRenderer.Current as OpenGLRenderer;
        if (renderer is null)
            return;

        // Clean up any existing fence
        if (_gpuFence != IntPtr.Zero)
        {
            renderer.RawGL.DeleteSync(_gpuFence);
            _gpuFence = IntPtr.Zero;
        }

        // Ensure the buffer is configured for persistent mapping if not already
        EnsureBufferMappedForReadback(_particlesBuffer);

        // Create a fence to track when the GPU work is done
        _gpuFence = renderer.RawGL.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        
        // Allocate readback data array if needed
        if (_readbackData is null || _readbackData.Length != _totalParticleCount)
            _readbackData = new ParticleData[_totalParticleCount];

        _readbackPending = true;
    }

    private static void EnsureBufferMappedForReadback(XRDataBuffer buffer)
    {
        if (buffer.ActivelyMapping.Count > 0)
            return;

        // Configure buffer for persistent mapped readback
        buffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent;
        buffer.RangeFlags |= EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
        buffer.DisposeOnPush = false;
        buffer.Usage = EBufferUsage.StreamRead;
        buffer.Resizable = false;
        buffer.MapBufferData();
    }

    private void DispatchMainPhysics(float timeVar, bool applyObjectMove)
    {
        if (_mainPhysicsProgram == null || _particlesBuffer == null ||
            _particleTreesBuffer == null || _transformMatricesBuffer == null || _collidersBuffer == null)
            return;

        // Set uniforms
        _mainPhysicsProgram.Uniform("DeltaTime", timeVar);
        _mainPhysicsProgram.Uniform("ObjectScale", _objectScale);
        _mainPhysicsProgram.Uniform("Weight", _weight);
        _mainPhysicsProgram.Uniform("Force", new Vector4(Force.X, Force.Y, Force.Z, 0));
        _mainPhysicsProgram.Uniform("Gravity", new Vector4(Gravity.X, Gravity.Y, Gravity.Z, 0));
        _mainPhysicsProgram.Uniform("ObjectMove", applyObjectMove ? new Vector4(_objectMove.X, _objectMove.Y, _objectMove.Z, 0) : Vector4.Zero);
        _mainPhysicsProgram.Uniform("FreezeAxis", (int)FreezeAxis);
        _mainPhysicsProgram.Uniform("ColliderCount", _collidersData.Count);
        _mainPhysicsProgram.Uniform("UpdateMode", (int)UpdateMode);
        _mainPhysicsProgram.Uniform("UpdateRate", UpdateRate);

        // Bind buffers
        _mainPhysicsProgram.BindBuffer(_particlesBuffer, 0);
        _mainPhysicsProgram.BindBuffer(_particleTreesBuffer, 1);
        _mainPhysicsProgram.BindBuffer(_transformMatricesBuffer, 2);
        _mainPhysicsProgram.BindBuffer(_collidersBuffer, 3);
        
        // Dispatch compute shader
        int threadGroupsX = (_totalParticleCount + 127) / 128;
        _mainPhysicsProgram.DispatchCompute((uint)threadGroupsX, 1, 1);
    }

    private void DispatchSkipUpdateParticles()
    {
        if (_skipUpdateParticlesProgram == null || _particlesBuffer == null || _transformMatricesBuffer == null)
            return;

        // Set uniforms
        _skipUpdateParticlesProgram.Uniform("ObjectMove", new Vector4(_objectMove.X, _objectMove.Y, _objectMove.Z, 0));
        _skipUpdateParticlesProgram.Uniform("Weight", _weight);

        // Bind buffers
        _skipUpdateParticlesProgram.BindBuffer(_particlesBuffer, 0);
        _skipUpdateParticlesProgram.BindBuffer(_transformMatricesBuffer, 2);
        
        // Dispatch compute shader
        int threadGroupsX = (_totalParticleCount + 127) / 128;
        _skipUpdateParticlesProgram.DispatchCompute((uint)threadGroupsX, 1, 1);
    }

    private void InitializeBuffers()
    {
        CleanupBuffers();

        // Prepare GPU data
        PrepareGPUData();

        // Create GPU buffers with proper binding indices
        // Particle: 16 floats (vec3 + float + vec3 + float + vec3 + float + vec3 + float + int + float + float + float + float + float + float + int + int + int + int)
        _particlesBuffer = new XRDataBuffer("Particles", EBufferTarget.ShaderStorageBuffer, (uint)_particlesData.Count, EComponentType.Float, 16, false, false);
        _particlesBuffer.SetBlockIndex(0); // Binding 0
        
        // ParticleTree: 20 floats (vec3 + float + vec3 + float + int + int + float + float + mat4 + float + int + int + int)
        _particleTreesBuffer = new XRDataBuffer("ParticleTrees", EBufferTarget.ShaderStorageBuffer, (uint)_particleTreesData.Count, EComponentType.Float, 20, false, false);
        _particleTreesBuffer.SetBlockIndex(1); // Binding 1
        
        // TransformMatrix: 16 floats (mat4)
        _transformMatricesBuffer = new XRDataBuffer("TransformMatrices", EBufferTarget.ShaderStorageBuffer, (uint)_transformMatrices.Count, EComponentType.Float, 16, false, false);
        _transformMatricesBuffer.SetBlockIndex(2); // Binding 2
        
        // Collider: 16 floats (vec4 + vec4 + int + int + int + int)
        _collidersBuffer = new XRDataBuffer("Colliders", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_collidersData.Count, 1), EComponentType.Float, 16, false, false);
        _collidersBuffer.SetBlockIndex(3); // Binding 3

        // Set initial data
        UpdateBufferData();

        _buffersInitialized = true;
    }

    private void UpdateBufferData()
    {
        // Clear and repopulate data arrays
        PrepareGPUData();

        // Update buffer data
        _particlesBuffer?.SetDataRaw(_particlesData);
        _particleTreesBuffer?.SetDataRaw(_particleTreesData);
        _transformMatricesBuffer?.SetDataRaw(_transformMatrices);
        _collidersBuffer?.SetDataRaw(_collidersData);
    }

    private void PrepareGPUData()
    {
        _particlesData.Clear();
        _particleTreesData.Clear();
        _transformMatrices.Clear();
        _collidersData.Clear();
        _totalParticleCount = 0;

        // Prepare particle tree data
        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            
            var treeData = new ParticleTreeData
            {
                LocalGravity = pt.LocalGravity,
                RestGravity = pt.RestGravity,
                ParticleStart = _totalParticleCount,
                ParticleCount = pt.Particles.Count,
                RootWorldToLocal = pt.RootWorldToLocalMatrix,
                BoneTotalLength = pt.BoneTotalLength
            };
            
            _particleTreesData.Add(treeData);
            
            // Prepare particle data
            for (int j = 0; j < pt.Particles.Count; ++j)
            {
                Particle p = pt.Particles[j];
                var particleData = new ParticleData
                {
                    Position = p.Position,
                    PrevPosition = p.PrevPosition,
                    TransformPosition = p.Transform?.WorldTranslation ?? p.Position,
                    TransformLocalPosition = p.Transform?.LocalTranslation ?? p.EndOffset,
                    ParentIndex = p.ParentIndex >= 0 ? p.ParentIndex + _totalParticleCount : -1,
                    Damping = p.Damping,
                    Elasticity = p.Elasticity,
                    Stiffness = p.Stiffness,
                    Inert = p.Inert,
                    Friction = p.Friction,
                    Radius = p.Radius,
                    BoneLength = p.BoneLength,
                    IsColliding = p.IsColliding ? 1 : 0
                };
                
                _particlesData.Add(particleData);
                
                // Add transform matrix to array
                if (p.Transform != null)
                    _transformMatrices.Add(p.TransformLocalToWorldMatrix);
                else if (p.ParentIndex >= 0)
                    _transformMatrices.Add(pt.Particles[p.ParentIndex].TransformLocalToWorldMatrix);
                else
                    _transformMatrices.Add(Matrix4x4.Identity);
            }
            
            _totalParticleCount += pt.Particles.Count;
        }

        // Prepare collider data
        foreach (var collider in _effectiveColliders)
        {
            if (collider is PhysicsChainSphereCollider sphereCollider)
            {
                _collidersData.Add(new ColliderData
                {
                    Center = new Vector4(
                        sphereCollider.Transform.WorldTranslation,
                        sphereCollider.Radius),
                    Type = 0
                });
            }
            else if (collider is PhysicsChainCapsuleCollider capsuleCollider)
            {
                Vector3 start = capsuleCollider.Transform.WorldTranslation;
                Vector3 end = capsuleCollider.Transform.TransformPoint(new Vector3(0, capsuleCollider.Height, 0));
                
                _collidersData.Add(new ColliderData
                {
                    Center = new Vector4(start, capsuleCollider.Radius),
                    Params = new Vector4(end, 0),
                    Type = 1
                });
            }
            else if (collider is PhysicsChainBoxCollider boxCollider)
            {
                _collidersData.Add(new ColliderData
                {
                    Center = new Vector4(boxCollider.Transform.WorldTranslation, 0),
                    Params = new Vector4(boxCollider.Size * 0.5f, 0),
                    Type = 2
                });
            }
            else if (collider is PhysicsChainPlaneCollider planeCollider)
            {
                // Plane collider uses prepared plane data
                // Center.xyz = point on plane, Params.xyz = plane normal, Params.w = bound (0=Outside, 1=Inside)
                _collidersData.Add(new ColliderData
                {
                    Center = new Vector4(planeCollider.Transform.TransformPoint(planeCollider._center), 0),
                    Params = new Vector4(planeCollider._plane.Normal, planeCollider._bound == PhysicsChainColliderBase.EBound.Inside ? 1.0f : 0.0f),
                    Type = 3
                });
            }
        }
    }

    private void UpdateParticlesFromGPUData()
    {
        int particleIndex = 0;
        
        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            
            for (int j = 0; j < pt.Particles.Count; ++j)
            {
                if (particleIndex < _particlesData.Count)
                {
                    ParticleData data = _particlesData[particleIndex];
                    Particle p = pt.Particles[j];
                    
                    p.Position = data.Position;
                    p.PrevPosition = data.PrevPosition;
                    p.IsColliding = data.IsColliding != 0;
                    
                    particleIndex++;
                }
            }
        }
    }

    private void CleanupBuffers()
    {
        _particlesBuffer?.Dispose();
        _particleTreesBuffer?.Dispose();
        _transformMatricesBuffer?.Dispose();
        _collidersBuffer?.Dispose();

        _particlesBuffer = null;
        _particleTreesBuffer = null;
        _transformMatricesBuffer = null;
        _collidersBuffer = null;
        
        _buffersInitialized = false;

        // Clean up async readback state
        if (_gpuFence != IntPtr.Zero)
        {
            var renderer = AbstractRenderer.Current as OpenGLRenderer;
            renderer?.RawGL.DeleteSync(_gpuFence);
            _gpuFence = IntPtr.Zero;
        }
        _readbackPending = false;
        _readbackData = null;
    }

    private void CleanupPrograms()
    {
        _mainPhysicsProgram?.Destroy();
        _skipUpdateParticlesProgram?.Destroy();
        
        _mainPhysicsProgram = null;
        _skipUpdateParticlesProgram = null;
    }

    #endregion

    #region Particle Setup and Management
    public void SetupParticles()
    {
        _particleTrees.Clear();

        if (Root != null)
            AppendParticleTree(Root);
        
        if (Roots != null)
        {
            for (int i = 0; i < Roots.Count; ++i)
            {
                Transform root = Roots[i];
                if (root == null)
                    continue;

                if (_particleTrees.Exists(x => x.Root == root))
                    continue;

                AppendParticleTree(root);
            }
        }

        if (_particleTrees.Count == 0)
            AppendParticleTree(SceneNode.GetTransformAs<Transform>(true)!);

        _objectScale = MathF.Abs(Transform.LossyWorldScale.X);
        _objectPrevPosition = Transform.WorldTranslation;
        _objectMove = Vector3.Zero;

        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            AppendParticles(pt, pt.Root, -1, 0.0f);
        }

        UpdateParameters();
        _buffersInitialized = false;
    }

    private void AppendParticleTree(Transform root)
    {
        if (root is null)
            return;

        _particleTrees.Add(new ParticleTree(root));
    }

    private void AppendParticles(ParticleTree tree, Transform? tfm, int parentIndex, float boneLength)
    {
        var ptcl = new Particle(tfm, parentIndex);

        if (tfm != null)
        {
            ptcl.Position = ptcl.PrevPosition = tfm.WorldTranslation;
            ptcl.InitLocalPosition = tfm.LocalTranslation;
            ptcl.InitLocalRotation = tfm.LocalRotation;
        }
        else //end bone
        {
            TransformBase? parent = tree.Particles[parentIndex].Transform;
            if (parent != null)
            {
                if (EndLength > 0.0f)
                {
                    TransformBase? parentParentTfm = parent.Parent;
                    Vector3 endOffset = parentParentTfm != null
                        ? parent.InverseTransformPoint(parent.WorldTranslation * 2.0f - parentParentTfm.WorldTranslation) * EndLength
                        : new Vector3(EndLength, 0.0f, 0.0f);
                    ptcl.EndOffset = endOffset;
                }
                else
                    ptcl.EndOffset = parent.InverseTransformPoint(Transform.TransformDirection(EndOffset) + parent.WorldTranslation);
                                
                ptcl.Position = ptcl.PrevPosition = parent.TransformPoint(ptcl.EndOffset);
            }
            ptcl.InitLocalPosition = Vector3.Zero;
            ptcl.InitLocalRotation = Quaternion.Identity;
        }

        if (parentIndex >= 0 && tree.Particles[parentIndex].Transform is not null)
        {
            var parentPtcl = tree.Particles[parentIndex];
            var parentTfm = parentPtcl.Transform!;
            var parentPtclPos = parentTfm.WorldTranslation;
            boneLength += (parentPtclPos - ptcl.Position).Length();
            ptcl.BoneLength = boneLength;
            tree.BoneTotalLength = MathF.Max(tree.BoneTotalLength, boneLength);
            tree.Particles[parentIndex].ChildCount += 1;
        }

        int index = tree.Particles.Count;
        tree.Particles.Add(ptcl);

        if (tfm != null)
        {
            for (int i = 0; i < tfm.Children.Count; ++i)
            {
                TransformBase child = tfm.Children[i];

                bool exclude = false;
                if (Exclusions != null)
                    exclude = Exclusions.Contains(child);
                
                if (!exclude)
                    AppendParticles(tree, child as Transform, index, boneLength);
                else if (EndLength > 0.0f || EndOffset != Vector3.Zero)
                    AppendParticles(tree, null, index, boneLength);
            }

            if (tfm.Children.Count == 0 && (EndLength > 0.0f || EndOffset != Vector3.Zero))
                AppendParticles(tree, null, index, boneLength);
        }
    }

    public void UpdateParameters()
    {
        SetWeight(BlendWeight);
        for (int i = 0; i < _particleTrees.Count; ++i)
            UpdateParameters(_particleTrees[i]);
    }

    private void UpdateParameters(ParticleTree pt)
    {
        pt.LocalGravity = Vector3.TransformNormal(Gravity, pt.RootWorldToLocalMatrix);

        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            p.Damping = Damping;
            p.Elasticity = Elasticity;
            p.Stiffness = Stiffness;
            p.Inert = Inert;
            p.Friction = Friction;
            p.Radius = Radius;

            if (pt.BoneTotalLength > 0)
            {
                float a = p.BoneLength / pt.BoneTotalLength;
                if (DampingDistrib != null && DampingDistrib.Keyframes.Count > 0)
                    p.Damping *= DampingDistrib.Evaluate(a);
                if (ElasticityDistrib != null && ElasticityDistrib.Keyframes.Count > 0)
                    p.Elasticity *= ElasticityDistrib.Evaluate(a);
                if (StiffnessDistrib != null && StiffnessDistrib.Keyframes.Count > 0)
                    p.Stiffness *= StiffnessDistrib.Evaluate(a);
                if (InertDistrib != null && InertDistrib.Keyframes.Count > 0)
                    p.Inert *= InertDistrib.Evaluate(a);
                if (FrictionDistrib != null && FrictionDistrib.Keyframes.Count > 0)
                    p.Friction *= FrictionDistrib.Evaluate(a);
                if (RadiusDistrib != null && RadiusDistrib.Keyframes.Count > 0)
                    p.Radius *= RadiusDistrib.Evaluate(a);
            }

            p.Damping = p.Damping.Clamp(0, 1);
            p.Elasticity = p.Elasticity.Clamp(0, 1);
            p.Stiffness = p.Stiffness.Clamp(0, 1);
            p.Inert = p.Inert.Clamp(0, 1);
            p.Friction = p.Friction.Clamp(0, 1);
            p.Radius = MathF.Max(p.Radius, 0);
        }
    }

    private bool IsRootChanged()
    {
        var roots = new List<Transform>();
        if (Root != null)
            roots.Add(Root);
        
        if (Roots != null)
            foreach (var root in Roots)
                if (root != null && !roots.Contains(root))
                    roots.Add(root);

        if (roots.Count == 0)
            roots.Add(SceneNode.GetTransformAs<Transform>(true)!);

        if (roots.Count != _particleTrees.Count)
            return true;

        for (int i = 0; i < roots.Count; ++i)
            if (roots[i] != _particleTrees[i].Root)
                return true;
        
        return false;
    }

    private void ResetParticlesPosition()
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            ResetParticlesPosition(_particleTrees[i]);

        _objectPrevPosition = Transform.WorldTranslation;
        InitializeRootBoneTracking();
    }

    private void InitializeRootBoneTracking()
    {
        if (RootBone is not null)
        {
            RootBone.RecalculateMatrices();
            _rootBonePrevPosition = RootBone.WorldTranslation;
        }
        _smoothedObjectMove = Vector3.Zero;
    }

    private static void ResetParticlesPosition(ParticleTree pt)
    {
        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            if (p.Transform is not null)
                p.Position = p.PrevPosition = p.Transform.WorldTranslation;
            else // end bone
            {
                Transform? pb = pt.Particles[p.ParentIndex].Transform;
                if (pb is not null)
                    p.Position = p.PrevPosition = pb.TransformPoint(p.EndOffset);
            }
            p.IsColliding = false;
        }
    }

    private void InitTransforms()
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            InitTransforms(_particleTrees[i]);
    }

    private static void InitTransforms(ParticleTree pt)
    {
        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            if (p.Transform is null)
                continue;
            
            p.Transform.Translation = p.InitLocalPosition;
            p.Transform.Rotation = p.InitLocalRotation;
        }
    }
    #endregion
    
    #region Transform Application
    private void ApplyParticlesToTransforms()
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            ApplyParticlesToTransforms(_particleTrees[i]);
    }

    private static void ApplyParticlesToTransforms(ParticleTree pt)
    {
        for (int i = 1; i < pt.Particles.Count; ++i)
        {
            Particle child = pt.Particles[i];
            Particle parent = pt.Particles[child.ParentIndex];

            Transform? pTfm = parent.Transform;
            Transform? cTfm = child.Transform;

            if (parent.ChildCount <= 1 && pTfm is not null) // do not modify bone orientation if has more then one child
            {
                Vector3 localPos = cTfm is not null
                    ? cTfm.Translation
                    : child.EndOffset;

                Vector3 v0 = pTfm.TransformDirection(localPos);
                Vector3 v1 = child.Position - parent.Position;
                Quaternion rot = Quaternion.Normalize(XRMath.RotationBetweenVectors(v0, v1));

                pTfm.AddWorldRotationDelta(rot);
            }

            cTfm?.SetWorldTranslation(child.Position);
        }
    }
    #endregion

    #region Distance Check
    private void CheckDistance()
    {
        if (!DistantDisable)
            return;

        TransformBase? rt = ReferenceObject;
        if (rt is null)
        {
            XRCamera? c = State.MainPlayer.ControlledPawn?.CameraComponent?.Camera;
            if (c != null)
                rt = c.Transform;
        }

        if (rt is null)
            return;

        rt.RecalculateMatrices();
        float d2 = (rt.WorldTranslation - Transform.LocalTranslation).LengthSquared();
        bool disable = d2 > DistanceToObject * DistanceToObject;
        if (disable == _distantDisabled)
            return;
        
        if (!disable)
            ResetParticlesPosition();
        _distantDisabled = disable;
    }
    #endregion

    #region Visualization
    private void Render()
    {
        if (!IsActive || Engine.Rendering.State.IsShadowPass)
            return;

        if (IsEditor && !IsPlaying && Transform.HasChanged)
        {
            SetupParticles();
        }

        for (int i = 0; i < _particleTrees.Count; ++i)
            DrawTree(_particleTrees[i]);
    }

    private void DrawTree(ParticleTree pt)
    {
        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            if (p.ParentIndex >= 0)
            {
                Particle p0 = pt.Particles[p.ParentIndex];
                Engine.Rendering.Debug.RenderLine(p.Position, p0.Position, ColorF4.Orange);
            }
            if (p.Radius > 0)
            {
                float radius = p.Radius * _objectScale;
                Engine.Rendering.Debug.RenderSphere(p.Position, radius, false, ColorF4.Yellow);
            }
        }
    }
    #endregion

    #region Weight Management
    public void SetWeight(float w)
    {
        if (_weight == w)
            return;
        
        if (w == 0)
            InitTransforms();
        else if (_weight == 0)
            ResetParticlesPosition();

        _weight = BlendWeight = w;
    }

    public float Weight => _weight;

    public RenderInfo[] RenderedObjects => _gpuWorkRenderInfo is not null ? [_gpuWorkRenderInfo] : [];

    #endregion
}

/// <summary>
/// Update mode for the physics chain.
/// </summary>
public enum EUpdateMode
{
    Default,
    FixedUpdate,
    Undilated
}

/// <summary>
/// Defines which axis to freeze movement on.
/// </summary>
public enum EFreezeAxis
{
    None = 0,
    X = 1,
    Y = 2,
    Z = 3
}
