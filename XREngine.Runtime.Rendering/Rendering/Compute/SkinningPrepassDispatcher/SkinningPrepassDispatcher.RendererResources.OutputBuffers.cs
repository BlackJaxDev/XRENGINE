using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    private sealed partial class RendererResources
    {
        private void EnsureOutputBuffers(XRMesh mesh, bool isInterleaved)
        {
            int vertexCount = mesh.VertexCount;

            // Also verify the output buffers still exist; they may have been
            // destroyed externally (for example, when compute skinning is toggled).
            bool buffersExist = isInterleaved
                ? _renderer.SkinnedInterleavedBuffer is not null
                : _renderer.SkinnedPositionsBuffer is not null;

            if (buffersExist && ReferenceEquals(_lastMesh, mesh) && _lastVertexCount == vertexCount && _lastWasInterleaved == isInterleaved)
                return;

            _lastMesh = mesh;
            _lastVertexCount = vertexCount;
            _lastWasInterleaved = isInterleaved;

            // Output buffers are renderer-owned because the draw path consumes them directly.
            _renderer.SkinnedPositionsBuffer?.Destroy();
            _renderer.SkinnedNormalsBuffer?.Destroy();
            _renderer.SkinnedTangentsBuffer?.Destroy();
            _renderer.SkinnedInterleavedBuffer?.Destroy();
            _renderer.SkinnedPositionsBuffer = null;
            _renderer.SkinnedNormalsBuffer = null;
            _renderer.SkinnedTangentsBuffer = null;
            _renderer.SkinnedInterleavedBuffer = null;
            _hasValidOutput = false;
            _seededFromRenderState = false;
            _seedInputsSettled = false;
            _settleLogged = false;
            _renderer.ResetSkinPaletteSeedState();
            _renderer.MarkSkinnedOutputDirty();
            _lastUsedPrecombinedBlendshapes = false;

            if (isInterleaved)
            {
                int stride = (int)mesh.InterleavedStride;
                uint vertexWords = (uint)(vertexCount * stride / sizeof(float));
                uint boundsWordOffset = AlignUp(vertexWords, 4u);
                _renderer.SkinnedInterleavedBuffer = new XRDataBuffer(
                    "SkinnedInterleaved",
                    EBufferTarget.ShaderStorageBuffer,
                    boundsWordOffset + 8u,
                    EComponentType.Float,
                    1,
                    true,
                    false)
                {
                    BindingIndexOverride = 9u,
                    Usage = EBufferUsage.DynamicDraw,
                    DisposeOnPush = false
                };
            }
            else
            {
                _renderer.SkinnedPositionsBuffer = new XRDataBuffer(
                    "SkinnedPositions",
                    EBufferTarget.ShaderStorageBuffer,
                    (uint)vertexCount + 2u,
                    EComponentType.Float,
                    4,
                    true,
                    false)
                {
                    BindingIndexOverride = 11u,
                    Usage = EBufferUsage.DynamicDraw,
                    DisposeOnPush = false
                };

                if (mesh.HasNormals)
                {
                    _renderer.SkinnedNormalsBuffer = new XRDataBuffer(
                        "SkinnedNormals",
                        EBufferTarget.ShaderStorageBuffer,
                        (uint)vertexCount,
                        EComponentType.Float,
                        4,
                        true,
                        false)
                    {
                        BindingIndexOverride = 12u,
                        Usage = EBufferUsage.DynamicDraw,
                        DisposeOnPush = false
                    };
                }

                if (mesh.HasTangents)
                {
                    _renderer.SkinnedTangentsBuffer = new XRDataBuffer(
                        "SkinnedTangents",
                        EBufferTarget.ShaderStorageBuffer,
                        (uint)vertexCount,
                        EComponentType.Float,
                        4,
                        true,
                        false)
                    {
                        BindingIndexOverride = 15u,
                        Usage = EBufferUsage.DynamicDraw,
                        DisposeOnPush = false
                    };
                }
            }
        }
    }
}
