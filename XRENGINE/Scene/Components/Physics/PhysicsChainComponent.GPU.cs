using Extensions;
using Silk.NET.OpenGL;
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
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
        public Vector3 Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ColliderData
    {
        public Vector4 Center;
        public Vector4 Params;
        public int Type;
        public Vector3 Padding;
    }

    private XRRenderProgram? _mainPhysicsProgram;
    private XRRenderProgram? _skipUpdateParticlesProgram;
    private XRShader? _mainPhysicsShader;
    private XRShader? _skipUpdateParticlesShader;

    private XRDataBuffer? _particlesBuffer;
    private XRDataBuffer? _particleTreesBuffer;
    private XRDataBuffer? _transformMatricesBuffer;
    private XRDataBuffer? _collidersBuffer;

    private readonly List<ParticleData> _particlesData = [];
    private readonly List<ParticleTreeData> _particleTreesData = [];
    private readonly List<Matrix4x4> _transformMatrices = [];
    private readonly List<ColliderData> _collidersData = [];

    private int _totalParticleCount;
    private bool _buffersInitialized;

    private ParticleData[]? _readbackData;

    private bool _pendingGpuExecutionReconfigure;
    private RenderInfo3D? _gpuWorkRenderInfo;
    private RenderCommandMethod3D? _gpuWorkRenderCommand;
    private volatile bool _hasPendingGPUWork;
    private int _pendingLoop;
    private float _pendingTimeVar;
    private readonly object _pendingWorkLock = new();

    private void ActivateGpuExecutionMode()
    {
        if (!UseGPU)
            return;

        GPUPhysicsChainDispatcher.Instance.Register(this);
    }

    private void DeactivateGpuExecutionMode()
    {
        GPUPhysicsChainDispatcher.Instance.Unregister(this);
        _hasPendingGPUWork = false;
        CleanupBuffers();
        CleanupPrograms();
    }

    private void EnsureStandaloneGpuPrograms()
    {
        if (_mainPhysicsProgram is not null && _skipUpdateParticlesProgram is not null)
            return;

        _mainPhysicsShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/PhysicsChain.comp", EShaderType.Compute);
        _skipUpdateParticlesShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/SkipUpdateParticles.comp", EShaderType.Compute);

        _mainPhysicsProgram = new XRRenderProgram(true, false, _mainPhysicsShader);
        _skipUpdateParticlesProgram = new XRRenderProgram(true, false, _skipUpdateParticlesShader);
    }

    private bool HandleGpuExecutionModePropertyChanged<T>(string? propName, T prev, T field)
    {
        if (propName != nameof(UseGPU) && propName != nameof(UseBatchedDispatcher))
            return false;

        if (_isSimulating)
        {
            _pendingGpuExecutionReconfigure = true;
            return true;
        }

        ReconfigureGpuExecutionMode();
        return true;
    }

    private void ApplyPendingGpuExecutionReconfigure()
    {
        if (!_pendingGpuExecutionReconfigure)
            return;

        _pendingGpuExecutionReconfigure = false;
        ReconfigureGpuExecutionMode();
    }

    private void ReconfigureGpuExecutionMode()
    {
        DeactivateGpuExecutionMode();
        if (IsActive)
            ActivateGpuExecutionMode();

        MarkGpuBuffersDirty();

        if (IsActive)
        {
            SetupParticles();
            ResetParticlesPosition();
        }
    }

    private void MarkGpuBuffersDirty()
    {
        CleanupBuffers();
        _hasPendingGPUWork = false;
    }

    private void ExecuteGpuLateUpdate()
    {
        CheckDistance();
        if (IsNeedUpdate())
        {
            Prepare();
            UpdateParticlesGpu();

            // Re-apply the last-known particle positions so that transform readers
            // (debug draw, dependent components) see the simulation state rather
            // than the rest pose that Prepare() restored.  The actual GPU compute +
            // readback happens later (GlobalPreRender), so these positions are
            // effectively one frame old — standard CPU-GPU sync latency.
            ApplyParticlesToTransforms();
        }
    }

    private void UpdateParticlesGpu()
    {
        if (_particleTrees.Count <= 0)
            return;

        int loop = 1;
        float timeVar = 1.0f;
        float dt = _deltaTime;

        if (UpdateMode == EUpdateMode.Default)
        {
            if (UpdateRate > 0.0f)
                timeVar = dt * UpdateRate;
        }
        else if (UpdateMode == EUpdateMode.FixedUpdate)
        {
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
                        _time = 0.0f;
                        break;
                    }
                }

                timeVar = frameTime;
            }
            else
            {
                timeVar = dt;
            }
        }
        else if (UpdateMode == EUpdateMode.Undilated && UpdateRate > 0.0f)
        {
            float frameTime = 1.0f / UpdateRate;
            _time += dt;
            loop = 0;

            while (_time >= frameTime)
            {
                _time -= frameTime;
                if (++loop >= 3)
                {
                    _time = 0.0f;
                    break;
                }
            }

            timeVar = frameTime;
        }

        SubmitToBatchedDispatcher(loop, timeVar);
    }

    private void ExecutePendingGpuWork()
    {
        if (!UseGPU || UseBatchedDispatcher || !_hasPendingGPUWork)
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

        ExecuteStandaloneGpuWork(loop, timeVar);
    }

    private void ExecuteStandaloneGpuWork(int loop, float timeVar)
    {
        if (!IsActive || !UseGPU || UseBatchedDispatcher)
            return;

        if (!_buffersInitialized)
            InitializeBuffers();
        else
            UpdateBufferData();

        if (loop > 0)
        {
            for (int i = 0; i < loop; ++i)
            {
                DispatchMainPhysics(timeVar, i == 0);
                if (i < loop - 1)
                    InsertShaderStorageBarrier();
            }
        }
        else
        {
            DispatchSkipUpdateParticles();
        }

        // Synchronous readback — matches the batched dispatcher's behavior and
        // avoids the 2-frame pipeline delay that async fence-based readback caused.
        InsertShaderStorageBarrier();
        SynchronousReadback();
    }

    private void SubmitToBatchedDispatcher(int loopCount, float timeVar)
    {
        PrepareGPUData();

        var particles = new List<GPUPhysicsChainDispatcher.GPUParticleData>(_particlesData.Count);
        foreach (ParticleData particle in _particlesData)
        {
            particles.Add(new GPUPhysicsChainDispatcher.GPUParticleData
            {
                Position = particle.Position,
                PrevPosition = particle.PrevPosition,
                TransformPosition = particle.TransformPosition,
                TransformLocalPosition = particle.TransformLocalPosition,
                ParentIndex = particle.ParentIndex,
                Damping = particle.Damping,
                Elasticity = particle.Elasticity,
                Stiffness = particle.Stiffness,
                Inert = particle.Inert,
                Friction = particle.Friction,
                Radius = particle.Radius,
                BoneLength = particle.BoneLength,
                IsColliding = particle.IsColliding
            });
        }

        var trees = new List<GPUPhysicsChainDispatcher.GPUParticleTreeData>(_particleTreesData.Count);
        foreach (ParticleTreeData tree in _particleTreesData)
        {
            trees.Add(new GPUPhysicsChainDispatcher.GPUParticleTreeData
            {
                LocalGravity = tree.LocalGravity,
                RestGravity = tree.RestGravity,
                ParticleStart = tree.ParticleStart,
                ParticleCount = tree.ParticleCount,
                RootWorldToLocal = tree.RootWorldToLocal,
                BoneTotalLength = tree.BoneTotalLength
            });
        }

        var colliders = new List<GPUPhysicsChainDispatcher.GPUColliderData>(_collidersData.Count);
        foreach (ColliderData collider in _collidersData)
        {
            colliders.Add(new GPUPhysicsChainDispatcher.GPUColliderData
            {
                Center = collider.Center,
                Params = collider.Params,
                Type = collider.Type
            });
        }

        GPUPhysicsChainDispatcher.Instance.SubmitData(
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

    public void ApplyReadbackData(ArraySegment<GPUPhysicsChainDispatcher.GPUParticleData> readbackData)
    {
        int particleIndex = 0;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            for (int particleTreeIndex = 0; particleTreeIndex < tree.Particles.Count; ++particleTreeIndex)
            {
                if (particleIndex >= readbackData.Count)
                    return;

                GPUPhysicsChainDispatcher.GPUParticleData data = readbackData.Array![readbackData.Offset + particleIndex];
                Particle particle = tree.Particles[particleTreeIndex];
                particle.Position = data.Position;
                particle.PrevPosition = data.PrevPosition;
                particle.IsColliding = data.IsColliding != 0;
                ++particleIndex;
            }
        }

        ApplyGpuResultsToTransforms();
    }

    private void InsertShaderStorageBarrier()
    {
        if (AbstractRenderer.Current is OpenGLRenderer renderer)
            renderer.RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
    }

    private void SynchronousReadback()
    {
        if (_particlesBuffer is null)
            return;

        if (_readbackData is null || _readbackData.Length != _totalParticleCount)
            _readbackData = new ParticleData[_totalParticleCount];

        ReadParticleDataFromBuffer();
        ApplyReadbackDataToParticles();
    }

    private void ReadParticleDataFromBuffer()
    {
        if (_particlesBuffer is null || _readbackData is null)
            return;

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
            return;

        if (_particlesBuffer.APIWrappers.FirstOrDefault() is not OpenGLRenderer.GLDataBuffer glBuffer
            || !glBuffer.TryGetBindingId(out uint bufferId)
            || bufferId == 0)
            return;

        unsafe
        {
            nuint byteSize = (nuint)(_readbackData.Length * Marshal.SizeOf<ParticleData>());
            fixed (ParticleData* ptr = _readbackData)
            {
                renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, bufferId);
                renderer.RawGL.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, byteSize, ptr);
                renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
            }
        }
    }

    private void ApplyReadbackDataToParticles()
    {
        if (_readbackData is null)
            return;

        int particleIndex = 0;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            for (int particleTreeIndex = 0; particleTreeIndex < tree.Particles.Count; ++particleTreeIndex)
            {
                if (particleIndex >= _readbackData.Length)
                    return;

                ParticleData data = _readbackData[particleIndex];
                Particle particle = tree.Particles[particleTreeIndex];
                particle.Position = data.Position;
                particle.PrevPosition = data.PrevPosition;
                particle.IsColliding = data.IsColliding != 0;
                ++particleIndex;
            }
        }

        ApplyGpuResultsToTransforms();
    }

    private void ApplyGpuResultsToTransforms()
    {
        ApplyParticlesToTransforms();

        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            _particleTrees[treeIndex].Root.RecalculateMatrixHierarchy(
                forceWorldRecalc: true,
                setRenderMatrixNow: true,
                childRecalcType: Engine.Rendering.Settings.RecalcChildMatricesLoopType).Wait();
        }
    }

    private void DispatchMainPhysics(float timeVar, bool applyObjectMove)
    {
        if (_mainPhysicsProgram is null || _particlesBuffer is null || _particleTreesBuffer is null || _transformMatricesBuffer is null || _collidersBuffer is null)
            return;

        _mainPhysicsProgram.Uniform("DeltaTime", timeVar);
        _mainPhysicsProgram.Uniform("ObjectScale", _objectScale);
        _mainPhysicsProgram.Uniform("Weight", _weight);
        _mainPhysicsProgram.Uniform("Force", Force);
        _mainPhysicsProgram.Uniform("Gravity", Gravity);
        _mainPhysicsProgram.Uniform("ObjectMove", applyObjectMove ? _objectMove : Vector3.Zero);
        _mainPhysicsProgram.Uniform("FreezeAxis", (int)FreezeAxis);
        _mainPhysicsProgram.Uniform("ColliderCount", _collidersData.Count);
        _mainPhysicsProgram.Uniform("UpdateMode", (int)UpdateMode);
        _mainPhysicsProgram.Uniform("UpdateRate", UpdateRate);

        _mainPhysicsProgram.BindBuffer(_particlesBuffer, 0);
        _mainPhysicsProgram.BindBuffer(_particleTreesBuffer, 1);
        _mainPhysicsProgram.BindBuffer(_transformMatricesBuffer, 2);
        _mainPhysicsProgram.BindBuffer(_collidersBuffer, 3);

        int threadGroupsX = (_totalParticleCount + 127) / 128;
        _mainPhysicsProgram.DispatchCompute((uint)threadGroupsX, 1, 1);
    }

    private void DispatchSkipUpdateParticles()
    {
        if (_skipUpdateParticlesProgram is null || _particlesBuffer is null || _transformMatricesBuffer is null)
            return;

        _skipUpdateParticlesProgram.Uniform("ObjectMove", _objectMove);
        _skipUpdateParticlesProgram.Uniform("Weight", _weight);

        _skipUpdateParticlesProgram.BindBuffer(_particlesBuffer, 0);
        _skipUpdateParticlesProgram.BindBuffer(_transformMatricesBuffer, 2);

        int threadGroupsX = (_totalParticleCount + 127) / 128;
        _skipUpdateParticlesProgram.DispatchCompute((uint)threadGroupsX, 1, 1);
    }

    private void InitializeBuffers()
    {
        CleanupBuffers();
        PrepareGPUData();

        _particlesBuffer = new XRDataBuffer("Particles", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_particlesData.Count, 1), EComponentType.Float, 16, false, false);
        _particlesBuffer.SetBlockIndex(0);

        _particleTreesBuffer = new XRDataBuffer("ParticleTrees", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_particleTreesData.Count, 1), EComponentType.Float, 20, false, false);
        _particleTreesBuffer.SetBlockIndex(1);

        _transformMatricesBuffer = new XRDataBuffer("TransformMatrices", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_transformMatrices.Count, 1), EComponentType.Float, 16, false, false);
        _transformMatricesBuffer.SetBlockIndex(2);

        _collidersBuffer = new XRDataBuffer("Colliders", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_collidersData.Count, 1), EComponentType.Float, 16, false, false);
        _collidersBuffer.SetBlockIndex(3);

        UpdateBufferData();
        _buffersInitialized = true;
    }

    private void UpdateBufferData()
    {
        PrepareGPUData();
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

        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            _particleTreesData.Add(new ParticleTreeData
            {
                LocalGravity = tree.LocalGravity,
                RestGravity = tree.RestGravity,
                ParticleStart = _totalParticleCount,
                ParticleCount = tree.Particles.Count,
                RootWorldToLocal = tree.RootWorldToLocalMatrix,
                BoneTotalLength = tree.BoneTotalLength
            });

            for (int particleIndex = 0; particleIndex < tree.Particles.Count; ++particleIndex)
            {
                Particle particle = tree.Particles[particleIndex];
                // Use cached rest-pose data from Prepare() rather than live transforms.
                // PrepareGPUData may run during PreRender (standalone path) after
                // ApplyParticlesToTransforms has written simulation positions to bone
                // transforms. Reading live transforms here would feed simulation-applied
                // positions as the "rest" reference, breaking stiffness/elasticity.
                _particlesData.Add(new ParticleData
                {
                    Position = particle.Position,
                    PrevPosition = particle.PrevPosition,
                    TransformPosition = particle.TransformPosition,
                    TransformLocalPosition = particle.TransformLocalPosition,
                    ParentIndex = particle.ParentIndex >= 0 ? particle.ParentIndex + _totalParticleCount : -1,
                    Damping = particle.Damping,
                    Elasticity = particle.Elasticity,
                    Stiffness = particle.Stiffness,
                    Inert = particle.Inert,
                    Friction = particle.Friction,
                    Radius = particle.Radius,
                    BoneLength = particle.BoneLength,
                    IsColliding = particle.IsColliding ? 1 : 0
                });

                if (particle.Transform is not null)
                    _transformMatrices.Add(particle.TransformLocalToWorldMatrix);
                else if (particle.ParentIndex >= 0)
                    _transformMatrices.Add(tree.Particles[particle.ParentIndex].TransformLocalToWorldMatrix);
                else
                    _transformMatrices.Add(Matrix4x4.Identity);
            }

            _totalParticleCount += tree.Particles.Count;
        }

        if (_effectiveColliders is null)
            return;

        for (int colliderIndex = 0; colliderIndex < _effectiveColliders.Count; ++colliderIndex)
        {
            PhysicsChainColliderBase collider = _effectiveColliders[colliderIndex];
            if (collider is PhysicsChainSphereCollider sphereCollider)
            {
                _collidersData.Add(new ColliderData
                {
                    Center = new Vector4(sphereCollider.Transform.WorldTranslation, sphereCollider.Radius),
                    Type = 0
                });
            }
            else if (collider is PhysicsChainCapsuleCollider capsuleCollider)
            {
                Vector3 start = capsuleCollider.Transform.WorldTranslation;
                Vector3 end = capsuleCollider.Transform.TransformPoint(new Vector3(0.0f, capsuleCollider.Height, 0.0f));
                _collidersData.Add(new ColliderData
                {
                    Center = new Vector4(start, capsuleCollider.Radius),
                    Params = new Vector4(end, 0.0f),
                    Type = 1
                });
            }
            else if (collider is PhysicsChainBoxCollider boxCollider)
            {
                _collidersData.Add(new ColliderData
                {
                    Center = new Vector4(boxCollider.Transform.WorldTranslation, 0.0f),
                    Params = new Vector4(boxCollider.Size * 0.5f, 0.0f),
                    Type = 2
                });
            }
            else if (collider is PhysicsChainPlaneCollider planeCollider)
            {
                _collidersData.Add(new ColliderData
                {
                    Center = new Vector4(planeCollider.Transform.TransformPoint(planeCollider._center), 0.0f),
                    Params = new Vector4(planeCollider._plane.Normal, planeCollider._bound == PhysicsChainColliderBase.EBound.Inside ? 1.0f : 0.0f),
                    Type = 3
                });
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

        _readbackData = null;
    }

    private void CleanupPrograms()
    {
        _mainPhysicsProgram?.Destroy();
        _skipUpdateParticlesProgram?.Destroy();

        _mainPhysicsProgram = null;
        _skipUpdateParticlesProgram = null;
        _mainPhysicsShader = null;
        _skipUpdateParticlesShader = null;
    }
}