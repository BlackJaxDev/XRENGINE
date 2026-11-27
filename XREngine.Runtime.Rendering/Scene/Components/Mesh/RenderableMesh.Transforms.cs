using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Mesh
{
    public partial class RenderableMesh
    {
        #region Render-matrix update queue

        private static readonly ConcurrentQueue<RenderableMesh> _pendingRenderMatrixUpdates = new();
        private readonly object _pendingRenderMatrixLock = new();
        private Matrix4x4 _pendingComponentRenderMatrix = Matrix4x4.Identity;
        private int _pendingComponentRenderMatrixVersion;
        private Matrix4x4 _pendingRootBoneRenderMatrix = Matrix4x4.Identity;
        private int _pendingRootBoneRenderMatrixVersion;
        private int _pendingRenderMatrixQueued;

        #endregion

        #region Component transform subscription

        private void ComponentPropertyChanged(object? s, IXRPropertyChangedEventArgs e)
        {
            if (!BeginLifetimeMutation())
                return;
            try
            {
                if (e.PropertyName == nameof(RenderableComponent.Transform) && !Component.SceneNode.IsTransformNull)
                {
                    Component.Transform.WorldMatrixChanged += Component_WorldMatrixPreviewChanged;
                    Component.Transform.RenderMatrixChanged += Component_WorldMatrixChanged;
                }
            }
            finally { EndLifetimeMutation(); }
        }
        private void ComponentPropertyChanging(object? s, IXRPropertyChangingEventArgs e)
        {
            if (_retiring)
                return;
            if (e.PropertyName == nameof(RenderableComponent.Transform) && !Component.SceneNode.IsTransformNull)
            {
                Component.Transform.WorldMatrixChanged -= Component_WorldMatrixPreviewChanged;
                Component.Transform.RenderMatrixChanged -= Component_WorldMatrixChanged;
            }
        }

        #endregion

        #region Serialized transform rebinding

        private TransformBase? GetTransformReferenceSearchRoot()
        {
            SceneNode? node = Component.SceneNode;
            if (node is null)
                return null;

            Guid prefabAssetId = node.Prefab?.PrefabAssetId ?? Guid.Empty;
            while (node.Parent is SceneNode parent)
            {
                Guid parentPrefabId = parent.Prefab?.PrefabAssetId ?? Guid.Empty;
                if (prefabAssetId != Guid.Empty && parentPrefabId != prefabAssetId)
                    break;
                if (prefabAssetId == Guid.Empty && parentPrefabId != Guid.Empty)
                    prefabAssetId = parentPrefabId;

                node = parent;
            }

            return node.Transform;
        }

        private static TransformBase? ResolveTransformReference(TransformBase? source, TransformBase? searchRoot)
        {
            if (source is null || searchRoot is null)
                return source;

            if (IsSelfOrDescendantOf(searchRoot, source))
                return source;

            Guid referenceId = source.EffectiveSerializedReferenceId;
            if (referenceId == Guid.Empty)
                return source;

            return searchRoot.FindSelfOrDescendantBySerializedReferenceId(referenceId) ?? source;
        }

        /// <summary>Checks completed world ownership for source carriers that can now resolve to scene transforms.</summary>
        internal bool RequiresSceneTransformRebind()
        {
            if (_retiring)
                return false;
            TransformBase? searchRoot = GetTransformReferenceSearchRoot();
            if (searchRoot is null)
                return false;

            bool hasSkinning = false;
            lock (_lodsLock)
            {
                for (LinkedListNode<RenderableLOD>? node = LODs.First; node is not null; node = node.Next)
                {
                    if (node.Value.Renderer.Mesh is not { HasSkinning: true } mesh)
                        continue;
                    hasSkinning = true;
                    if (mesh.NeedsSerializedTransformRebind(searchRoot))
                        return true;
                }
            }

            return hasSkinning &&
                (!ReferenceEquals(ResolveTransformReference(RootBone, searchRoot), RootBone) ||
                 !ReferenceEquals(ResolveTransformReference(_skinnedBoundsRootTransform, searchRoot), _skinnedBoundsRootTransform));
        }

        private XRMesh? CreateRuntimeMesh(XRMesh? sourceMesh, TransformBase? searchRoot)
        {
            if (sourceMesh is null || searchRoot is null || !sourceMesh.NeedsSerializedTransformRebind(searchRoot))
                return sourceMesh;

            XRMesh reboundMesh = sourceMesh.CloneForRuntimeTransformRebind();
            // Rebinding writes callback-capable live bone bind matrices. Adopt the
            // clone before those callbacks so constructor failure can retire it.
            _ownedRuntimeMeshes.Add(reboundMesh);
            try
            {
                if (!reboundMesh.RebindSerializedTransformReferences(searchRoot, remapVertexWeights: false))
                {
                    ReleaseOwnedRuntimeMesh(reboundMesh);
                    return sourceMesh;
                }

                // The rebind changes only bone references. The clone uses the source geometry objects,
                // so it gets its own copy of the source meshlets and keeps the meshlet and cluster
                // level-of-detail routes (for example, cloned crowd avatars).
                reboundMesh.TryAttachSharedGeometryMeshletPayload(sourceMesh);
                return reboundMesh;
            }
            catch (Exception bindingFailure)
            {
                try { ReleaseOwnedRuntimeMesh(reboundMesh); }
                catch (Exception cleanupFailure) { bindingFailure.Data["RuntimeMeshRebindCleanupFailure"] = cleanupFailure; }
                throw;
            }
        }

        /// <summary>
        /// Copies the live skeleton bone bindings, root bone, and material override from
        /// <paramref name="source"/> onto this renderable mesh so runtime proxy clones deform
        /// with the exact same active skeleton transforms and clean up owned runtime meshes on disposal.
        /// </summary>
        public void SyncSkinningBindingsFrom(RenderableMesh source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (_retiring || source._retiring)
                return;

            if (source.RootBone is not null && !ReferenceEquals(RootBone, source.RootBone))
                RootBone = source.RootBone;
            if (!ReferenceEquals(MaterialOverride, source.MaterialOverride))
                MaterialOverride = source.MaterialOverride;

            RenderableLOD[] sourceLods = source.GetLodSnapshot();
            RenderableLOD[] targetLods = GetLodSnapshot();
            int count = Math.Min(sourceLods.Length, targetLods.Length);
            bool reboundAnyLod = false;
            for (int index = 0; index < count; ++index)
            {
                XRMesh? sourceMesh = sourceLods[index].Renderer.Mesh;
                XRMesh? targetMesh = targetLods[index].Renderer.Mesh;
                if (sourceMesh is not { HasSkinning: true } || targetMesh is not { HasSkinning: true })
                    continue;

                var sourceBones = sourceMesh.UtilizedBones;
                var targetBones = targetMesh.UtilizedBones;
                if (sourceBones.Length != targetBones.Length || HasSameBoneTransforms(sourceBones, targetBones))
                    continue;

                XRMesh rebound = targetMesh.CloneForRuntimeTransformRebind();
                _ownedRuntimeMeshes.Add(rebound);
                try
                {
                    rebound.ReplaceSkinningBoneBindings(sourceBones);
                    rebound.TryAttachSharedGeometryMeshletPayload(sourceMesh);
                    XRMeshRenderer renderer = targetLods[index].Renderer;
                    renderer.Mesh = rebound;
                    if (!ReferenceEquals(renderer.Mesh, rebound))
                    {
                        ReleaseOwnedRuntimeMesh(rebound);
                        continue;
                    }
                    if (_boneTrackingByRenderer.TryGetValue(renderer, out bool wasTracked) && wasTracked)
                        TrackBones(targetMesh, false);
                    AdvanceLodRegistrationVersion();
                    TrackBones(rebound, true);
                    _boneTrackingByRenderer[renderer] = true;
                    ReleaseOwnedRuntimeMesh(targetMesh);
                    reboundAnyLod = true;
                }
                catch
                {
                    ReleaseOwnedRuntimeMesh(rebound);
                    throw;
                }
            }

            if (reboundAnyLod)
            {
                // Rebuild cached per-bone culling boxes, GPU-scene registration, and world bounds
                // from the rebound skeleton so first-person proxies stay synchronized and visible.
                Volatile.Write(ref _committedBoundsStateDirty, 1);
                MarkSkinnedDataDirty();
                MarkSkinnedBoneCullingVolumesDirty();
                RefreshSkinnedCullingIntersectionOverride();
                if (!_retiring)
                    _rc?.MarkDirty();
                _ = RefreshSkinnedCullingBoundsForSceneCulling();
                QueueCurrentRenderMatrixUpdate();
            }
        }

        private static bool HasSameBoneTransforms(
            (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] first,
            (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] second)
        {
            if (first.Length != second.Length)
                return false;

            for (int i = 0; i < first.Length; ++i)
            {
                if (!ReferenceEquals(first[i].tfm, second[i].tfm))
                    return false;
            }

            return true;
        }

        private static bool IsSelfOrDescendantOf(TransformBase root, TransformBase candidate)
        {
            for (TransformBase? current = candidate; current is not null; current = current.Parent)
            {
                if (ReferenceEquals(current, root))
                    return true;
            }

            return false;
        }

        private void ReleaseOwnedRuntimeMesh(XRMesh? mesh)
        {
            if (mesh is null || !_ownedRuntimeMeshes.Contains(mesh))
                return;

            mesh.Destroy(now: true);
            if (!mesh.IsDestroyed)
                throw new InvalidOperationException("RenderableMesh.RuntimeMeshRetirementVetoed: runtime clone ownership is retained for retry.");
            _ownedRuntimeMeshes.Remove(mesh);
        }

        #endregion

        #region Runtime render settings

        private void Rendering_SettingsChanged()
        {
            if (!BeginLifetimeMutation())
                return;
            try
            {
                bool isSkinned = IsSkinned;
                if (!RenderDeformationSettingsChanged(isSkinned))
                    return;

                CaptureRenderDeformationSettings(isSkinned);
                InvalidateGpuDeformationState();
                if (_retiring)
                    return;
                MarkSkinnedDataDirty();

                if (isSkinned)
                {
                    XRMeshRenderer? renderer = CurrentLODRenderer;
                    if (renderer?.EnsureSkinningBuffers(logWarnings: false) == true)
                        renderer.RefreshBoneMatricesFromRenderState();
                    if (_retiring)
                        return;

                    if (RootBone is not null)
                        MarkPendingRootBoneRenderMatrix(GetCurrentTransformMatrix(RootBone));
                    else
                        SetSkinnedRootRenderMatrix(GetCurrentTransformMatrix(Component.Transform));
                }

                MarkPendingComponentRenderMatrix(GetCurrentTransformMatrix(Component.Transform));
            }
            finally { EndLifetimeMutation(); }
        }

        #endregion

        #region Render-matrix propagation

        /// <summary>
        /// Updates the culling offset matrix for skinned meshes when the root bone moves.
        /// </summary>
        private void RootBone_WorldMatrixChanged(TransformBase rootBone, Matrix4x4 renderMatrix)
        {
            if (_retiring)
                return;
            MarkSourceFlatCommandsDirty();
            if (UsesCommittedWorldBounds)
                return;

            long t = RenderableMeshStageTelemetry.Begin();
            if (RuntimeEngine.IsRenderThread)
            {
                ApplyImmediateRenderMatrixUpdate(componentMatrix: null, rootMatrix: renderMatrix);
                RenderableMeshStageTelemetry.End(RenderableMeshStage.RootBoneRenderMatrixChanged, t);
                return;
            }

            MarkPendingRootBoneRenderMatrix(renderMatrix);
            RenderableMeshStageTelemetry.End(RenderableMeshStage.RootBoneRenderMatrixChanged, t);
        }

        /// <summary>
        /// Seeds the skinned basis and culling bounds when a root bone is assigned. Later
        /// root motion arrives through <see cref="RootBone_WorldMatrixChanged"/> after the
        /// render-matrix publication that the bounds read.
        /// </summary>
        private void InitializeRootBoneCullingBasis(TransformBase rootBone, Matrix4x4 worldMatrix)
        {
            if (!BeginLifetimeMutation())
                return;
            try
            {
                if (_retiring)
                    return;
                bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
                if (!hasSkinning)
                    return;

                Matrix4x4 basis = GetSkinnedBasisMatrix();
                // Keep the basis for the GPU skinned BVH only. Skinned culling bounds use the single
                // world-space-LocalCullingVolume + identity-offset convention; republish via the aggregate
                // path instead of stamping the basis as an offset (which would pair the existing world-space
                // volume with the root matrix and double-transform the box -> culling "tower" flicker).
                SetSkinnedRootRenderMatrix(basis);
                if (!UsesCommittedWorldBounds)
                    TryApplySkinnedBoneCullingBounds();
            }
            finally { EndLifetimeMutation(); }
        }

        /// <summary>
        /// Updates the culling offset matrix for non-skinned meshes when the component moves.
        /// </summary>
        private void Component_WorldMatrixChanged(TransformBase component, Matrix4x4 renderMatrix)
        {
            if (_retiring)
                return;
            MarkSourceFlatCommandsDirty();
            if (UsesCommittedWorldBounds)
                return;

            if (RuntimeEngine.IsRenderThread)
            {
                ApplyImmediateRenderMatrixUpdate(componentMatrix: renderMatrix, rootMatrix: null);
                return;
            }

            MarkPendingComponentRenderMatrix(renderMatrix);
        }

        private void Component_WorldMatrixPreviewChanged(TransformBase component, Matrix4x4 worldMatrix)
        {
            if (UsesCommittedWorldBounds)
                return;

            if (!BeginLifetimeMutation())
                return;
            try
            {
                if (_retiring)
                    return;
                bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
                if (hasSkinning)
                {
                    Matrix4x4 basis = GetSkinnedBasisMatrix();
                    // World-space bounds + identity offset convention; republish via aggregate path
                    // rather than stamping the basis as a culling offset (torn-read tower bug).
                    SetSkinnedRootRenderMatrix(basis);
                    TryApplySkinnedBoneCullingBounds();
                    return;
                }

                RenderInfo?.CullingOffsetMatrix = worldMatrix;
            }
            finally { EndLifetimeMutation(); }
        }

        private void ApplyImmediateRenderMatrixUpdate(Matrix4x4? componentMatrix, Matrix4x4? rootMatrix)
        {
            if (UsesCommittedWorldBounds)
                return;

            if (!BeginLifetimeMutation())
                return;
            try
            {
                if (_retiring)
                    return;
                bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
                if (hasSkinning)
                {
                    Matrix4x4 basis = GetSkinnedBasisMatrix();
                    _rc.WorldMatrix = Matrix4x4.Identity;
                    // Basis is for the GPU skinned BVH; TryApplySkinnedBoneCullingBounds publishes the
                    // world-space aggregate with an identity offset (the single skinned convention).
                    SetSkinnedRootRenderMatrix(basis);
                    AABB? priorBounds = RenderInfo?.LocalCullingVolume;
                    _ = TryApplySkinnedBoneCullingBounds();
                    if (!Nullable.Equals(RenderInfo?.LocalCullingVolume, priorBounds))
                        _rc.SwapBuffers();

                    return;
                }

                Matrix4x4 matrix = componentMatrix ?? GetCurrentTransformMatrix(Component.Transform);
                _rc?.WorldMatrix = matrix;

                RenderInfo?.CullingOffsetMatrix = matrix;
            }
            finally { EndLifetimeMutation(); }
        }

        internal void QueueCurrentRenderMatrixUpdate()
        {
            if (_retiring)
                return;
            if (UsesCommittedWorldBounds)
                return;

            if (RuntimeEngine.IsRenderThread)
            {
                ApplyImmediateRenderMatrixUpdate(
                    GetCurrentTransformMatrix(Component.Transform),
                    RootBone is null ? null : GetCurrentTransformMatrix(RootBone));
                return;
            }

            MarkPendingComponentRenderMatrix(GetCurrentTransformMatrix(Component.Transform));

            if (RootBone is not null)
                MarkPendingRootBoneRenderMatrix(GetCurrentTransformMatrix(RootBone));
        }

        private void QueuePendingRenderMatrixUpdate()
        {
            if (_retiring)
                return;
            if (Interlocked.Exchange(ref _pendingRenderMatrixQueued, 1) == 0)
                _pendingRenderMatrixUpdates.Enqueue(this);
        }

        private void MarkPendingComponentRenderMatrix(Matrix4x4 renderMatrix)
        {
            if (_retiring)
                return;
            lock (_pendingRenderMatrixLock)
            {
                _pendingComponentRenderMatrix = renderMatrix;
                _pendingComponentRenderMatrixVersion++;
            }

            QueuePendingRenderMatrixUpdate();
        }

        private void MarkPendingRootBoneRenderMatrix(Matrix4x4 renderMatrix)
        {
            if (_retiring)
                return;
            lock (_pendingRenderMatrixLock)
            {
                _pendingRootBoneRenderMatrix = renderMatrix;
                _pendingRootBoneRenderMatrixVersion++;
            }

            QueuePendingRenderMatrixUpdate();
        }

        private void MarkSourceFlatCommandsDirty()
        {
            // Component and root changes can arrive before command construction.
            if (_rc is null)
                return;

            XRMeshRenderer? renderer = CurrentLODRenderer;
            XRMaterial? materialOverride = _rc.MaterialOverride;
            if (SourceMultilayerFlatDirection.IsRequired(materialOverride))
                _rc.MarkDirty();
            else if (materialOverride is null && renderer is not null)
            {
                int count = Math.Max(1, renderer.Submeshes.Count);
                for (int index = 0; index < count; ++index)
                {
                    renderer.TryGetMesh(index, out _, out XRMaterial? material);
                    if (!SourceMultilayerFlatDirection.IsRequired(material))
                        continue;
                    _rc.MarkDirty();
                    break;
                }
            }

            if (_materialOutlineCommand.Enabled &&
                SourceMultilayerFlatDirection.IsRequired(_materialOutlineCommand.MaterialOverride))
                _materialOutlineCommand.MarkDirty();
        }

        private void ApplyPendingRenderMatrixUpdates()
        {
            if (UsesCommittedWorldBounds)
            {
                Interlocked.Exchange(ref _pendingRenderMatrixQueued, 0);
                return;
            }

            if (!BeginLifetimeMutation())
            {
                Interlocked.Exchange(ref _pendingRenderMatrixQueued, 0);
                return;
            }
            long tTotal = RenderableMeshStageTelemetry.Begin();
            long tHead = tTotal;
            try
            {
                int componentVersion;
                int rootBoneVersion;
                Matrix4x4 componentMatrix;

                lock (_pendingRenderMatrixLock)
                {
                    componentVersion = _pendingComponentRenderMatrixVersion;
                    rootBoneVersion = _pendingRootBoneRenderMatrixVersion;
                    componentMatrix = _pendingComponentRenderMatrix;
                }

                bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
                if (hasSkinning)
                {
                    Matrix4x4 basis = GetSkinnedBasisMatrix();
                    _rc?.WorldMatrix = Matrix4x4.Identity;
                    // Keep the basis available for the GPU skinned BVH, but DO NOT publish it as the
                    // culling offset. Skinned culling bounds use a single convention: a world-space
                    // LocalCullingVolume paired with an IDENTITY CullingOffsetMatrix (see
                    // PublishSkinnedWorldCullingBounds). Writing the basis here as an offset-only update
                    // (without updating LocalCullingVolume) pairs the prior world-space aggregate volume
                    // with the root render matrix, double-transforming the box into the culling "tower"
                    // that flickers the mesh out. The bounds publish below owns the offset.
                    SetSkinnedRootRenderMatrix(basis);
                }
                else
                {
                    _rc?.WorldMatrix = componentMatrix;

                    RenderInfo?.CullingOffsetMatrix = componentMatrix;
                }

                Interlocked.Exchange(ref _pendingRenderMatrixQueued, 0);
                if (_retiring)
                    return;

                lock (_pendingRenderMatrixLock)
                {
                    if (_pendingComponentRenderMatrixVersion != componentVersion ||
                        _pendingRootBoneRenderMatrixVersion != rootBoneVersion)
                    {
                        QueuePendingRenderMatrixUpdate();
                    }
                }

                RenderableMeshStageTelemetry.End(RenderableMeshStage.ApplyPendingMatrixState, tHead);
                long tBounds = RenderableMeshStageTelemetry.Begin();
                ProcessSkinnedBoundsRefresh();
                RenderableMeshStageTelemetry.End(RenderableMeshStage.ProcessPendingSkinnedBounds, tBounds);
                if (_retiring)
                    return;
                long tApply = RenderableMeshStageTelemetry.Begin();
                if (hasSkinning)
                    _ = TryApplySkinnedBoneCullingBounds();
                RenderableMeshStageTelemetry.End(RenderableMeshStage.ApplyPendingBoneBounds, tApply);
                if (_retiring)
                    return;

                // Visible collection has already finished. Publish the final matrix and bounds
                // together so the command does not lag a frame or publish an intermediate state.
                long tSwap = RenderableMeshStageTelemetry.Begin();
                _rc?.SwapBuffers();
                RenderableMeshStageTelemetry.End(RenderableMeshStage.SwapPendingRenderCommand, tSwap);
            }
            finally
            {
                RenderableMeshStageTelemetry.End(RenderableMeshStage.ApplyPendingRenderMatrixUpdates, tTotal);
                EndLifetimeMutation();
            }
        }

        internal static void ProcessPendingRenderMatrixUpdates()
        {
            // Bound draining to a snapshot of the current queue length. ApplyPendingRenderMatrixUpdates
            // -> ProcessSkinnedBoundsRefresh may re-enqueue the same mesh while an async bounds refresh
            // is in flight (or bounds are still being built for the first time); draining until empty
            // would spin indefinitely. Re-enqueued meshes are picked up on the next swap.
            int remaining = _pendingRenderMatrixUpdates.Count;
            while (remaining-- > 0 && _pendingRenderMatrixUpdates.TryDequeue(out var mesh))
                mesh.ApplyPendingRenderMatrixUpdates();
        }

        #endregion
    }
}
