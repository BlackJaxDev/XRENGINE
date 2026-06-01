using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    // SSBO binding application for the active mesh layout. The numeric slots are centralized
    // in SkinningPrepassBindings so shader layout drift is easier to spot during review.
    private sealed partial class RendererResources
    {
        public void BindBlocks(
            bool doSkinning,
            bool doBlendshapes,
            bool isInterleaved,
            bool useGlobalBlendshapeWeights,
            bool usePrecombinedBlendshapes,
            XRDataBuffer? skinPalette,
            XRDataBuffer? globalBlendshapeWeights,
            XRDataBuffer? emptySpillHeaders,
            XRDataBuffer? emptySpillEntries)
        {
            var mesh = _renderer.Mesh;
            if (mesh is null)
                return;

            if (isInterleaved)
            {
                mesh.InterleavedVertexBuffer?.SetBlockIndex(SkinningPrepassBindings.InterleavedInput);
                _renderer.SkinnedInterleavedBuffer?.SetBlockIndex(SkinningPrepassBindings.InterleavedOutput);
            }
            else
            {
                mesh.PositionsBuffer?.SetBlockIndex(SkinningPrepassBindings.NonInterleavedPositionInput);
                mesh.NormalsBuffer?.SetBlockIndex(SkinningPrepassBindings.NonInterleavedNormalInput);
                mesh.TangentsBuffer?.SetBlockIndex(SkinningPrepassBindings.NonInterleavedTangentInput);

                _renderer.SkinnedPositionsBuffer?.SetBlockIndex(SkinningPrepassBindings.NonInterleavedPositionOutput);
                _renderer.SkinnedNormalsBuffer?.SetBlockIndex(SkinningPrepassBindings.NonInterleavedNormalOutput);
                _renderer.SkinnedTangentsBuffer?.SetBlockIndex(SkinningPrepassBindings.NonInterleavedTangentOutput);
            }

            if (doSkinning)
            {
                skinPalette?.SetBlockIndex(SkinningPrepassBindings.SkinPalette);
                mesh.BoneInfluenceCoreIndices?.SetBlockIndex(SkinningPrepassBindings.BoneCoreIndices);
                mesh.BoneInfluenceCoreWeights?.SetBlockIndex(SkinningPrepassBindings.BoneCoreWeights);

                XRDataBuffer? spillHeaders = mesh.HasSpillInfluences ? mesh.BoneInfluenceSpillHeaders : emptySpillHeaders;
                XRDataBuffer? spillEntries = mesh.HasSpillInfluences ? mesh.BoneInfluenceSpillEntries : emptySpillEntries;

                // Spill influence buffers have different bindings for interleaved vs non-interleaved.
                if (isInterleaved)
                {
                    spillHeaders?.SetBlockIndex(SkinningPrepassBindings.InterleavedSpillHeaders);
                    spillEntries?.SetBlockIndex(SkinningPrepassBindings.InterleavedSpillEntries);
                }
                else
                {
                    spillHeaders?.SetBlockIndex(SkinningPrepassBindings.NonInterleavedSpillHeaders);
                    spillEntries?.SetBlockIndex(SkinningPrepassBindings.NonInterleavedSpillEntries);
                }
            }

            if (doBlendshapes)
            {
                if (usePrecombinedBlendshapes)
                {
                    _renderer.PrecombinedBlendshapePositionsBuffer?.SetBlockIndex(SkinningPrepassBindings.BlendshapeSparseShapeRanges);
                    _renderer.PrecombinedBlendshapeNormalsBuffer?.SetBlockIndex(SkinningPrepassBindings.BlendshapeSparseRecords);
                    _renderer.PrecombinedBlendshapeTangentsBuffer?.SetBlockIndex(SkinningPrepassBindings.BlendshapeQuantizedDeltas);
                }
                else
                {
                    _renderer.BlendshapeActiveWeights?.SetBlockIndex(SkinningPrepassBindings.BlendshapeActiveWeights);
                    mesh.BlendshapeSparseShapeRanges?.SetBlockIndex(SkinningPrepassBindings.BlendshapeSparseShapeRanges);
                    mesh.BlendshapeSparseRecords?.SetBlockIndex(SkinningPrepassBindings.BlendshapeSparseRecords);
                    mesh.BlendshapeQuantizedDeltas?.SetBlockIndex(SkinningPrepassBindings.BlendshapeQuantizedDeltas);
                    mesh.BlendshapeQuantizationMetadata?.SetBlockIndex(SkinningPrepassBindings.BlendshapeQuantizationMetadata);
                }
            }
        }
    }
}
