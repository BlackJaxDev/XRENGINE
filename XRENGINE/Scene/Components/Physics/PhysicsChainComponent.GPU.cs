using Extensions;
using Silk.NET.OpenGL;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using XREngine.Scene.Transforms;
using GPUParticleData = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUParticleData;
using GPUParticleStaticData = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUParticleStaticData;
using GPUParticleTreeData = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUParticleTreeData;
using GPUColliderData = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUColliderData;
using GPUPerTreeParams = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUPerTreeParams;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    private static readonly int Matrix4x4SizeBytes = Marshal.SizeOf<Matrix4x4>();
    private static readonly int ColliderDataSizeBytes = Marshal.SizeOf<GPUColliderData>();

    [StructLayout(LayoutKind.Sequential)]
    private struct GPUDrivenBoneMappingData
    {
        public int ParticleIndex;
        public int ChildParticleIndex;
        public int BoneMatrixIndex;
        public int Flags;
        public Vector3 RestLocalDirection;
        public float _pad0;
    }

    private sealed class GpuDrivenRendererState(XRMeshRenderer renderer, XRDataBuffer mappingBuffer, uint[] drivenBoneIndices)
    {
        public XRMeshRenderer Renderer { get; } = renderer;
        public XRDataBuffer MappingBuffer { get; } = mappingBuffer;
        public uint[] DrivenBoneIndices { get; } = drivenBoneIndices;
        public int MappingCount => DrivenBoneIndices.Length;
    }

    private readonly record struct StandaloneInFlightReadback(
        XRDataBuffer ReadbackBuffer,
        IntPtr Fence,
        int ParticleCount,
        int ExecutionGeneration,
        long SubmissionId);

    private const int BonePaletteRotationFlag = 1;

    private XRRenderProgram? _mainPhysicsProgram;
    private XRRenderProgram? _skipUpdateParticlesProgram;
    private XRRenderProgram? _gpuDebugRenderProgram;
    private XRRenderProgram? _gpuBonePaletteProgram;
    private XRShader? _mainPhysicsShader;
    private XRShader? _skipUpdateParticlesShader;
    private XRShader? _gpuDebugRenderShader;
    private XRShader? _gpuBonePaletteShader;

    private XRDataBuffer? _particlesBuffer;
    private XRDataBuffer? _particleStaticBuffer;
    private XRDataBuffer? _transformMatricesBuffer;
    private XRDataBuffer? _collidersBuffer;
    private XRDataBuffer? _perTreeParamsBuffer;
    private XRDataBuffer? _gpuDebugPointsBuffer;
    private XRDataBuffer? _gpuDebugLinesBuffer;
    private XRMeshRenderer? _gpuDebugPointsRenderer;
    private XRMeshRenderer? _gpuDebugLinesRenderer;

    private readonly List<GPUParticleData> _particlesData = [];
    private readonly List<GPUParticleStaticData> _particleStaticData = [];
    private readonly List<GPUParticleTreeData> _particleTreesData = [];
    private readonly List<Matrix4x4> _transformMatrices = [];
    private readonly List<GPUColliderData> _collidersData = [];
    private readonly List<Matrix4x4> _preparedTransformSnapshot = [];
    private readonly List<GPUColliderData> _preparedColliderSnapshot = [];

    private int _totalParticleCount;
    private bool _buffersInitialized;

    private GPUParticleData[]? _readbackData;

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
    private int _preparedTransformSignature = int.MinValue;
    private int _preparedColliderSignature = int.MinValue;
    private int _preparedTransformSnapshotSignature = int.MinValue;
    private int _preparedColliderSnapshotSignature = int.MinValue;
    private int _preparedTransformDirtyStart;
    private int _preparedTransformDirtyLength;
    private int _preparedColliderDirtyStart;
    private int _preparedColliderDirtyLength;
    private int _uploadedStaticDataVersion = -1;
    private int _uploadedTransformSignature = int.MinValue;
    private int _uploadedColliderSignature = int.MinValue;
    private bool _gpuParticleStateInitialized;
    private readonly List<GpuDrivenRendererState> _gpuDrivenRenderers = [];
    private readonly List<StandaloneInFlightReadback> _standaloneReadbacks = [];
    private readonly Stack<XRDataBuffer> _standaloneReadbackBufferPool = [];

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
        ClearGpuDrivenRendererBindings();
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
        if (!UseGPU || UseBatchedDispatcher)
            return;

        TryCompleteStandaloneReadbacks();

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

        UpdatePerTreeParams(timeVar);

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

        InsertShaderStorageBarrier();
        PublishGpuDrivenBoneMatrices(_particlesBuffer, _transformMatricesBuffer, particleBaseOffset: 0);
        if (RequiresGpuReadback())
        {
            InsertShaderStorageBarrier();
            QueueStandaloneReadback();
        }
    }

    private void SubmitToBatchedDispatcher(int loopCount, float timeVar)
    {
        PrepareGPUData();
        long submissionId = ++_latestGpuSubmissionId;

        GPUPhysicsChainDispatcher.Instance.SubmitData(
            this,
            _particlesData,
            _particleStaticData,
            _particleTreesData,
            _transformMatrices,
            _collidersData,
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
            _particlesVersion,
            _particleStateVersion,
            _preparedTransformSignature,
            _preparedColliderSignature);
    }

    public void ApplyReadbackData(ReadOnlySpan<GPUPhysicsChainDispatcher.GPUParticleData> readbackData, int generation, long submissionId)
    {
        if (!UseGPU || generation != _gpuExecutionGeneration || submissionId <= _lastAppliedGpuSubmissionId)
            return;

        int particleIndex = 0;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            for (int particleTreeIndex = 0; particleTreeIndex < tree.Particles.Count; ++particleTreeIndex)
            {
                if (particleIndex >= readbackData.Length)
                    return;

                GPUPhysicsChainDispatcher.GPUParticleData data = readbackData[particleIndex];
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
        using var profilerState = Engine.Profiler.Start("PhysicsChainComponent.GPU.SynchronousReadback");

        if (_particlesBuffer is null)
            return;

        if (_readbackData is null || _readbackData.Length != _totalParticleCount)
            _readbackData = new GPUParticleData[_totalParticleCount];

        ReadParticleDataFromBuffer();
        ApplyReadbackDataToParticles();
    }

    private void QueueStandaloneReadback()
    {
        if (_particlesBuffer is null || _totalParticleCount <= 0)
            return;

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
            return;

        XRDataBuffer readbackBuffer = AcquireStandaloneReadbackBuffer((uint)_totalParticleCount);
        if (!TryGetBufferId(_particlesBuffer, out uint sourceBufferId)
            || !TryGetBufferId(readbackBuffer, out uint destinationBufferId))
        {
            _standaloneReadbackBufferPool.Push(readbackBuffer);
            NotifyGpuReadbackUnavailable("standalone-readback-buffer");
            return;
        }

        nuint byteSize = (nuint)(_totalParticleCount * Marshal.SizeOf<GPUParticleData>());
        renderer.RawGL.CopyNamedBufferSubData(sourceBufferId, destinationBufferId, 0, 0, byteSize);
        GPUPhysicsChainDispatcher.RecordGpuCopyBytes((long)byteSize, isBatched: false);

        IntPtr fence = renderer.RawGL.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        if (fence == IntPtr.Zero)
        {
            _standaloneReadbackBufferPool.Push(readbackBuffer);
            NotifyGpuReadbackUnavailable("standalone-readback-fence");
            return;
        }

        _standaloneReadbacks.Add(new StandaloneInFlightReadback(
            readbackBuffer,
            fence,
            _totalParticleCount,
            _gpuExecutionGeneration,
            _latestGpuSubmissionId));
    }

    private void TryCompleteStandaloneReadbacks()
    {
        if (_standaloneReadbacks.Count == 0)
            return;

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
            return;

        for (int i = _standaloneReadbacks.Count - 1; i >= 0; --i)
        {
            StandaloneInFlightReadback entry = _standaloneReadbacks[i];
            if (!IsFenceComplete(entry.Fence, renderer))
                continue;

            EnsureStandaloneReadbackArray(entry.ParticleCount);
            if (_readbackData is not null)
                ReadParticleDataFromBuffer(entry.ReadbackBuffer, _readbackData.AsSpan(0, entry.ParticleCount), renderer);

            if (_readbackData is not null)
                ApplyReadbackData(_readbackData.AsSpan(0, entry.ParticleCount), entry.ExecutionGeneration, entry.SubmissionId);

            if (entry.Fence != IntPtr.Zero)
                renderer.RawGL.DeleteSync(entry.Fence);

            _standaloneReadbackBufferPool.Push(entry.ReadbackBuffer);
            _standaloneReadbacks.RemoveAt(i);
        }
    }

    private void EnsureStandaloneReadbackArray(int particleCount)
    {
        if (_readbackData is null || _readbackData.Length != particleCount)
            _readbackData = new GPUParticleData[particleCount];
    }

    private void ReadParticleDataFromBuffer()
    {
        if (_particlesBuffer is null || _readbackData is null)
            return;

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
            return;

        ReadParticleDataFromBuffer(_particlesBuffer, _readbackData.AsSpan(), renderer);
    }

    private static unsafe void ReadParticleDataFromBuffer(XRDataBuffer buffer, Span<GPUParticleData> readbackData, OpenGLRenderer renderer)
    {
        if (readbackData.IsEmpty)
            return;

        if (!TryGetBufferId(buffer, out uint bufferId) || bufferId == 0)
            return;

        nuint byteSize = (nuint)(readbackData.Length * Marshal.SizeOf<GPUParticleData>());
        fixed (GPUParticleData* ptr = readbackData)
        {
            renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, bufferId);
            renderer.RawGL.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, byteSize, ptr);
            renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
            GPUPhysicsChainDispatcher.RecordCpuReadbackBytes((long)byteSize, isBatched: false);
        }
    }

    private static bool TryGetBufferId(XRDataBuffer buffer, out uint bufferId)
    {
        bufferId = 0;
        if (buffer.APIWrappers.FirstOrDefault() is not OpenGLRenderer.GLDataBuffer glBuffer)
            return false;

        return glBuffer.TryGetBindingId(out bufferId) && bufferId != 0;
    }

    private static bool IsFenceComplete(IntPtr fence, OpenGLRenderer renderer)
    {
        if (fence == IntPtr.Zero)
            return false;

        GLEnum status = renderer.RawGL.ClientWaitSync(fence, 0u, 0u);
        return status == GLEnum.AlreadySignaled || status == GLEnum.ConditionSatisfied;
    }

    private XRDataBuffer AcquireStandaloneReadbackBuffer(uint particleCount)
    {
        uint elementCount = Math.Max(particleCount, 1u);
        while (_standaloneReadbackBufferPool.Count > 0)
        {
            XRDataBuffer buffer = _standaloneReadbackBufferPool.Pop();
            if (buffer.ElementCount >= elementCount)
                return buffer;

            buffer.Dispose();
        }

        var created = new XRDataBuffer("PhysicsChainStandaloneReadback", EBufferTarget.ShaderStorageBuffer, elementCount, EComponentType.Float, 16, false, false)
        {
            DisposeOnPush = false,
            Usage = EBufferUsage.StreamRead,
        };
        created.SetDataRaw(new GPUParticleData[elementCount]);
        created.PushData();
        return created;
    }

    private void CleanupStandaloneReadbacks()
    {
        if (AbstractRenderer.Current is OpenGLRenderer renderer)
        {
            for (int i = 0; i < _standaloneReadbacks.Count; ++i)
            {
                StandaloneInFlightReadback entry = _standaloneReadbacks[i];
                if (entry.Fence != IntPtr.Zero)
                    renderer.RawGL.DeleteSync(entry.Fence);
                _standaloneReadbackBufferPool.Push(entry.ReadbackBuffer);
            }
        }
        else
        {
            for (int i = 0; i < _standaloneReadbacks.Count; ++i)
                _standaloneReadbackBufferPool.Push(_standaloneReadbacks[i].ReadbackBuffer);
        }

        _standaloneReadbacks.Clear();

        while (_standaloneReadbackBufferPool.Count > 0)
            _standaloneReadbackBufferPool.Pop().Dispose();
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

                GPUParticleData data = _readbackData[particleIndex];
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

    internal bool RequiresGpuReadback()
        => GpuSyncToBones;

    internal void NotifyGpuReadbackUnavailable(string reason)
    {
        if (!GpuSyncToBones)
            return;

        LogFault(
            $"GpuReadbackUnavailable:{GetHashCode()}:{reason}",
            $"Async GPU readback was unavailable for compatibility sync mode on {FormatRoot(Root)}. Keeping the previous CPU bone pose. Reason={reason}.");
    }

    private void ApplyGpuResultsToTransforms(bool newSimulationResults = false)
    {
        if (!GpuSyncToBones)
            return;

        ApplyCurrentParticleTransforms(newSimulationResults);

        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            using (Engine.Profiler.Start("PhysicsChainComponent.ApplyGpuResultsToTransforms.HierarchyRecalc"))
            {
                long hierarchyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                _particleTrees[treeIndex].Root.RecalculateMatrixHierarchy(
                    forceWorldRecalc: true,
                    setRenderMatrixNow: true,
                    childRecalcType: Engine.Rendering.Settings.RecalcChildMatricesLoopType).Wait();
                GPUPhysicsChainDispatcher.RecordHierarchyRecalcTicks(System.Diagnostics.Stopwatch.GetTimestamp() - hierarchyStart);
            }
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
        if (_mainPhysicsProgram is null || _particlesBuffer is null || _particleStaticBuffer is null || _transformMatricesBuffer is null || _collidersBuffer is null || _perTreeParamsBuffer is null)
            return;

        _mainPhysicsProgram.Uniform("ApplyObjectMove", applyObjectMove ? 1 : 0);

        _mainPhysicsProgram.BindBuffer(_particlesBuffer, 0);
        _mainPhysicsProgram.BindBuffer(_particleStaticBuffer, 1);
        _mainPhysicsProgram.BindBuffer(_transformMatricesBuffer, 3);
        _mainPhysicsProgram.BindBuffer(_collidersBuffer, 4);
        _mainPhysicsProgram.BindBuffer(_perTreeParamsBuffer, 5);

        int threadGroupsX = (_totalParticleCount + 127) / 128;
        _mainPhysicsProgram.DispatchCompute((uint)threadGroupsX, 1, 1);
    }

    private void DispatchSkipUpdateParticles()
    {
        if (_skipUpdateParticlesProgram is null || _particlesBuffer is null || _particleStaticBuffer is null || _transformMatricesBuffer is null || _perTreeParamsBuffer is null)
            return;

        _skipUpdateParticlesProgram.BindBuffer(_particlesBuffer, 0);
        _skipUpdateParticlesProgram.BindBuffer(_particleStaticBuffer, 1);
        _skipUpdateParticlesProgram.BindBuffer(_transformMatricesBuffer, 3);
        _skipUpdateParticlesProgram.BindBuffer(_perTreeParamsBuffer, 5);

        int threadGroupsX = (_totalParticleCount + 127) / 128;
        _skipUpdateParticlesProgram.DispatchCompute((uint)threadGroupsX, 1, 1);
    }

    private void InitializeBuffers()
    {
        CleanupBuffers();
        PrepareGPUData();

        _particlesBuffer = new XRDataBuffer("Particles", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_particlesData.Count, 1), EComponentType.Float, 16, false, false);
        _particlesBuffer.SetBlockIndex(0);

        _particleStaticBuffer = new XRDataBuffer("ParticleStatic", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_particleStaticData.Count, 1), EComponentType.Float, 16, false, false);
        _particleStaticBuffer.SetBlockIndex(1);

        _transformMatricesBuffer = new XRDataBuffer("TransformMatrices", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_transformMatrices.Count, 1), EComponentType.Float, 16, false, false);
        _transformMatricesBuffer.SetBlockIndex(3);

        _collidersBuffer = new XRDataBuffer("Colliders", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_collidersData.Count, 1), EComponentType.Float, 12, false, false);
        _collidersBuffer.SetBlockIndex(4);

        _perTreeParamsBuffer = new XRDataBuffer("PerTreeParams", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_particleTreesData.Count, 1), EComponentType.Float, 24, false, false);
        _perTreeParamsBuffer.SetBlockIndex(5);

        UpdateBufferData();
        _buffersInitialized = true;
    }

    private void UpdateBufferData()
    {
        using var profilerState = Engine.Profiler.Start("PhysicsChainComponent.GPU.UpdateBufferData");

        PrepareGPUData();
        if (!_gpuParticleStateInitialized || RequiresGpuReadback())
        {
            bool particlesResized = _particlesBuffer?.EnsureRawCapacity<GPUParticleData>((uint)_particlesData.Count) ?? false;
            uint particleBytes = _particlesBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_particlesData)) ?? 0u;
            bool fullParticlePush = particlesResized || !_buffersInitialized;
            PushBufferUpdate(_particlesBuffer, fullParticlePush, particleBytes);
            GPUPhysicsChainDispatcher.RecordCpuUploadBytes(fullParticlePush ? _particlesBuffer?.Length ?? 0u : particleBytes, isBatched: false);
            _gpuParticleStateInitialized = true;
        }
        if (_uploadedStaticDataVersion != _preparedGpuDataVersion)
        {
            bool staticResized = _particleStaticBuffer?.EnsureRawCapacity<GPUParticleStaticData>((uint)_particleStaticData.Count) ?? false;
            uint staticBytes = _particleStaticBuffer?.WriteDataRaw(CollectionsMarshal.AsSpan(_particleStaticData)) ?? 0u;
            bool fullStaticPush = staticResized || !_buffersInitialized;
            PushBufferUpdate(_particleStaticBuffer, fullStaticPush, staticBytes);
            GPUPhysicsChainDispatcher.RecordCpuUploadBytes(fullStaticPush ? _particleStaticBuffer?.Length ?? 0u : staticBytes, isBatched: false);
            _uploadedStaticDataVersion = _preparedGpuDataVersion;
        }
        bool transformsResized = _transformMatricesBuffer?.EnsureRawCapacity<Matrix4x4>((uint)_transformMatrices.Count) ?? false;
        bool transformsDirty = _uploadedTransformSignature != _preparedTransformSignature;
        if (transformsDirty || transformsResized || !_buffersInitialized)
        {
            ReadOnlySpan<Matrix4x4> transformSpan = CollectionsMarshal.AsSpan(_transformMatrices);
            bool fullTransformPush = transformsResized || !_buffersInitialized || _preparedTransformDirtyLength <= 0 || _preparedTransformDirtyLength >= _transformMatrices.Count;
            uint transformBytes = fullTransformPush
                ? _transformMatricesBuffer?.WriteDataRaw(transformSpan) ?? 0u
                : _transformMatricesBuffer?.WriteDataRaw(transformSpan.Slice(_preparedTransformDirtyStart, _preparedTransformDirtyLength), (uint)_preparedTransformDirtyStart) ?? 0u;
            PushBufferUpdate(_transformMatricesBuffer, fullTransformPush, transformBytes, _preparedTransformDirtyStart * Matrix4x4SizeBytes);
            GPUPhysicsChainDispatcher.RecordCpuUploadBytes(fullTransformPush ? _transformMatricesBuffer?.Length ?? 0u : transformBytes, isBatched: false);
            _uploadedTransformSignature = _preparedTransformSignature;
        }

        bool collidersResized = _collidersBuffer?.EnsureRawCapacity<GPUColliderData>((uint)_collidersData.Count) ?? false;
        bool collidersDirty = _uploadedColliderSignature != _preparedColliderSignature;
        if (collidersDirty || collidersResized || !_buffersInitialized)
        {
            ReadOnlySpan<GPUColliderData> colliderSpan = CollectionsMarshal.AsSpan(_collidersData);
            bool fullColliderPush = collidersResized || !_buffersInitialized || _preparedColliderDirtyLength <= 0 || _preparedColliderDirtyLength >= _collidersData.Count;
            uint colliderBytes = fullColliderPush
                ? _collidersBuffer?.WriteDataRaw(colliderSpan) ?? 0u
                : _collidersBuffer?.WriteDataRaw(colliderSpan.Slice(_preparedColliderDirtyStart, _preparedColliderDirtyLength), (uint)_preparedColliderDirtyStart) ?? 0u;
            PushBufferUpdate(_collidersBuffer, fullColliderPush, colliderBytes, _preparedColliderDirtyStart * ColliderDataSizeBytes);
            GPUPhysicsChainDispatcher.RecordCpuUploadBytes(fullColliderPush ? _collidersBuffer?.Length ?? 0u : colliderBytes, isBatched: false);
            _uploadedColliderSignature = _preparedColliderSignature;
        }
    }

    private void UpdatePerTreeParams(float timeVar)
    {
        using var profilerState = Engine.Profiler.Start("PhysicsChainComponent.GPU.UpdatePerTreeParams");

        if (_perTreeParamsBuffer is null)
            return;

        Span<GPUPerTreeParams> paramsSpan = _particleTreesData.Count > 0
            ? stackalloc GPUPerTreeParams[_particleTreesData.Count]
            : stackalloc GPUPerTreeParams[1];

        for (int i = 0; i < _particleTreesData.Count; i++)
        {
            paramsSpan[i] = new GPUPerTreeParams
            {
                DeltaTime = timeVar,
                ObjectScale = _objectScale,
                Weight = _weight,
                FreezeAxis = (int)FreezeAxis,
                Force = Force,
                ColliderCount = _collidersData.Count,
                Gravity = Gravity,
                ColliderOffset = 0,
                ObjectMove = _objectMove,
                RestGravity = _particleTreesData[i].RestGravity,
                UpdateMode = (int)UpdateMode,
            };
        }

        bool perTreeResized = _perTreeParamsBuffer.EnsureRawCapacity<GPUPerTreeParams>((uint)_particleTreesData.Count);
        uint perTreeBytes = _perTreeParamsBuffer.WriteDataRaw(paramsSpan);
        bool fullPerTreePush = perTreeResized || !_buffersInitialized;
        PushBufferUpdate(_perTreeParamsBuffer, fullPerTreePush, perTreeBytes);
        GPUPhysicsChainDispatcher.RecordCpuUploadBytes(fullPerTreePush ? _perTreeParamsBuffer.Length : perTreeBytes, isBatched: false);
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

    private static void UpdatePreparedDirtyRange<T>(List<T> current, List<T> snapshot, ref int snapshotSignature, int currentSignature, out int dirtyStart, out int dirtyLength) where T : struct
    {
        if (snapshotSignature == currentSignature && snapshot.Count == current.Count)
        {
            dirtyStart = 0;
            dirtyLength = 0;
            return;
        }

        if (snapshot.Count != current.Count)
        {
            snapshot.Clear();
            snapshot.AddRange(current);
            snapshotSignature = currentSignature;
            dirtyStart = 0;
            dirtyLength = current.Count;
            return;
        }

        int firstChanged = -1;
        int lastChanged = -1;
        for (int i = 0; i < current.Count; ++i)
        {
            T currentValue = current[i];
            if (EqualityComparer<T>.Default.Equals(currentValue, snapshot[i]))
                continue;

            snapshot[i] = currentValue;
            if (firstChanged < 0)
                firstChanged = i;
            lastChanged = i;
        }

        snapshotSignature = currentSignature;
        dirtyStart = firstChanged >= 0 ? firstChanged : 0;
        dirtyLength = firstChanged >= 0 ? lastChanged - firstChanged + 1 : 0;
    }

    private void PrepareGPUData()
    {
        using var profilerState = Engine.Profiler.Start("PhysicsChainComponent.GPU.PrepareGPUData");

        var transformHash = new HashCode();
        var colliderHash = new HashCode();

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
            GPUParticleTreeData treeData = new()
            {
                RestGravity = tree.RestGravity,
            };

            if (rebuildPreparedData)
                _particleTreesData.Add(treeData);
            else
                _particleTreesData[treeIndex] = treeData;

            for (int particleIndex = 0; particleIndex < tree.Particles.Count; ++particleIndex)
            {
                Particle particle = tree.Particles[particleIndex];
                GPUParticleData particleData = new()
                {
                    Position = particle.Position,
                    PrevPosition = particle.PrevPosition,
                    IsColliding = particle.IsColliding ? 1 : 0,
                    PreviousPhysicsPosition = particle.PreviousPhysicsPosition
                };

                GPUParticleStaticData particleStaticData = new()
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
                    TreeIndex = treeIndex,
                };

                _particlesData.Add(particleData);
                if (rebuildPreparedData)
                    _particleStaticData.Add(particleStaticData);

                Matrix4x4 transformMatrix = particle.Transform is not null
                    ? particle.TransformLocalToWorldMatrix
                    : particle.ParentIndex >= 0
                        ? tree.Particles[particle.ParentIndex].TransformLocalToWorldMatrix
                        : Matrix4x4.Identity;

                _transformMatrices.Add(transformMatrix);
                transformHash.Add(transformMatrix);

                ++particleCursor;
            }

            _totalParticleCount += tree.Particles.Count;
        }

        _preparedGpuDataVersion = _particlesVersion;
        _preparedTransformSignature = transformHash.ToHashCode();
        UpdatePreparedDirtyRange(_transformMatrices, _preparedTransformSnapshot, ref _preparedTransformSnapshotSignature, _preparedTransformSignature, out _preparedTransformDirtyStart, out _preparedTransformDirtyLength);

        if (_effectiveColliders is null)
        {
            _preparedColliderSignature = colliderHash.ToHashCode();
            UpdatePreparedDirtyRange(_collidersData, _preparedColliderSnapshot, ref _preparedColliderSnapshotSignature, _preparedColliderSignature, out _preparedColliderDirtyStart, out _preparedColliderDirtyLength);
            return;
        }

        for (int colliderIndex = 0; colliderIndex < _effectiveColliders.Count; ++colliderIndex)
        {
            PhysicsChainColliderBase collider = _effectiveColliders[colliderIndex];
            if (collider is PhysicsChainSphereCollider sphereCollider)
            {
                GPUColliderData colliderData = new()
                {
                    Center = new Vector4(sphereCollider.Transform.WorldTranslation, sphereCollider.Radius),
                    Type = 0
                };
                _collidersData.Add(colliderData);
                AddColliderSignature(ref colliderHash, colliderData);
            }
            else if (collider is PhysicsChainCapsuleCollider capsuleCollider)
            {
                Vector3 start = capsuleCollider.Transform.WorldTranslation;
                Vector3 end = capsuleCollider.Transform.TransformPoint(new Vector3(0.0f, capsuleCollider.Height, 0.0f));
                GPUColliderData colliderData = new()
                {
                    Center = new Vector4(start, capsuleCollider.Radius),
                    Params = new Vector4(end, 0.0f),
                    Type = 1
                };
                _collidersData.Add(colliderData);
                AddColliderSignature(ref colliderHash, colliderData);
            }
            else if (collider is PhysicsChainBoxCollider boxCollider)
            {
                GPUColliderData colliderData = new()
                {
                    Center = new Vector4(boxCollider.Transform.WorldTranslation, 0.0f),
                    Params = new Vector4(boxCollider.Size * 0.5f, 0.0f),
                    Type = 2
                };
                _collidersData.Add(colliderData);
                AddColliderSignature(ref colliderHash, colliderData);
            }
            else if (collider is PhysicsChainPlaneCollider planeCollider)
            {
                GPUColliderData colliderData = new()
                {
                    Center = new Vector4(planeCollider.Transform.TransformPoint(planeCollider._center), 0.0f),
                    Params = new Vector4(planeCollider._plane.Normal, planeCollider._bound == PhysicsChainColliderBase.EBound.Inside ? 1.0f : 0.0f),
                    Type = 3
                };
                _collidersData.Add(colliderData);
                AddColliderSignature(ref colliderHash, colliderData);
            }
        }

        _preparedColliderSignature = colliderHash.ToHashCode();
        UpdatePreparedDirtyRange(_collidersData, _preparedColliderSnapshot, ref _preparedColliderSnapshotSignature, _preparedColliderSignature, out _preparedColliderDirtyStart, out _preparedColliderDirtyLength);
    }

    private static void AddColliderSignature(ref HashCode hash, GPUColliderData colliderData)
    {
        hash.Add(colliderData.Center);
        hash.Add(colliderData.Params);
        hash.Add(colliderData.Type);
    }

    private void CleanupBuffers()
    {
        CleanupStandaloneReadbacks();
        _particlesBuffer?.Dispose();
        _particleStaticBuffer?.Dispose();
        _transformMatricesBuffer?.Dispose();
        _collidersBuffer?.Dispose();
        _perTreeParamsBuffer?.Dispose();
        _gpuDebugPointsBuffer?.Dispose();
        _gpuDebugLinesBuffer?.Dispose();
        _gpuDebugPointsRenderer?.Destroy();
        _gpuDebugLinesRenderer?.Destroy();

        _particlesBuffer = null;
        _particleStaticBuffer = null;
        _transformMatricesBuffer = null;
        _collidersBuffer = null;
        _perTreeParamsBuffer = null;
        _gpuDebugPointsBuffer = null;
        _gpuDebugLinesBuffer = null;
        _gpuDebugPointsRenderer = null;
        _gpuDebugLinesRenderer = null;
        _buffersInitialized = false;

        _readbackData = null;
        _preparedTransformSnapshot.Clear();
        _preparedColliderSnapshot.Clear();
        _preparedTransformSnapshotSignature = int.MinValue;
        _preparedColliderSnapshotSignature = int.MinValue;
        _preparedTransformDirtyStart = 0;
        _preparedTransformDirtyLength = 0;
        _preparedColliderDirtyStart = 0;
        _preparedColliderDirtyLength = 0;
        _uploadedStaticDataVersion = -1;
        _uploadedTransformSignature = int.MinValue;
        _uploadedColliderSignature = int.MinValue;
        _gpuParticleStateInitialized = false;
    }

    private void CleanupPrograms()
    {
        _mainPhysicsProgram?.Destroy();
        _skipUpdateParticlesProgram?.Destroy();
        _gpuDebugRenderProgram?.Destroy();
        _gpuBonePaletteProgram?.Destroy();

        _mainPhysicsProgram = null;
        _skipUpdateParticlesProgram = null;
        _gpuDebugRenderProgram = null;
        _gpuBonePaletteProgram = null;
        _mainPhysicsShader = null;
        _skipUpdateParticlesShader = null;
        _gpuDebugRenderShader = null;
        _gpuBonePaletteShader = null;
    }

    private void RebuildGpuDrivenRendererBindings()
    {
        ClearGpuDrivenRendererBindings();

        if (!UseGPU || SceneNode is null || _particleTrees.Count == 0)
            return;

        _gpuDrivenParticleIndexByTransform.Clear();
        _gpuDrivenFirstChildIndexByParticle.Clear();
        _gpuDrivenRestDirectionByParticle.Clear();

        int globalParticleIndex = 0;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            List<Particle> particles = tree.Particles;
            int treeBase = globalParticleIndex;

            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex, ++globalParticleIndex)
            {
                Particle particle = particles[particleIndex];
                if (particle.Transform is not null)
                    _gpuDrivenParticleIndexByTransform[particle.Transform] = treeBase + particleIndex;
            }

            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
            {
                Particle particle = particles[particleIndex];
                if (particle.ParentIndex < 0)
                    continue;

                int parentGlobalIndex = treeBase + particle.ParentIndex;
                if (_gpuDrivenFirstChildIndexByParticle.ContainsKey(parentGlobalIndex))
                    continue;

                int childGlobalIndex = treeBase + particleIndex;
                _gpuDrivenFirstChildIndexByParticle[parentGlobalIndex] = childGlobalIndex;
                _gpuDrivenRestDirectionByParticle[parentGlobalIndex] = particle.Transform is not null
                    ? particle.InitLocalPosition
                    : particle.EndOffset;
            }
        }

        // Scan from parent so we also discover skinned renderers on sibling nodes
        // (e.g. a ModelComponent alongside the skeleton root rather than under it).
        // The particle-transform filter ensures only renderers referencing our bones match.
        var searchRoot = SceneNode.Parent ?? SceneNode;
        searchRoot.IterateComponents<ModelComponent>(model =>
        {
            foreach (XRMeshRenderer renderer in model.GetAllRenderersWhere(static renderer => renderer.Mesh?.HasSkinning == true))
                TryAddGpuDrivenRendererState(renderer, _gpuDrivenParticleIndexByTransform, _gpuDrivenFirstChildIndexByParticle, _gpuDrivenRestDirectionByParticle);
        }, true);
    }

    /// <summary>
    /// Forces a re-scan of the node subtree for skinned renderers whose bones
    /// are driven by this physics chain. Call after dynamically adding a
    /// <see cref="ModelComponent"/> with a skinned mesh that references chain bones.
    /// </summary>
    public void InvalidateGpuDrivenRenderers()
    {
        if (!UseGPU || _particleTrees.Count == 0)
            return;

        RebuildGpuDrivenRendererBindings();
    }

    private void TryAddGpuDrivenRendererState(
        XRMeshRenderer renderer,
        Dictionary<Transform, int> particleIndexByTransform,
        Dictionary<int, int> firstChildIndexByParticle,
        Dictionary<int, Vector3> restDirectionByParticle)
    {
        XRMesh? mesh = renderer.Mesh;
        if (mesh?.UtilizedBones is not { Length: > 0 } || renderer.BoneMatricesBuffer is null)
            return;

        List<GPUDrivenBoneMappingData> mappingData = [];
        List<uint> drivenBoneIndices = [];

        for (int boneIndex = 0; boneIndex < mesh.UtilizedBones.Length; ++boneIndex)
        {
            var (boneTransform, _) = mesh.UtilizedBones[boneIndex];
            if (boneTransform is not Transform transform || !particleIndexByTransform.TryGetValue(transform, out int particleIndex))
                continue;

            int childParticleIndex = -1;
            int flags = 0;
            Vector3 restLocalDirection = Vector3.Zero;
            if (firstChildIndexByParticle.TryGetValue(particleIndex, out int firstChildIndex) && restDirectionByParticle.TryGetValue(particleIndex, out Vector3 localDirection))
            {
                childParticleIndex = firstChildIndex;
                flags |= BonePaletteRotationFlag;
                restLocalDirection = localDirection;
            }

            mappingData.Add(new GPUDrivenBoneMappingData
            {
                ParticleIndex = particleIndex,
                ChildParticleIndex = childParticleIndex,
                BoneMatrixIndex = boneIndex + 1,
                Flags = flags,
                RestLocalDirection = restLocalDirection,
            });
            drivenBoneIndices.Add((uint)(boneIndex + 1));
        }

        if (mappingData.Count == 0)
            return;

        XRDataBuffer mappingBuffer = new(
            $"PhysicsChainBonePaletteMap_{renderer.GetHashCode():X}",
            EBufferTarget.ShaderStorageBuffer,
            (uint)mappingData.Count,
            EComponentType.Float,
            8,
            false,
            false)
        {
            Usage = EBufferUsage.StaticDraw,
            DisposeOnPush = false
        };
        mappingBuffer.SetDataRaw(mappingData);
        mappingBuffer.PushData();

        uint[] drivenIndices = drivenBoneIndices.ToArray();
        renderer.RegisterGpuDrivenBoneIndices(drivenIndices);
        _gpuDrivenRenderers.Add(new GpuDrivenRendererState(renderer, mappingBuffer, drivenIndices));
    }

    private void ClearGpuDrivenRendererBindings()
    {
        for (int i = 0; i < _gpuDrivenRenderers.Count; ++i)
        {
            GpuDrivenRendererState state = _gpuDrivenRenderers[i];
            state.Renderer.UnregisterGpuDrivenBoneIndices(state.DrivenBoneIndices);
            state.MappingBuffer.Destroy();
        }

        _gpuDrivenRenderers.Clear();
    }

    private void EnsureGpuBonePaletteProgram()
    {
        if (_gpuBonePaletteProgram is not null)
            return;

        _gpuBonePaletteShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/PhysicsChainBonePalette.comp", EShaderType.Compute);
        _gpuBonePaletteProgram = new XRRenderProgram(true, false, _gpuBonePaletteShader);
    }

    internal void PublishGpuDrivenBoneMatrices(XRDataBuffer? particlesBuffer, XRDataBuffer? transformMatricesBuffer, int particleBaseOffset)
    {
        using var profilerState = Engine.Profiler.Start("PhysicsChainComponent.GPU.PublishGpuDrivenBoneMatrices");

        if (particlesBuffer is null || transformMatricesBuffer is null || _gpuDrivenRenderers.Count == 0)
            return;

        EnsureGpuBonePaletteProgram();
        if (_gpuBonePaletteProgram is null)
            return;

        for (int i = 0; i < _gpuDrivenRenderers.Count; ++i)
        {
            GpuDrivenRendererState state = _gpuDrivenRenderers[i];
            XRDataBuffer? outputBoneMatrices = state.Renderer.BoneMatricesBuffer;
            if (outputBoneMatrices is null || state.MappingCount <= 0)
                continue;

            _gpuBonePaletteProgram.Uniform("particleBaseOffset", particleBaseOffset);
            _gpuBonePaletteProgram.Uniform("mappingCount", state.MappingCount);
            _gpuBonePaletteProgram.BindBuffer(particlesBuffer, 0);
            _gpuBonePaletteProgram.BindBuffer(transformMatricesBuffer, 1);
            _gpuBonePaletteProgram.BindBuffer(state.MappingBuffer, 2);
            _gpuBonePaletteProgram.BindBuffer(outputBoneMatrices, 3);

            uint groupsX = (uint)(state.MappingCount + 63) / 64u;
            _gpuBonePaletteProgram.DispatchCompute(Math.Max(groupsX, 1u), 1u, 1u);
            InsertShaderStorageBarrier();
        }
    }

    private void RenderGpuDebug()
    {
        if (!DebugDrawChains)
            return;

        if (AbstractRenderer.Current is not OpenGLRenderer renderer || !Engine.Rendering.State.DebugInstanceRenderingAvailable)
            return;

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