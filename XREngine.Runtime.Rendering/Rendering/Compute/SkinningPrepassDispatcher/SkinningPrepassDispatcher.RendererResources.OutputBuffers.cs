using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    private sealed partial class RendererResources
    {
        private void EnsureOutputBuffers(XRMesh mesh, bool isInterleaved)
        {
            int vertexCount = mesh.VertexCount;
            XRMeshRenderer.SkinnedOutputResourceSnapshot outputState =
                _renderer.CaptureSkinnedOutputResources();
            bool buffersExist = isInterleaved
                ? outputState.Interleaved is not null
                : outputState.Positions is not null;

            if (buffersExist && ReferenceEquals(_lastMesh, mesh) &&
                _lastVertexCount == vertexCount &&
                _lastWasInterleaved == isInterleaved)
            {
                return;
            }

            ReplaceOutputBuffers(mesh, isInterleaved);
        }

        /// <summary>
        /// Replaces invalid or incompatible output resources. Keeping the
        /// publication callbacks in this cold path prevents their compiler-
        /// generated closure from being allocated during steady frame updates.
        /// </summary>
        private void ReplaceOutputBuffers(XRMesh mesh, bool isInterleaved)
        {
            int vertexCount = mesh.VertexCount;

            // Also verify the output buffers still exist; they may have been
            // destroyed externally (for example, when compute skinning is toggled).
            XRMeshRenderer.SkinnedOutputResourceSnapshot outputState =
                _renderer.CaptureSkinnedOutputResources();
            bool buffersExist = isInterleaved
                ? outputState.Interleaved is not null
                : outputState.Positions is not null;

            if (buffersExist && ReferenceEquals(_lastMesh, mesh) && _lastVertexCount == vertexCount && _lastWasInterleaved == isInterleaved)
                return;

            XRMeshRenderer.SkinnedOutputResourceSnapshot oldOutputResources = default;
            XRMesh? oldLastMesh = null;
            int oldLastVertexCount = 0;
            bool oldLastWasInterleaved = false;
            bool oldHasValidOutput = false;
            bool oldSeededFromRenderState = false;
            bool oldSeedInputsSettled = false;
            bool oldSettleLogged = false;
            bool oldLastUsedPrecombinedBlendshapes = false;

            using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
            XRDataBuffer? newPositions = null;
            XRDataBuffer? newNormals = null;
            XRDataBuffer? newTangents = null;
            XRDataBuffer? newInterleaved = null;
            bool lifetimeLeaseHeld = false;

            if (isInterleaved)
            {
                int stride = (int)mesh.InterleavedStride;
                uint vertexWords = (uint)(vertexCount * stride / sizeof(float));
                uint boundsWordOffset = AlignUp(vertexWords, 4u);
                newInterleaved = new XRDataBuffer(
                    "SkinnedInterleaved",
                    EBufferTarget.ShaderStorageBuffer,
                    boundsWordOffset + 8u,
                    EComponentType.Float,
                    1,
                    true,
                    false)
                {
                    BindingIndexOverride = MeshDeformationBindingLayout.ComputeInterleaved,
                    Usage = EBufferUsage.DynamicDraw,
                    DisposeOnPush = false
                };
            }
            else
            {
                newPositions = new XRDataBuffer(
                    "SkinnedPositions",
                    EBufferTarget.ShaderStorageBuffer,
                    (uint)vertexCount + 2u,
                    EComponentType.Float,
                    4,
                    true,
                    false)
                {
                    BindingIndexOverride = MeshDeformationBindingLayout.ComputePosition,
                    Usage = EBufferUsage.DynamicDraw,
                    DisposeOnPush = false
                };

                if (mesh.HasNormals)
                    newNormals = new XRDataBuffer(
                        "SkinnedNormals",
                        EBufferTarget.ShaderStorageBuffer,
                        (uint)vertexCount,
                        EComponentType.Float,
                        4,
                        true,
                        false)
                    {
                        BindingIndexOverride = MeshDeformationBindingLayout.ComputeNormal,
                        Usage = EBufferUsage.DynamicDraw,
                        DisposeOnPush = false
                    };

                if (mesh.HasTangents)
                    newTangents = new XRDataBuffer(
                        "SkinnedTangents",
                        EBufferTarget.ShaderStorageBuffer,
                        (uint)vertexCount,
                        EComponentType.Float,
                        4,
                        true,
                        false)
                    {
                        BindingIndexOverride = MeshDeformationBindingLayout.ComputeTangent,
                        Usage = EBufferUsage.DynamicDraw,
                        DisposeOnPush = false
                    };
            }

            publication.Complete(
                beforeCachePublication: () =>
                {
                    _renderer.EnterResourcePublicationLease();
                    lifetimeLeaseHeld = true;
                    if (Volatile.Read(ref _disposed) != 0)
                        throw new ObjectDisposedException(nameof(RendererResources));

                    oldOutputResources = _renderer.CaptureSkinnedOutputResources();
                    oldLastMesh = _lastMesh;
                    oldLastVertexCount = _lastVertexCount;
                    oldLastWasInterleaved = _lastWasInterleaved;
                    oldHasValidOutput = _hasValidOutput;
                    oldSeededFromRenderState = _seededFromRenderState;
                    oldSeedInputsSettled = _seedInputsSettled;
                    oldSettleLogged = _settleLogged;
                    oldLastUsedPrecombinedBlendshapes = _lastUsedPrecombinedBlendshapes;
                    _renderer.InstallSkinnedOutputResources(
                        newPositions,
                        newNormals,
                        newTangents,
                        newInterleaved);
                    _lastMesh = mesh;
                    _lastVertexCount = vertexCount;
                    _lastWasInterleaved = isInterleaved;
                    _hasValidOutput = false;
                    _seededFromRenderState = false;
                    _seedInputsSettled = false;
                    _settleLogged = false;
                    _lastUsedPrecombinedBlendshapes = false;
                    _renderer.ValidateResourcePublicationLease();
                },
                rollbackOnFailure: () =>
                {
                    if (!lifetimeLeaseHeld)
                        return;
                    try
                    {
                        _renderer.InstallSkinnedOutputResources(
                            oldOutputResources.Positions,
                            oldOutputResources.Normals,
                            oldOutputResources.Tangents,
                            oldOutputResources.Interleaved);
                        _lastMesh = oldLastMesh;
                        _lastVertexCount = oldLastVertexCount;
                        _lastWasInterleaved = oldLastWasInterleaved;
                        _hasValidOutput = oldHasValidOutput;
                        _seededFromRenderState = oldSeededFromRenderState;
                        _seedInputsSettled = oldSeedInputsSettled;
                        _settleLogged = oldSettleLogged;
                        _lastUsedPrecombinedBlendshapes = oldLastUsedPrecombinedBlendshapes;
                    }
                    finally
                    {
                        lifetimeLeaseHeld = false;
                        _renderer.ExitResourcePublicationLease();
                    }
                },
                afterPublication: () =>
                {
                    if (!lifetimeLeaseHeld)
                        return;
                    try
                    {
                        oldOutputResources.Positions?.Destroy();
                        oldOutputResources.Normals?.Destroy();
                        oldOutputResources.Tangents?.Destroy();
                        oldOutputResources.Interleaved?.Destroy();
                        _renderer.ResetSkinPaletteSeedState();
                        _renderer.MarkSkinnedOutputDirty();
                    }
                    finally
                    {
                        lifetimeLeaseHeld = false;
                        _renderer.ExitResourcePublicationLease();
                    }
                });
        }
    }
}
