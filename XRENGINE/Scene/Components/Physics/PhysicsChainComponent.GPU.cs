using XREngine.Extensions;
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
using XREngine.Scene.Transforms;
using GPUParticleData = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUParticleData;
using GPUParticleStaticData = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUParticleStaticData;
using GPUParticleTreeData = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUParticleTreeData;
using GPUColliderData = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUColliderData;
using GPUDrivenBoneMappingData = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUDrivenBoneMappingData;
using GPUPerTreeParams = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUPerTreeParams;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    private static readonly bool VerboseGpuDrivenRendererLogging = false;
    private const int GpuDrivenRendererBindingRetryFrameCount = 8;

    private sealed class GpuDrivenRendererState(
        XRMeshRenderer renderer,
        XRDataBuffer<GPUDrivenBoneMappingData> mappingBuffer,
        GPUDrivenBoneMappingData[] mappingData,
        uint[] drivenBoneIndices,
        uint boneMatrixElementCount,
        bool drivesCompleteBonePalette)
    {
        public XRMeshRenderer Renderer { get; } = renderer;
        public XRDataBuffer<GPUDrivenBoneMappingData> MappingBuffer { get; } = mappingBuffer;
        public GPUDrivenBoneMappingData[] MappingData { get; } = mappingData;
        public uint[] DrivenBoneIndices { get; } = drivenBoneIndices;
        public uint BoneMatrixElementCount { get; } = boneMatrixElementCount;
        public bool DrivesCompleteBonePalette { get; } = drivesCompleteBonePalette;
        public int MappingCount => MappingData.Length;
    }

    private const int BonePaletteRotationFlag = 1;

    private XRRenderProgram? _gpuBonePaletteProgram;
    private XRShader? _gpuBonePaletteShader;

    private readonly List<GPUParticleData> _particlesData = [];
    private readonly List<GPUParticleStaticData> _particleStaticData = [];
    private readonly List<GPUParticleTreeData> _particleTreesData = [];
    private readonly List<Matrix4x4> _transformMatrices = [];
    private readonly List<GPUColliderData> _collidersData = [];
    private int _totalParticleCount;

    private bool _pendingGpuExecutionReconfigure;
    private int _gpuExecutionGeneration;
    private long _latestGpuSubmissionId;
    private long _lastAppliedGpuSubmissionId;

    /// <summary>Current global GPU physics-chain readback health snapshot.</summary>
    public PhysicsChainReadbackDiagnostics GpuReadbackDiagnostics
        => GPUPhysicsChainDispatcher.Instance.GetReadbackDiagnosticsSnapshot();

    /// <summary>Current global GPU physics-chain dispatch failure snapshot.</summary>
    public PhysicsChainDispatchDiagnostics GpuDispatchDiagnostics
        => GPUPhysicsChainDispatcher.Instance.GetDispatchDiagnosticsSnapshot();

    /// <summary>Current global resident-arena capacity, utilization, growth, and retirement snapshot.</summary>
    public GPUPhysicsChainArenaDiagnostics GpuArenaDiagnostics
        => GPUPhysicsChainDispatcher.Instance.GetArenaDiagnosticsSnapshot();

    /// <summary>Current global GPU-authored active-work and indirect-dispatch snapshot.</summary>
    public PhysicsChainActiveWorkDiagnostics GpuActiveWorkDiagnostics
        => GPUPhysicsChainDispatcher.Instance.GetActiveWorkDiagnosticsSnapshot();

    /// <summary>Current global GPU physics-chain transfer and hierarchy-work snapshot.</summary>
    public GPUPhysicsChainBandwidthSnapshot GpuBandwidthDiagnostics
        => GPUPhysicsChainDispatcher.GetBandwidthPressureSnapshot();

    private int _preparedGpuDataVersion = -1;
    private int _preparedParticleStateVersion = -1;
    private int _preparedTransformSignature = int.MinValue;
    private int _preparedColliderSignature = int.MinValue;
    private bool _gpuDispatcherRegistered;
    private bool _gpuDrivenRendererBindingsDirty = true;
    private int _gpuDrivenRendererBindingRetryFrames;
    private int _gpuDrivenRendererBindingGeneration;
    private readonly List<GpuDrivenRendererState> _gpuDrivenRenderers = [];

    internal bool HasGpuDrivenRenderers => _gpuDrivenRenderers.Count > 0;
    internal int GpuDrivenRendererBindingGeneration => _gpuDrivenRendererBindingGeneration;

    private void ActivateGpuExecutionMode()
    {
        if (!UseGPU || !IsActiveInHierarchy || _gpuDispatcherRegistered)
            return;

        unchecked
        {
            ++_gpuExecutionGeneration;
        }

        GPUPhysicsChainDispatcher.Instance.Register(this);
        _gpuDispatcherRegistered = true;
    }

    private void DeactivateGpuExecutionMode()
    {
        unchecked
        {
            ++_gpuExecutionGeneration;
        }

        if (_gpuDispatcherRegistered)
        {
            GPUPhysicsChainDispatcher.Instance.Unregister(this);
            _gpuDispatcherRegistered = false;
        }

        ClearGpuDrivenRendererBindings();
        _gpuDrivenRendererBindingsDirty = true;
        _gpuDrivenRendererBindingRetryFrames = 0;
        CleanupBuffers();
        CleanupPrograms();
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

        if (DefaultTransform is null)
        {
            MarkGpuBuffersDirty();
            return;
        }

        if (IsActiveInHierarchy)
            ActivateGpuExecutionMode();

        MarkGpuBuffersDirty();

        if (IsActiveInHierarchy)
        {
            SetupParticles();
            ResetParticlesPosition();
        }
    }

    private void MarkGpuBuffersDirty()
    {
        CleanupBuffers();
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

        SubmitToBatchedDispatcher(loop, timeVar);
    }

    private void SubmitToBatchedDispatcher(int loopCount, float timeVar)
    {
        PrepareGPUData();
        RefreshGpuDrivenRendererBindingsIfNeeded();
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

    private void PrepareGPUData()
    {
        using var profilerState = Engine.Profiler.Start("PhysicsChainComponent.GPU.PrepareGPUData");

        var transformHash = new HashCode();
        var colliderHash = new HashCode();

        _transformMatrices.Clear();
        _collidersData.Clear();
        _totalParticleCount = 0;

        bool rebuildPreparedData = _preparedGpuDataVersion != _particlesVersion || _particleTreesData.Count != _particleTrees.Count;
        bool rebuildParticleState = rebuildPreparedData || _preparedParticleStateVersion != _particleStateVersion;
        if (rebuildPreparedData)
        {
            _particleStaticData.Clear();
            _particleTreesData.Clear();
        }

        if (rebuildParticleState)
            _particlesData.Clear();

        int particleCursor = 0;

        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            GPUParticleTreeData treeData = new()
            {
                RestGravity = tree.RestGravity,
                ParticleOffset = particleCursor,
                ParticleCount = tree.Particles.Count,
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
                    BoneLength = particle.SegmentLength,
                    TreeIndex = treeIndex,
                };

                if (rebuildParticleState)
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
        _preparedParticleStateVersion = _particleStateVersion;
        _preparedTransformSignature = transformHash.ToHashCode();

        if (_effectiveColliders is null)
        {
            _preparedColliderSignature = colliderHash.ToHashCode();
            return;
        }

        for (int colliderIndex = 0; colliderIndex < _effectiveColliders.Count; ++colliderIndex)
        {
            PhysicsChainColliderBase collider = _effectiveColliders[colliderIndex];
            if (collider is PhysicsChainSphereCollider sphereCollider)
            {
                TransformBase sphereTransform = sphereCollider.ColliderTransform ?? sphereCollider.Transform;
                GPUColliderData colliderData = new()
                {
                    Center = new Vector4(sphereTransform.WorldTranslation, sphereCollider.Radius),
                    Type = 0
                };
                _collidersData.Add(colliderData);
                AddColliderSignature(ref colliderHash, colliderData);
            }
            else if (collider is PhysicsChainCapsuleCollider capsuleCollider)
            {
                TransformBase capsuleTransform = capsuleCollider.ColliderTransform ?? capsuleCollider.Transform;
                Vector3 center = capsuleTransform.WorldTranslation;
                Vector3 halfAxis = capsuleTransform.WorldUp * (capsuleCollider.Height * 0.5f);
                Vector3 start = center - halfAxis;
                Vector3 end = center + halfAxis;
                float lengthSquared = Vector3.DistanceSquared(start, end);
                GPUColliderData colliderData = new()
                {
                    Center = new Vector4(start, capsuleCollider.Radius),
                    Params = new Vector4(end, lengthSquared > 1e-8f ? 1.0f / lengthSquared : 0.0f),
                    Type = 1
                };
                _collidersData.Add(colliderData);
                AddColliderSignature(ref colliderHash, colliderData);
            }
            else if (collider is PhysicsChainBoxCollider boxCollider)
            {
                TransformBase boxTransform = boxCollider.ColliderTransform ?? boxCollider.Transform;
                Quaternion rotation = boxTransform.WorldRotation;
                GPUColliderData colliderData = new()
                {
                    Center = new Vector4(boxTransform.WorldTranslation, 0.0f),
                    Params = new Vector4(Vector3.Abs(boxCollider.Size) * Vector3.Abs(boxTransform.LossyWorldScale) * 0.5f, 0.0f),
                    Orientation = new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W),
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
    }

    private static void AddColliderSignature(ref HashCode hash, GPUColliderData colliderData)
    {
        hash.Add(colliderData.Center);
        hash.Add(colliderData.Params);
        hash.Add(colliderData.Orientation);
        hash.Add(colliderData.Type);
    }

    /// <summary>
    /// Clears CPU preparation state. Standalone requests are isolated dispatcher
    /// groups, so the component owns no renderer-specific simulation buffers.
    /// </summary>
    private void CleanupBuffers()
    {
        _particlesData.Clear();
        _particleStaticData.Clear();
        _particleTreesData.Clear();
        _transformMatrices.Clear();
        _collidersData.Clear();
        _preparedGpuDataVersion = -1;
        _preparedParticleStateVersion = -1;
        _preparedTransformSignature = int.MinValue;
        _preparedColliderSignature = int.MinValue;
    }

    private void CleanupPrograms()
    {
        _gpuBonePaletteProgram?.Destroy();
        _gpuBonePaletteShader?.Destroy();
        _gpuBonePaletteProgram = null;
        _gpuBonePaletteShader = null;
    }

    private void MarkGpuDrivenRendererBindingsDirty(int retryFrames = GpuDrivenRendererBindingRetryFrameCount)
    {
        _gpuDrivenRendererBindingsDirty = true;
        _gpuDrivenRendererBindingRetryFrames = Math.Max(_gpuDrivenRendererBindingRetryFrames, retryFrames);
    }

    private void RefreshGpuDrivenRendererBindingsIfNeeded()
    {
        if (!_gpuDrivenRendererBindingsDirty)
            return;

        if (!UseGpuDrivenSkinning)
        {
            ClearGpuDrivenRendererBindings();
            _gpuDrivenRendererBindingsDirty = false;
            _gpuDrivenRendererBindingRetryFrames = 0;
            return;
        }

        if (!UseGPU || SceneNode is null || _particleTrees.Count == 0)
            return;

        RebuildGpuDrivenRendererBindings(out int skinnedRendererCount);

        if (_gpuDrivenRenderers.Count > 0 || _gpuDrivenRendererBindingRetryFrames <= 0)
        {
            _gpuDrivenRendererBindingsDirty = false;
            _gpuDrivenRendererBindingRetryFrames = 0;
            if (skinnedRendererCount > 0 && _gpuDrivenRenderers.Count == 0)
            {
                Debug.PhysicsWarning(
                    $"[PhysicsChain] RebuildGpuDrivenRendererBindings: Found {skinnedRendererCount} skinned renderers but created 0 GPU-driven bindings. Component={GetHashCode():X}");
            }
            return;
        }

        --_gpuDrivenRendererBindingRetryFrames;
    }

    private void RebuildGpuDrivenRendererBindings(out int skinnedRendererCount)
    {
        int previousBindingCount = _gpuDrivenRenderers.Count;
        ClearGpuDrivenRendererBindings();
        skinnedRendererCount = 0;

        if (!UseGPU || !UseGpuDrivenSkinning || SceneNode is null || _particleTrees.Count == 0)
        {
            if (VerboseGpuDrivenRendererLogging && previousBindingCount > 0)
                Debug.Out($"[PhysicsChain] RebuildGpuDrivenRendererBindings: Cleared {previousBindingCount} bindings, not rebuilding (UseGPU={UseGPU}, UseGpuDrivenSkinning={UseGpuDrivenSkinning}, SceneNode={SceneNode != null}, ParticleTrees={_particleTrees.Count}). Component={GetHashCode():X}");
            return;
        }

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
        int foundSkinnedRendererCount = 0;
        var searchRoot = SceneNode.Parent ?? SceneNode;
        searchRoot.IterateComponents<ModelComponent>(model =>
        {
            foreach (XRMeshRenderer renderer in model.GetAllRenderersWhere(static renderer => renderer.Mesh?.HasSkinning == true))
            {
                ++foundSkinnedRendererCount;
                TryAddGpuDrivenRendererState(renderer, _gpuDrivenParticleIndexByTransform, _gpuDrivenFirstChildIndexByParticle, _gpuDrivenRestDirectionByParticle);
            }
        }, true);
        skinnedRendererCount = foundSkinnedRendererCount;

        unchecked
        {
            ++_gpuDrivenRendererBindingGeneration;
        }

        if (VerboseGpuDrivenRendererLogging)
            Debug.Out($"[PhysicsChain] RebuildGpuDrivenRendererBindings: Scanned {skinnedRendererCount} skinned renderers, created {_gpuDrivenRenderers.Count} GPU-driven bindings. Component={GetHashCode():X}, TotalParticles={globalParticleIndex}, BoneTransformMappings={_gpuDrivenParticleIndexByTransform.Count}, PreviousBindings={previousBindingCount}");
    }

    /// <summary>
    /// Forces a re-scan of the node subtree for skinned renderers whose bones
    /// are driven by this physics chain. Call after dynamically adding a
    /// <see cref="ModelComponent"/> with a skinned mesh that references chain bones.
    /// </summary>
    public void InvalidateGpuDrivenRenderers()
    {
        MarkGpuDrivenRendererBindingsDirty();
        RefreshGpuDrivenRendererBindingsIfNeeded();
    }

    internal void AppendBatchedGpuDrivenBonePaletteBindings(
        int particleBaseOffset,
        List<GPUPhysicsChainDispatcher.GpuDrivenRendererPaletteBinding> bindings)
    {
        RefreshGpuDrivenRendererBindingsIfNeeded();

        for (int i = 0; i < _gpuDrivenRenderers.Count; ++i)
        {
            GpuDrivenRendererState state = _gpuDrivenRenderers[i];

            if (state.Renderer.SkinPaletteBuffer is null || state.Renderer.BoneInvBindMatricesBuffer is null)
                continue;

            bindings.Add(new GPUPhysicsChainDispatcher.GpuDrivenRendererPaletteBinding(
                this,
                state.Renderer,
                state.MappingData,
                particleBaseOffset,
                state.BoneMatrixElementCount,
                state.DrivesCompleteBonePalette,
                _gpuDrivenRendererBindingGeneration,
                _particleStateVersion));
        }
    }

    internal void ClearBatchedGpuDrivenBonePaletteSources()
    {
        for (int i = 0; i < _gpuDrivenRenderers.Count; ++i)
            _gpuDrivenRenderers[i].Renderer.ClearGpuDrivenSkinPaletteSource(this);
    }

    private void TryAddGpuDrivenRendererState(
        XRMeshRenderer renderer,
        Dictionary<Transform, int> particleIndexByTransform,
        Dictionary<int, int> firstChildIndexByParticle,
        Dictionary<int, Vector3> restDirectionByParticle)
    {
        if (!UseGpuDrivenSkinning)
            return;

        XRMesh? mesh = renderer.Mesh;
        if (mesh?.UtilizedBones is not { Length: > 0 })
        {
            if (mesh is null)
                Debug.PhysicsWarning($"[PhysicsChain] TryAddGpuDrivenRendererState: Renderer has no mesh. Component={GetHashCode():X}, Renderer={renderer.GetHashCode():X}");
            else if (mesh.UtilizedBones is null || mesh.UtilizedBones.Length == 0)
                Debug.PhysicsWarning($"[PhysicsChain] TryAddGpuDrivenRendererState: Mesh '{mesh.Name}' has no UtilizedBones. Component={GetHashCode():X}, Renderer={renderer.GetHashCode():X}, HasSkinning={mesh.HasSkinning}");
            return;
        }

        // Solution 2: Force renderer buffer initialization if needed
        if (renderer.SkinPaletteBuffer is null || renderer.BoneInvBindMatricesBuffer is null)
        {
            Debug.PhysicsWarning($"[PhysicsChain] TryAddGpuDrivenRendererState: skin palette buffer is null, attempting late initialization. Component={GetHashCode():X}, Renderer={renderer.GetHashCode():X}, Mesh='{mesh.Name}'");

            if (!renderer.EnsureSkinningBuffers())
            {
                Debug.PhysicsWarning($"[PhysicsChain] TryAddGpuDrivenRendererState: Failed to initialize skinning buffers - GPU-driven bone palette will NOT work for this renderer. Component={GetHashCode():X}, Renderer={renderer.GetHashCode():X}, Mesh='{mesh.Name}'");
                return;
            }

            Debug.PhysicsWarning($"[PhysicsChain] TryAddGpuDrivenRendererState: Late initialization succeeded. Component={GetHashCode():X}, Renderer={renderer.GetHashCode():X}, Mesh='{mesh.Name}'");
        }

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
        {
            if (VerboseGpuDrivenRendererLogging)
                Debug.Out($"[PhysicsChain] TryAddGpuDrivenRendererState: No bone mappings matched particle transforms. Component={GetHashCode():X}, Renderer={renderer.GetHashCode():X}, Mesh='{mesh.Name}', UtilizedBones={mesh.UtilizedBones.Length}, ParticleTransformCount={particleIndexByTransform.Count}");
            return;
        }

        XRDataBuffer<GPUDrivenBoneMappingData> mappingBuffer = new(
            $"PhysicsChainBonePaletteMap_{renderer.GetHashCode():X}",
            EBufferTarget.ShaderStorageBuffer,
            (uint)mappingData.Count)
        {
            Usage = EBufferUsage.StaticDraw,
            DisposeOnPush = false
        };
        mappingBuffer.SetData(CollectionsMarshal.AsSpan(mappingData));

        GPUDrivenBoneMappingData[] mappings = mappingData.ToArray();
        uint[] drivenIndices = drivenBoneIndices.ToArray();
        uint boneMatrixElementCount = (uint)mesh.UtilizedBones.Length + 1u;
        bool drivesCompleteBonePalette = mappingData.Count == mesh.UtilizedBones.Length;
        renderer.RegisterGpuDrivenBoneIndices(drivenIndices);
        _gpuDrivenRenderers.Add(new GpuDrivenRendererState(
            renderer,
            mappingBuffer,
            mappings,
            drivenIndices,
            boneMatrixElementCount,
            drivesCompleteBonePalette));

        if (VerboseGpuDrivenRendererLogging)
            Debug.Out($"[PhysicsChain] TryAddGpuDrivenRendererState: Successfully created GPU-driven binding. Component={GetHashCode():X}, Renderer={renderer.GetHashCode():X}, Mesh='{mesh.Name}', MappedBones={mappingData.Count}/{mesh.UtilizedBones.Length}");
    }

    private void ClearGpuDrivenRendererBindings()
    {
        bool hadBindings = _gpuDrivenRenderers.Count > 0;
        for (int i = 0; i < _gpuDrivenRenderers.Count; ++i)
        {
            GpuDrivenRendererState state = _gpuDrivenRenderers[i];
            state.Renderer.ClearGpuDrivenSkinPaletteSource(this);
            state.Renderer.UnregisterGpuDrivenBoneIndices(state.DrivenBoneIndices);
            state.MappingBuffer.Destroy();
        }


        _gpuDrivenRenderers.Clear();
        if (hadBindings)
        {
            unchecked
            {
                ++_gpuDrivenRendererBindingGeneration;
            }
        }
    }

    private void EnsureGpuBonePaletteProgram()
    {
        if (_gpuBonePaletteProgram is not null)
            return;

        _gpuBonePaletteShader = ShaderHelper.LoadEngineShader("Compute/PhysicsChain/PhysicsChainBonePalette.comp", EShaderType.Compute);
        _gpuBonePaletteProgram = new XRRenderProgram(true, false, _gpuBonePaletteShader);
    }

    internal bool PublishGpuDrivenBoneMatrices(
        XRDataBuffer? particlesBuffer,
        XRDataBuffer? transformMatricesBuffer,
        int particleBaseOffset,
        bool includeCompletePalettes = true,
        IPhysicsChainComputeBackend? backend = null)
    {
        using var profilerState = Engine.Profiler.Start("PhysicsChainComponent.GPU.PublishGpuDrivenBoneMatrices");

        if (particlesBuffer is null)
        {
            if (_gpuDrivenRenderers.Count > 0)
                Debug.PhysicsWarning($"[PhysicsChain] PublishGpuDrivenBoneMatrices: particlesBuffer is NULL but have {_gpuDrivenRenderers.Count} GPU-driven renderers. Component={GetHashCode():X}");
            return false;
        }

        if (transformMatricesBuffer is null)
        {
            if (_gpuDrivenRenderers.Count > 0)
                Debug.PhysicsWarning($"[PhysicsChain] PublishGpuDrivenBoneMatrices: transformMatricesBuffer is NULL but have {_gpuDrivenRenderers.Count} GPU-driven renderers. Component={GetHashCode():X}");
            return false;
        }

        if (_gpuDrivenRenderers.Count == 0)
            return true;

        if (!includeCompletePalettes && !HasPartialGpuDrivenRendererPalette())
            return true;

        if (backend is null
            && !PhysicsChainComputeBackendFactory.TryCreate(AbstractRenderer.Current, out backend))
            return false;

        EnsureGpuBonePaletteProgram();
        if (_gpuBonePaletteProgram is null)
        {
            Debug.PhysicsWarning($"[PhysicsChain] PublishGpuDrivenBoneMatrices: Failed to create bone palette program. Component={GetHashCode():X}");
            return false;
        }

        int dispatchedCount = 0;
        int skippedCount = 0;

        for (int i = 0; i < _gpuDrivenRenderers.Count; ++i)
        {
            GpuDrivenRendererState state = _gpuDrivenRenderers[i];
            if (state.DrivesCompleteBonePalette && !includeCompletePalettes)
                continue;

            if (state.DrivesCompleteBonePalette)
                state.Renderer.ClearGpuDrivenSkinPaletteSource(this);

            XRDataBuffer? outputSkinPalette = state.Renderer.SkinPaletteBuffer;
            XRDataBuffer? boneInvBindMatrices = state.Renderer.BoneInvBindMatricesBuffer;

            if (outputSkinPalette is null || boneInvBindMatrices is null)
            {
                Debug.PhysicsWarning($"[PhysicsChain] PublishGpuDrivenBoneMatrices: Renderer skin palette or inverse bind buffer is NULL (index {i}). Component={GetHashCode():X}, RendererHash={state.Renderer.GetHashCode():X}, MappingCount={state.MappingCount}");
                ++skippedCount;
                continue;
            }

            if (state.MappingCount <= 0)
            {
                Debug.PhysicsWarning($"[PhysicsChain] PublishGpuDrivenBoneMatrices: MappingCount is 0 (index {i}). Component={GetHashCode():X}");
                ++skippedCount;
                continue;
            }

            if (VerboseGpuDrivenRendererLogging)
                Debug.Out($"[PhysicsChain] PublishGpuDrivenBoneMatrices: Dispatching skin palette. Component={GetHashCode():X}, RendererIndex={i}, RendererHash={state.Renderer.GetHashCode():X}, ParticleBaseOffset={particleBaseOffset}, MappingCount={state.MappingCount}, OutputBufferHash={outputSkinPalette.GetHashCode():X}");

            _gpuBonePaletteProgram.Uniform("particleBaseOffset", particleBaseOffset);
            _gpuBonePaletteProgram.Uniform("mappingCount", state.MappingCount);
            _gpuBonePaletteProgram.BindBuffer(particlesBuffer, 0);
            _gpuBonePaletteProgram.BindBuffer(transformMatricesBuffer, 1);
            _gpuBonePaletteProgram.BindBuffer(state.MappingBuffer, 2);
            _gpuBonePaletteProgram.BindBuffer(outputSkinPalette, 3);
            _gpuBonePaletteProgram.BindBuffer(boneInvBindMatrices, 4);

            uint groupsX = (uint)(state.MappingCount + 63) / 64u;
            PhysicsChainComputeEnqueueStatus enqueueStatus = backend!.TryDispatchDirect(
                _gpuBonePaletteProgram,
                Math.Max(groupsX, 1u),
                1u,
                1u,
                PhysicsChainComputePassKind.BonePalettePublication);
            if (enqueueStatus != PhysicsChainComputeEnqueueStatus.Enqueued)
            {
                Debug.PhysicsWarningEvery(
                    $"PhysicsChain.BonePaletteDispatch.{GetHashCode():X}.{enqueueStatus}",
                    TimeSpan.FromSeconds(1),
                    "[PhysicsChain] Backend '{0}' rejected bone-palette dispatch ({1}); GPU work remains pending.",
                    backend.Name,
                    enqueueStatus);
                return false;
            }
            PhysicsChainComputeEnqueueStatus completionStatus = backend.TryCompletePass(new PhysicsChainComputePass(
                PhysicsChainComputePassKind.BonePalettePublication,
                EMemoryBarrierMask.ShaderStorage));
            if (completionStatus != PhysicsChainComputeEnqueueStatus.Enqueued)
            {
                Debug.PhysicsWarningEvery(
                    $"PhysicsChain.BonePaletteCompletion.{GetHashCode():X}.{completionStatus}",
                    TimeSpan.FromSeconds(1),
                    "[PhysicsChain] Backend '{0}' rejected the bone-palette completion barrier ({1}); GPU work remains pending.",
                    backend.Name,
                    completionStatus);
                return false;
            }
            ++dispatchedCount;
        }

        if (skippedCount > 0)
            Debug.PhysicsWarning($"[PhysicsChain] PublishGpuDrivenBoneMatrices: Dispatched {dispatchedCount} renderers, SKIPPED {skippedCount} renderers. Component={GetHashCode():X}, ParticleBaseOffset={particleBaseOffset}");
        return skippedCount == 0;
    }

    private bool HasPartialGpuDrivenRendererPalette()
    {
        for (int i = 0; i < _gpuDrivenRenderers.Count; ++i)
            if (!_gpuDrivenRenderers[i].DrivesCompleteBonePalette)
                return true;

        return false;
    }

    private void RenderGpuDebug()
        => GPUPhysicsChainDispatcher.Instance.RenderSelectedGpuDebug();

    internal float GetGpuDebugInterpolationAlpha()
        => ComputeRenderAlpha();

}
