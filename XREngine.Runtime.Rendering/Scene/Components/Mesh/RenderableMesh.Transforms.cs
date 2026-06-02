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
            if (e.PropertyName == nameof(RenderableComponent.Transform) && !Component.SceneNode.IsTransformNull)
            {
                Component.Transform.WorldMatrixChanged += Component_WorldMatrixPreviewChanged;
                Component.Transform.RenderMatrixChanged += Component_WorldMatrixChanged;
            }
        }
        private void ComponentPropertyChanging(object? s, IXRPropertyChangingEventArgs e)
        {
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
                if (prefabAssetId != Guid.Empty && (parent.Prefab?.PrefabAssetId ?? Guid.Empty) != prefabAssetId)
                    break;

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

        private XRMesh? CreateRuntimeMesh(XRMesh? sourceMesh, TransformBase? searchRoot)
        {
            if (sourceMesh is null || searchRoot is null || !sourceMesh.NeedsSerializedTransformRebind(searchRoot))
                return sourceMesh;

            XRMesh reboundMesh = sourceMesh.CloneForRuntimeTransformRebind();
            if (!reboundMesh.RebindSerializedTransformReferences(searchRoot, remapVertexWeights: false))
            {
                reboundMesh.Destroy(now: true);
                return sourceMesh;
            }

            _ownedRuntimeMeshes.Add(reboundMesh);
            return reboundMesh;
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
            if (mesh is null || !_ownedRuntimeMeshes.Remove(mesh))
                return;

            mesh.Destroy(now: true);
        }

        #endregion

        #region Runtime render settings

        private void Rendering_SettingsChanged()
        {
            bool isSkinned = IsSkinned;
            if (!RenderDeformationSettingsChanged(isSkinned))
                return;

            CaptureRenderDeformationSettings(isSkinned);
            InvalidateGpuDeformationState();
            MarkSkinnedDataDirty();

            if (isSkinned)
            {
                XRMeshRenderer? renderer = CurrentLODRenderer;
                if (renderer?.EnsureSkinningBuffers(logWarnings: false) == true)
                    renderer.RefreshBoneMatricesFromRenderState();

                if (RootBone is not null)
                    MarkPendingRootBoneRenderMatrix(GetCurrentTransformMatrix(RootBone));
                else
                    SetSkinnedRootRenderMatrix(GetCurrentTransformMatrix(Component.Transform));
            }

            MarkPendingComponentRenderMatrix(GetCurrentTransformMatrix(Component.Transform));
        }

        #endregion

        #region Render-matrix propagation

        /// <summary>
        /// Updates the culling offset matrix for skinned meshes when the root bone moves.
        /// </summary>
        private void RootBone_WorldMatrixChanged(TransformBase rootBone, Matrix4x4 renderMatrix)
        {
            if (RuntimeEngine.IsRenderThread)
            {
                ApplyImmediateRenderMatrixUpdate(componentMatrix: null, rootMatrix: renderMatrix);
                return;
            }

            MarkPendingRootBoneRenderMatrix(renderMatrix);
        }

        private void RootBone_WorldMatrixPreviewChanged(TransformBase rootBone, Matrix4x4 worldMatrix)
        {
            bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
            if (!hasSkinning)
                return;

            Matrix4x4 basis = GetSkinnedBasisMatrix();
            SetSkinnedRootRenderMatrix(basis);
            RenderInfo?.CullingOffsetMatrix = basis;
        }

        /// <summary>
        /// Updates the culling offset matrix for non-skinned meshes when the component moves.
        /// </summary>
        private void Component_WorldMatrixChanged(TransformBase component, Matrix4x4 renderMatrix)
        {
            if (RuntimeEngine.IsRenderThread)
            {
                ApplyImmediateRenderMatrixUpdate(componentMatrix: renderMatrix, rootMatrix: null);
                return;
            }

            MarkPendingComponentRenderMatrix(renderMatrix);
        }

        private void Component_WorldMatrixPreviewChanged(TransformBase component, Matrix4x4 worldMatrix)
        {
            bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
            if (hasSkinning)
            {
                Matrix4x4 basis = GetSkinnedBasisMatrix();
                SetSkinnedRootRenderMatrix(basis);
                RenderInfo?.CullingOffsetMatrix = basis;
                return;
            }

            RenderInfo?.CullingOffsetMatrix = worldMatrix;
        }

        private void ApplyImmediateRenderMatrixUpdate(Matrix4x4? componentMatrix, Matrix4x4? rootMatrix)
        {
            bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
            if (hasSkinning)
            {
                Matrix4x4 basis = GetSkinnedBasisMatrix();
                _rc.WorldMatrix = Matrix4x4.Identity;
                SetSkinnedRootRenderMatrix(basis);
                RenderInfo?.CullingOffsetMatrix = basis;

                return;
            }

            Matrix4x4 matrix = componentMatrix ?? GetCurrentTransformMatrix(Component.Transform);
            _rc?.WorldMatrix = matrix;

            RenderInfo?.CullingOffsetMatrix = matrix;
        }

        internal void QueueCurrentRenderMatrixUpdate()
        {
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
            if (Interlocked.Exchange(ref _pendingRenderMatrixQueued, 1) == 0)
                _pendingRenderMatrixUpdates.Enqueue(this);
        }

        private void MarkPendingComponentRenderMatrix(Matrix4x4 renderMatrix)
        {
            lock (_pendingRenderMatrixLock)
            {
                _pendingComponentRenderMatrix = renderMatrix;
                _pendingComponentRenderMatrixVersion++;
            }

            QueuePendingRenderMatrixUpdate();
        }

        private void MarkPendingRootBoneRenderMatrix(Matrix4x4 renderMatrix)
        {
            lock (_pendingRenderMatrixLock)
            {
                _pendingRootBoneRenderMatrix = renderMatrix;
                _pendingRootBoneRenderMatrixVersion++;
            }

            QueuePendingRenderMatrixUpdate();
        }

        private void ApplyPendingRenderMatrixUpdates()
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
                SetSkinnedRootRenderMatrix(basis);
                RenderInfo?.CullingOffsetMatrix = basis;
            }
            else
            {
                _rc?.WorldMatrix = componentMatrix;

                RenderInfo?.CullingOffsetMatrix = componentMatrix;
            }

            Interlocked.Exchange(ref _pendingRenderMatrixQueued, 0);

            lock (_pendingRenderMatrixLock)
            {
                if (_pendingComponentRenderMatrixVersion != componentVersion ||
                    _pendingRootBoneRenderMatrixVersion != rootBoneVersion)
                {
                    QueuePendingRenderMatrixUpdate();
                }
            }

            // Matrix changes are applied in the world's SwapBuffers phase after visible
            // collection has already run. Publish the command snapshot here too; otherwise
            // dirty-delta command swapping can leave the rendered matrix one frame behind.
            _rc?.SwapBuffers();

            ProcessSkinnedBoundsRefresh();
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
