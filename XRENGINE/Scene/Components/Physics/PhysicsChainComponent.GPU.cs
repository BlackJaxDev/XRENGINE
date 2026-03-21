using Extensions;
using Silk.NET.OpenGL;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Colors;
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
    private struct ParticleStaticData
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
    private XRRenderProgram? _gpuDebugRenderProgram;
    private XRShader? _mainPhysicsShader;
    private XRShader? _skipUpdateParticlesShader;
    private XRShader? _gpuDebugRenderShader;

    private XRDataBuffer? _particlesBuffer;
    private XRDataBuffer? _particleStaticBuffer;
    private XRDataBuffer? _particleTreesBuffer;
    private XRDataBuffer? _transformMatricesBuffer;
    private XRDataBuffer? _collidersBuffer;
    private XRDataBuffer? _gpuDebugPointsBuffer;
    private XRDataBuffer? _gpuDebugLinesBuffer;
    private XRMeshRenderer? _gpuDebugPointsRenderer;
    private XRMeshRenderer? _gpuDebugLinesRenderer;

    private readonly List<ParticleData> _particlesData = [];
    private readonly List<ParticleStaticData> _particleStaticData = [];
    private readonly List<ParticleTreeData> _particleTreesData = [];
    private readonly List<Matrix4x4> _transformMatrices = [];
    private readonly List<ColliderData> _collidersData = [];
    private readonly List<GPUPhysicsChainDispatcher.GPUParticleData> _dispatcherParticlesData = [];
    private readonly List<GPUPhysicsChainDispatcher.GPUParticleStaticData> _dispatcherParticleStaticData = [];
    private readonly List<GPUPhysicsChainDispatcher.GPUParticleTreeData> _dispatcherParticleTreesData = [];
    private readonly List<GPUPhysicsChainDispatcher.GPUColliderData> _dispatcherCollidersData = [];

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
    private int _gpuExecutionGeneration;
    private long _latestGpuSubmissionId;
    private long _lastAppliedGpuSubmissionId;
    private int _preparedGpuDataVersion = -1;
    private int _uploadedStaticDataVersion = -1;
    private int _dispatcherStaticDataVersion = -1;

    private void ActivateGpuExecutionMode()
    {
        if (!UseGPU)
            return;

        unchecked
        {
            ++_gpuExecutionGeneration;
        }

        GPUPhysicsChainDispatcher.Instance.Register(this);
    }

    private void DeactivateGpuExecutionMode()
    {
        unchecked
        {
            ++_gpuExecutionGeneration;
        }

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

    private void EnsureGpuDebugRenderProgram()
    {
        if (_gpuDebugRenderProgram is not null)
            return;

        _gpuDebugRenderShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/PhysicsChainDebugDraw.comp", EShaderType.Compute);
        _gpuDebugRenderProgram = new XRRenderProgram(true, false, _gpuDebugRenderShader);
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
        }
    }

    private void UpdateParticlesGpu()
    {
        if (_particleTrees.Count <= 0)
        {
            _lastSimulationProducedResults = false;
            return;
        }

        float dt = _deltaTime;
        ResolveSimulationLoopAndTimeScale(dt, out int loop, out float timeVar);

        bool producedResults = loop > 0;
        _lastSimulationProducedResults = producedResults;

        if (!producedResults)
            return;

        if (UseBatchedDispatcher)
        {
            SubmitToBatchedDispatcher(loop, timeVar);
            return;
        }

        lock (_pendingWorkLock)
        {
            _pendingLoop = loop;
            _pendingTimeVar = timeVar;
            _hasPendingGPUWork = true;
        }
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

        EnsureStandaloneGpuPrograms();

        if (!_buffersInitialized)
            InitializeBuffers();
        else
            UpdateBufferData();

        if (loop <= 0)
            return;

        if (loop > 0)
        {
            for (int i = 0; i < loop; ++i)
            {
                DispatchMainPhysics(timeVar, i == 0);
                if (i < loop - 1)
                    InsertShaderStorageBarrier();
            }
        }

        // Always read back GPU results to keep CPU particle state in sync.
        // Without this, PrepareGPUData would overwrite GPU-computed positions
        // with stale CPU-side values on the next frame.
        InsertShaderStorageBarrier();
        SynchronousReadback();
    }

    private void SubmitToBatchedDispatcher(int loopCount, float timeVar)
    {
        PrepareGPUData();
        long submissionId = ++_latestGpuSubmissionId;

        _dispatcherParticlesData.Clear();
        foreach (ParticleData particle in _particlesData)
        {
            _dispatcherParticlesData.Add(new GPUPhysicsChainDispatcher.GPUParticleData
            {
                Position = particle.Position,
                PrevPosition = particle.PrevPosition,
                TransformPosition = particle.TransformPosition,
                IsColliding = particle.IsColliding,
                PreviousPhysicsPosition = particle.PreviousPhysicsPosition
            });
        }

        if (_dispatcherStaticDataVersion != _preparedGpuDataVersion)
        {
            _dispatcherParticleStaticData.Clear();
            foreach (ParticleStaticData particle in _particleStaticData)
            {
                _dispatcherParticleStaticData.Add(new GPUPhysicsChainDispatcher.GPUParticleStaticData
                {
                    TransformLocalPosition = particle.TransformLocalPosition,
                    ParentIndex = particle.ParentIndex,
                    Damping = particle.Damping,
                    Elasticity = particle.Elasticity,
                    Stiffness = particle.Stiffness,
                    Inert = particle.Inert,
                    Friction = particle.Friction,
                    Radius = particle.Radius,
                    BoneLength = particle.BoneLength,
                });
            }

            _dispatcherStaticDataVersion = _preparedGpuDataVersion;
        }

        _dispatcherParticleTreesData.Clear();
        foreach (ParticleTreeData tree in _particleTreesData)
        {
            _dispatcherParticleTreesData.Add(new GPUPhysicsChainDispatcher.GPUParticleTreeData
            {
                LocalGravity = tree.LocalGravity,
                RestGravity = tree.RestGravity,
                ParticleStart = tree.ParticleStart,
                ParticleCount = tree.ParticleCount,
                RootWorldToLocal = tree.RootWorldToLocal,
                BoneTotalLength = tree.BoneTotalLength
            });
        }

        _dispatcherCollidersData.Clear();
        foreach (ColliderData collider in _collidersData)
        {
            _dispatcherCollidersData.Add(new GPUPhysicsChainDispatcher.GPUColliderData
            {
                Center = collider.Center,
                Params = collider.Params,
                Type = collider.Type
            });
        }

        GPUPhysicsChainDispatcher.Instance.SubmitData(
            this,
            _dispatcherParticlesData,
            _dispatcherParticleStaticData,
            _dispatcherParticleTreesData,
            _transformMatrices,
            _dispatcherCollidersData,
            _deltaTime,
            _objectScale,
            _weight,
            Force,
            Gravity,
            _objectMove,
            (int)FreezeAxis,
            loopCount,
            timeVar,
            _gpuExecutionGeneration,
            submissionId,
            _particlesVersion);
    }

    public void ApplyReadbackData(ArraySegment<GPUPhysicsChainDispatcher.GPUParticleData> readbackData, int generation, long submissionId)
    {
        if (!UseGPU || generation != _gpuExecutionGeneration || submissionId <= _lastAppliedGpuSubmissionId)
            return;

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
                particle.PreviousPhysicsPosition = data.PreviousPhysicsPosition;
                particle.IsColliding = data.IsColliding != 0;
                ++particleIndex;
            }
        }

        _lastAppliedGpuSubmissionId = submissionId;

        if (GpuSyncToBones)
            _hasPendingGpuBoneSync = true;
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
                particle.PreviousPhysicsPosition = data.PreviousPhysicsPosition;
                particle.IsColliding = data.IsColliding != 0;
                ++particleIndex;
            }
        }

        if (GpuSyncToBones)
        {
            _hasPendingGpuBoneSync = true;
            return;
        }

        ApplyCurrentParticleTransforms(newSimulationResults: _lastSimulationProducedResults);
    }

    private void ApplyGpuResultsToTransforms(bool newSimulationResults = false)
    {
        if (!GpuSyncToBones)
            return;

        ApplyCurrentParticleTransforms(newSimulationResults);

        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            _particleTrees[treeIndex].Root.RecalculateMatrixHierarchy(
                forceWorldRecalc: true,
                setRenderMatrixNow: true,
                childRecalcType: Engine.Rendering.Settings.RecalcChildMatricesLoopType).Wait();
        }
    }

    private void ApplyPendingGpuBoneSync()
    {
        if (!UseGPU || !_hasPendingGpuBoneSync)
            return;

        _hasPendingGpuBoneSync = false;
        ApplyGpuResultsToTransforms(newSimulationResults: true);
    }

    private void DispatchMainPhysics(float timeVar, bool applyObjectMove)
    {
        if (_mainPhysicsProgram is null || _particlesBuffer is null || _particleStaticBuffer is null || _particleTreesBuffer is null || _transformMatricesBuffer is null || _collidersBuffer is null)
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
        _mainPhysicsProgram.BindBuffer(_particleStaticBuffer, 1);
        _mainPhysicsProgram.BindBuffer(_particleTreesBuffer, 2);
        _mainPhysicsProgram.BindBuffer(_transformMatricesBuffer, 3);
        _mainPhysicsProgram.BindBuffer(_collidersBuffer, 4);

        int threadGroupsX = (_totalParticleCount + 127) / 128;
        _mainPhysicsProgram.DispatchCompute((uint)threadGroupsX, 1, 1);
    }

    private void DispatchSkipUpdateParticles()
    {
        if (_skipUpdateParticlesProgram is null || _particlesBuffer is null || _particleStaticBuffer is null || _transformMatricesBuffer is null)
            return;

        _skipUpdateParticlesProgram.Uniform("ObjectMove", _objectMove);
        _skipUpdateParticlesProgram.Uniform("Weight", _weight);

        _skipUpdateParticlesProgram.BindBuffer(_particlesBuffer, 0);
        _skipUpdateParticlesProgram.BindBuffer(_particleStaticBuffer, 1);
        _skipUpdateParticlesProgram.BindBuffer(_transformMatricesBuffer, 3);

        int threadGroupsX = (_totalParticleCount + 127) / 128;
        _skipUpdateParticlesProgram.DispatchCompute((uint)threadGroupsX, 1, 1);
    }

    private void InitializeBuffers()
    {
        CleanupBuffers();
        PrepareGPUData();

        _particlesBuffer = new XRDataBuffer("Particles", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_particlesData.Count, 1), EComponentType.Float, 20, false, false);
        _particlesBuffer.SetBlockIndex(0);

        _particleStaticBuffer = new XRDataBuffer("ParticleStatic", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_particleStaticData.Count, 1), EComponentType.Float, 12, false, false);
        _particleStaticBuffer.SetBlockIndex(1);

        _particleTreesBuffer = new XRDataBuffer("ParticleTrees", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_particleTreesData.Count, 1), EComponentType.Float, 20, false, false);
        _particleTreesBuffer.SetBlockIndex(2);

        _transformMatricesBuffer = new XRDataBuffer("TransformMatrices", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_transformMatrices.Count, 1), EComponentType.Float, 16, false, false);
        _transformMatricesBuffer.SetBlockIndex(3);

        _collidersBuffer = new XRDataBuffer("Colliders", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_collidersData.Count, 1), EComponentType.Float, 16, false, false);
        _collidersBuffer.SetBlockIndex(4);

        UpdateBufferData();
        _buffersInitialized = true;
    }

    private void UpdateBufferData()
    {
        PrepareGPUData();
        _particlesBuffer?.SetDataRaw(_particlesData);
        _particlesBuffer?.PushData();
        if (_uploadedStaticDataVersion != _preparedGpuDataVersion)
        {
            _particleStaticBuffer?.SetDataRaw(_particleStaticData);
            _particleStaticBuffer?.PushData();
            _uploadedStaticDataVersion = _preparedGpuDataVersion;
        }
        _particleTreesBuffer?.SetDataRaw(_particleTreesData);
        _particleTreesBuffer?.PushData();
        _transformMatricesBuffer?.SetDataRaw(_transformMatrices);
        _transformMatricesBuffer?.PushData();
        _collidersBuffer?.SetDataRaw(_collidersData);
        _collidersBuffer?.PushData();
    }

    private void PrepareGPUData()
    {
        _transformMatrices.Clear();
        _collidersData.Clear();
        _totalParticleCount = 0;

        bool rebuildPreparedData = _preparedGpuDataVersion != _particlesVersion || _particleTreesData.Count != _particleTrees.Count;
        if (rebuildPreparedData)
        {
            _particlesData.Clear();
            _particleStaticData.Clear();
            _particleTreesData.Clear();
        }
        else
        {
            _particlesData.Clear();
        }

        int particleCursor = 0;

        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            ParticleTreeData treeData = new()
            {
                LocalGravity = tree.LocalGravity,
                RestGravity = tree.RestGravity,
                ParticleStart = _totalParticleCount,
                ParticleCount = tree.Particles.Count,
                RootWorldToLocal = tree.RootWorldToLocalMatrix,
                BoneTotalLength = tree.BoneTotalLength
            };

            if (rebuildPreparedData)
                _particleTreesData.Add(treeData);
            else
                _particleTreesData[treeIndex] = treeData;

            for (int particleIndex = 0; particleIndex < tree.Particles.Count; ++particleIndex)
            {
                Particle particle = tree.Particles[particleIndex];
                ParticleData particleData = new()
                {
                    Position = particle.Position,
                    PrevPosition = particle.PrevPosition,
                    TransformPosition = particle.TransformPosition,
                    IsColliding = particle.IsColliding ? 1 : 0,
                    PreviousPhysicsPosition = particle.PreviousPhysicsPosition
                };

                ParticleStaticData particleStaticData = new()
                {
                    TransformLocalPosition = particle.TransformLocalPosition,
                    ParentIndex = particle.ParentIndex >= 0 ? particle.ParentIndex + _totalParticleCount : -1,
                    Damping = particle.Damping,
                    Elasticity = particle.Elasticity,
                    Stiffness = particle.Stiffness,
                    Inert = particle.Inert,
                    Friction = particle.Friction,
                    Radius = particle.Radius,
                    BoneLength = particle.BoneLength,
                };

                _particlesData.Add(particleData);
                if (rebuildPreparedData)
                    _particleStaticData.Add(particleStaticData);

                if (particle.Transform is not null)
                    _transformMatrices.Add(particle.TransformLocalToWorldMatrix);
                else if (particle.ParentIndex >= 0)
                    _transformMatrices.Add(tree.Particles[particle.ParentIndex].TransformLocalToWorldMatrix);
                else
                    _transformMatrices.Add(Matrix4x4.Identity);

                ++particleCursor;
            }

            _totalParticleCount += tree.Particles.Count;
        }

        _preparedGpuDataVersion = _particlesVersion;

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
        _particleStaticBuffer?.Dispose();
        _particleTreesBuffer?.Dispose();
        _transformMatricesBuffer?.Dispose();
        _collidersBuffer?.Dispose();
        _gpuDebugPointsBuffer?.Dispose();
        _gpuDebugLinesBuffer?.Dispose();
        _gpuDebugPointsRenderer?.Destroy();
        _gpuDebugLinesRenderer?.Destroy();

        _particlesBuffer = null;
        _particleStaticBuffer = null;
        _particleTreesBuffer = null;
        _transformMatricesBuffer = null;
        _collidersBuffer = null;
        _gpuDebugPointsBuffer = null;
        _gpuDebugLinesBuffer = null;
        _gpuDebugPointsRenderer = null;
        _gpuDebugLinesRenderer = null;
        _buffersInitialized = false;

        _readbackData = null;
        _uploadedStaticDataVersion = -1;
    }

    private void CleanupPrograms()
    {
        _mainPhysicsProgram?.Destroy();
        _skipUpdateParticlesProgram?.Destroy();
        _gpuDebugRenderProgram?.Destroy();

        _mainPhysicsProgram = null;
        _skipUpdateParticlesProgram = null;
        _gpuDebugRenderProgram = null;
        _mainPhysicsShader = null;
        _skipUpdateParticlesShader = null;
        _gpuDebugRenderShader = null;
    }

    private void RenderGpuDebug()
    {
        if (!DebugDrawChains)
            return;

        if (AbstractRenderer.Current is not OpenGLRenderer renderer || !Engine.Rendering.State.DebugInstanceRenderingAvailable)
            return;

        if (UseBatchedDispatcher)
        {
            // Batched dispatch groups rebuild the shared dispatcher buffers multiple times per frame.
            // Rendering from the component's read-back particle state avoids cross-chain slice aliasing.
            for (int i = 0; i < _particleTrees.Count; ++i)
                DrawTree(_particleTrees[i]);

            return;
        }

        if (!TryGetGpuParticleRenderSource(out XRDataBuffer? particleBuffer, out XRDataBuffer? particleStaticBuffer, out int particleOffset, out int particleCount)
            || particleBuffer is null
            || particleStaticBuffer is null
            || particleCount <= 0)
            return;

        EnsureGpuDebugRenderProgram();
        EnsureGpuDebugResources((uint)particleCount);

        if (_gpuDebugRenderProgram is null || _gpuDebugPointsBuffer is null || _gpuDebugLinesBuffer is null)
            return;

        _gpuDebugRenderProgram.Uniform("ParticleOffset", particleOffset);
        _gpuDebugRenderProgram.Uniform("ParticleCount", particleCount);
        _gpuDebugRenderProgram.Uniform("PointColor", new Vector4(1.0f, 1.0f, 0.0f, 1.0f));
        _gpuDebugRenderProgram.Uniform("LineColor", new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
        _gpuDebugRenderProgram.Uniform("InterpolationAlpha", ComputeRenderAlpha());
        _gpuDebugRenderProgram.Uniform("InterpolationMode", (int)InterpolationMode);
        _gpuDebugRenderProgram.BindBuffer(particleBuffer, 0);
        _gpuDebugRenderProgram.BindBuffer(particleStaticBuffer, 1);
        _gpuDebugRenderProgram.BindBuffer(_gpuDebugPointsBuffer, 2);
        _gpuDebugRenderProgram.BindBuffer(_gpuDebugLinesBuffer, 3);

        uint groupCount = ((uint)particleCount + 127u) / 128u;
        _gpuDebugRenderProgram.DispatchCompute(groupCount, 1, 1);
        renderer.RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

        _gpuDebugPointsRenderer?.Material?.SetInt(1, particleCount);
        _gpuDebugLinesRenderer?.Material?.SetInt(1, particleCount);
        _gpuDebugPointsRenderer?.Render(null, (uint)particleCount);
        _gpuDebugLinesRenderer?.Render(null, (uint)particleCount);
    }

    private bool TryGetGpuParticleRenderSource(out XRDataBuffer? particleBuffer, out XRDataBuffer? particleStaticBuffer, out int particleOffset, out int particleCount)
    {
        if (!UseGPU)
        {
            particleBuffer = null;
            particleStaticBuffer = null;
            particleOffset = 0;
            particleCount = 0;
            return false;
        }

        if (UseBatchedDispatcher)
            return GPUPhysicsChainDispatcher.Instance.TryGetRenderParticleBuffers(this, out particleBuffer, out particleStaticBuffer, out particleOffset, out particleCount);

        particleBuffer = _particlesBuffer;
        particleStaticBuffer = _particleStaticBuffer;
        particleOffset = 0;
        particleCount = _totalParticleCount;
        return particleBuffer is not null && particleStaticBuffer is not null && particleCount > 0;
    }

    private void EnsureGpuDebugResources(uint particleCount)
    {
        const uint pointComponents = 8u;
        const uint lineComponents = 12u;

        if (_gpuDebugPointsBuffer is null || _gpuDebugPointsBuffer.ElementCount < particleCount)
        {
            _gpuDebugPointsBuffer?.Dispose();
            _gpuDebugPointsBuffer = new XRDataBuffer("PhysicsChainDebugPoints", EBufferTarget.ShaderStorageBuffer, Math.Max(particleCount, 1u), EComponentType.Float, pointComponents, false, false, true)
            {
                BindingIndexOverride = 0,
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false
            };
            _gpuDebugPointsBuffer.SetDataRaw(new float[Math.Max(particleCount, 1u) * pointComponents]);
            _gpuDebugPointsBuffer.PushData();
        }

        if (_gpuDebugLinesBuffer is null || _gpuDebugLinesBuffer.ElementCount < particleCount)
        {
            _gpuDebugLinesBuffer?.Dispose();
            _gpuDebugLinesBuffer = new XRDataBuffer("PhysicsChainDebugLines", EBufferTarget.ShaderStorageBuffer, Math.Max(particleCount, 1u), EComponentType.Float, lineComponents, false, false, true)
            {
                BindingIndexOverride = 0,
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false
            };
            _gpuDebugLinesBuffer.SetDataRaw(new float[Math.Max(particleCount, 1u) * lineComponents]);
            _gpuDebugLinesBuffer.PushData();
        }

        _gpuDebugPointsRenderer ??= new XRMeshRenderer(new XRMesh([new Vertex(Vector3.Zero)]), CreateGpuDebugPointMaterial());
        _gpuDebugLinesRenderer ??= new XRMeshRenderer(new XRMesh([new Vertex(Vector3.Zero)]), CreateGpuDebugLineMaterial());

        if (_gpuDebugPointsRenderer.Buffers is not null && _gpuDebugPointsBuffer is not null && !_gpuDebugPointsRenderer.Buffers.ContainsKey(_gpuDebugPointsBuffer.AttributeName))
            _gpuDebugPointsRenderer.Buffers.Add(_gpuDebugPointsBuffer.AttributeName, _gpuDebugPointsBuffer);

        if (_gpuDebugLinesRenderer.Buffers is not null && _gpuDebugLinesBuffer is not null && !_gpuDebugLinesRenderer.Buffers.ContainsKey(_gpuDebugLinesBuffer.AttributeName))
            _gpuDebugLinesRenderer.Buffers.Add(_gpuDebugLinesBuffer.AttributeName, _gpuDebugLinesBuffer);
    }

    private XRMaterial? CreateGpuDebugPointMaterial()
    {
        XRShader vertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"), EShaderType.Vertex);
        XRShader stereoMV2VertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitiveStereoMV2.vs"), EShaderType.Vertex);
        XRShader[] vertexShaders = Engine.Rendering.State.IsVulkan
            ? [vertShader, stereoMV2VertShader]
            :
            [
                vertShader,
                stereoMV2VertShader,
                ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitiveStereoNV.vs"), EShaderType.Vertex),
            ];

        XRShader geomShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "gs", "PointInstance.gs"), EShaderType.Geometry);
        XRShader fragShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitivePoint.fs"), EShaderType.Fragment);
        ShaderVar[] vars =
        [
            new ShaderFloat(0.005f, "PointSize"),
            new ShaderInt(0, "TotalPoints"),
        ];
        var mat = new XRMaterial(vars, [.. vertexShaders, geomShader, fragShader]);
        mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        mat.RenderOptions.CullMode = ECullMode.None;
        mat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        mat.RenderPass = (int)EDefaultRenderPass.OnTopForward;
        return mat;
    }

    private XRMaterial? CreateGpuDebugLineMaterial()
    {
        XRShader vertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"), EShaderType.Vertex);
        XRShader stereoMV2VertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitiveStereoMV2.vs"), EShaderType.Vertex);
        XRShader[] vertexShaders = Engine.Rendering.State.IsVulkan
            ? [vertShader, stereoMV2VertShader]
            :
            [
                vertShader,
                stereoMV2VertShader,
                ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitiveStereoNV.vs"), EShaderType.Vertex),
            ];

        XRShader geomShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "gs", "LineInstance.gs"), EShaderType.Geometry);
        XRShader fragShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitive.fs"), EShaderType.Fragment);
        ShaderVar[] vars =
        [
            new ShaderFloat(0.001f, "LineWidth"),
            new ShaderInt(0, "TotalLines"),
        ];
        var mat = new XRMaterial(vars, [.. vertexShaders, geomShader, fragShader]);
        mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        mat.RenderOptions.CullMode = ECullMode.None;
        mat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        mat.RenderPass = (int)EDefaultRenderPass.OnTopForward;
        return mat;
    }
}