using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    // Per-renderer cache and state shared by the residency, output-buffer, binding, and
    // diagnostic partials. Instances are held in a ConditionalWeakTable by the dispatcher.
    private sealed partial class RendererResources(XRMeshRenderer renderer)
    {
        private readonly XRMeshRenderer _renderer = renderer;
        private bool _seededFromRenderState;
        private bool _seedInputsSettled;
        private bool _settleLogged;
        private XRMesh? _lastMesh;

        private int _lastVertexCount;
        private bool _lastWasInterleaved;
        private bool _hasValidOutput;
        private bool _lastDidSkinning;
        private bool _lastDidBlendshapes;
        private bool _lastUsedPrecombinedBlendshapes;
        private ulong _lastOutputVersion;

        public ulong LastComputePrepassFrameId;

        /// <summary>
        /// Gets the output buffer containing skinned positions.
        /// </summary>
        public XRDataBuffer? SkinnedPositions => _renderer.SkinnedPositionsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned normals.
        /// </summary>
        public XRDataBuffer? SkinnedNormals => _renderer.SkinnedNormalsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned tangents.
        /// </summary>
        public XRDataBuffer? SkinnedTangents => _renderer.SkinnedTangentsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned interleaved data.
        /// </summary>
        public XRDataBuffer? SkinnedInterleaved => _renderer.SkinnedInterleavedBuffer;

        public bool Validate(XRMesh mesh, bool doSkinning, bool doBlendshapes, bool isInterleaved, bool usePrecombinedBlendshapes)
        {
            // Validate read-side prerequisites first; output buffers are allocated only after
            // the mesh proves it can actually participate in the selected compute path.
            if (doSkinning)
            {
                if (_renderer.ActiveSkinPaletteBuffer is null)
                    return false;
                if (!mesh.SupportsComputeSkinning)
                    return false;
            }

            if (doBlendshapes)
            {
                if (usePrecombinedBlendshapes)
                {
                    if (!_renderer.HasValidPrecombinedBlendshapeDeltas
                        || _renderer.PrecombinedBlendshapePositionsBuffer is null
                        || (mesh.HasNormals && _renderer.PrecombinedBlendshapeNormalsBuffer is null)
                        || (mesh.HasTangents && _renderer.PrecombinedBlendshapeTangentsBuffer is null))
                    {
                        return false;
                    }
                }
                else
                {
                    if (mesh.BlendshapeSparseShapeRanges is null
                        || mesh.BlendshapeSparseRecords is null
                        || mesh.BlendshapeQuantizedDeltas is null
                        || mesh.BlendshapeQuantizationMetadata is null)
                        return false;
                    if (_renderer.BlendshapeActiveWeights is null)
                        return false;
                }
            }

            if (isInterleaved)
            {
                if (mesh.InterleavedVertexBuffer is null)
                    return false;
            }
            else
            {
                if (mesh.PositionsBuffer is null)
                    return false;
            }

            EnsureOutputBuffers(mesh, isInterleaved);

            return true;
        }

        public bool CanReuseOutput(bool doSkinning, bool doBlendshapes, bool usePrecombinedBlendshapes)
        {
            // Cached output is valid only for stable renderer-owned inputs. External/gpu-driven
            // skin palettes can change without this cache seeing a renderer dirty flag.
            if (doSkinning && !_seedInputsSettled)
                return false;

            if (!_hasValidOutput
                || _renderer.SkinnedOutputDirty
                || _lastDidSkinning != doSkinning
                || _lastDidBlendshapes != doBlendshapes
                || _lastUsedPrecombinedBlendshapes != usePrecombinedBlendshapes
                || _renderer.HasPendingComputeSkinningInputChanges
                || _lastOutputVersion != _renderer.SkinnedOutputVersion
                || (doSkinning && (_renderer.HasExternalSkinPaletteSource || _renderer.HasGpuDrivenBoneSource)))
            {
                return false;
            }

            return doSkinning || doBlendshapes;
        }

        public void MarkOutputValid(bool doSkinning, bool doBlendshapes, bool usePrecombinedBlendshapes)
        {
            _hasValidOutput = true;
            _lastDidSkinning = doSkinning;
            _lastDidBlendshapes = doBlendshapes;
            _lastUsedPrecombinedBlendshapes = usePrecombinedBlendshapes;
            _lastOutputVersion = _renderer.SkinnedOutputVersion;
            _renderer.MarkSkinnedOutputClean();
        }

        public void SyncDynamicBuffers(bool pushSkinPalette, bool pushBlendshapeWeights)
        {
            if (pushSkinPalette)
                _renderer.PushBoneMatricesToGPU();
            if (pushBlendshapeWeights)
                _renderer.PushBlendshapeWeightsToGPU();
        }

        public void Dispose()
        {
            _renderer.SkinnedPositionsBuffer?.Destroy();
            _renderer.SkinnedNormalsBuffer?.Destroy();
            _renderer.SkinnedTangentsBuffer?.Destroy();
            _renderer.SkinnedInterleavedBuffer?.Destroy();
            _renderer.SkinnedPositionsBuffer = null;
            _renderer.SkinnedNormalsBuffer = null;
            _renderer.SkinnedTangentsBuffer = null;
            _renderer.SkinnedInterleavedBuffer = null;
            _lastVertexCount = 0;
            _lastWasInterleaved = false;
            _lastMesh = null;
            _hasValidOutput = false;
            _seededFromRenderState = false;
            _seedInputsSettled = false;
            _settleLogged = false;
            _renderer.ResetSkinPaletteSeedState();
            _lastDidSkinning = false;
            _lastDidBlendshapes = false;
            _lastUsedPrecombinedBlendshapes = false;
            _lastOutputVersion = 0;
        }
    }
}
