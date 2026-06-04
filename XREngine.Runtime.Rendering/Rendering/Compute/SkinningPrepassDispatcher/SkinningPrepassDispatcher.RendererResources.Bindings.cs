using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    // SSBO binding application for the active mesh layout. The numeric slots are centralized
    // in SkinningPrepassBindings so shader layout drift is easier to spot during review.
    private sealed partial class RendererResources
    {
        public void BindBlocks(
            XRRenderProgram program,
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
                BindStorageBuffer(program, mesh.InterleavedVertexBuffer, SkinningPrepassBindings.InterleavedInput);
                BindStorageBuffer(program, _renderer.SkinnedInterleavedBuffer, SkinningPrepassBindings.InterleavedOutput);
            }
            else
            {
                BindStorageBuffer(program, mesh.PositionsBuffer, SkinningPrepassBindings.NonInterleavedPositionInput);
                BindStorageBuffer(program, mesh.NormalsBuffer, SkinningPrepassBindings.NonInterleavedNormalInput);
                BindStorageBuffer(program, mesh.TangentsBuffer, SkinningPrepassBindings.NonInterleavedTangentInput);

                BindStorageBuffer(program, _renderer.SkinnedPositionsBuffer, SkinningPrepassBindings.NonInterleavedPositionOutput);
                BindStorageBuffer(program, _renderer.SkinnedNormalsBuffer, SkinningPrepassBindings.NonInterleavedNormalOutput);
                BindStorageBuffer(program, _renderer.SkinnedTangentsBuffer, SkinningPrepassBindings.NonInterleavedTangentOutput);
            }

            if (doSkinning)
            {
                BindStorageBuffer(program, skinPalette, SkinningPrepassBindings.SkinPalette);
                BindStorageBuffer(program, mesh.BoneInfluenceCoreIndices, SkinningPrepassBindings.BoneCoreIndices);
                BindStorageBuffer(program, mesh.BoneInfluenceCoreWeights, SkinningPrepassBindings.BoneCoreWeights);

                XRDataBuffer? spillHeaders = mesh.HasSpillInfluences ? mesh.BoneInfluenceSpillHeaders : emptySpillHeaders;
                XRDataBuffer? spillEntries = mesh.HasSpillInfluences ? mesh.BoneInfluenceSpillEntries : emptySpillEntries;

                // Spill influence buffers have different bindings for interleaved vs non-interleaved.
                if (isInterleaved)
                {
                    BindStorageBuffer(program, spillHeaders, SkinningPrepassBindings.InterleavedSpillHeaders);
                    BindStorageBuffer(program, spillEntries, SkinningPrepassBindings.InterleavedSpillEntries);
                }
                else
                {
                    BindStorageBuffer(program, spillHeaders, SkinningPrepassBindings.NonInterleavedSpillHeaders);
                    BindStorageBuffer(program, spillEntries, SkinningPrepassBindings.NonInterleavedSpillEntries);
                }
            }

            if (doBlendshapes)
            {
                if (usePrecombinedBlendshapes)
                {
                    BindStorageBuffer(program, _renderer.PrecombinedBlendshapePositionsBuffer, SkinningPrepassBindings.BlendshapeSparseShapeRanges);
                    BindStorageBuffer(program, _renderer.PrecombinedBlendshapeNormalsBuffer, SkinningPrepassBindings.BlendshapeSparseRecords);
                    BindStorageBuffer(program, _renderer.PrecombinedBlendshapeTangentsBuffer, SkinningPrepassBindings.BlendshapeQuantizedDeltas);
                }
                else
                {
                    BindStorageBuffer(program, _renderer.BlendshapeActiveWeights, SkinningPrepassBindings.BlendshapeActiveWeights);
                    BindStorageBuffer(program, mesh.BlendshapeSparseShapeRanges, SkinningPrepassBindings.BlendshapeSparseShapeRanges);
                    BindStorageBuffer(program, mesh.BlendshapeSparseRecords, SkinningPrepassBindings.BlendshapeSparseRecords);
                    BindStorageBuffer(program, mesh.BlendshapeQuantizedDeltas, SkinningPrepassBindings.BlendshapeQuantizedDeltas);
                    BindStorageBuffer(program, mesh.BlendshapeQuantizationMetadata, SkinningPrepassBindings.BlendshapeQuantizationMetadata);
                }
            }
        }

        private static void BindStorageBuffer(XRRenderProgram program, XRDataBuffer? buffer, uint binding)
        {
            if (buffer is null)
                return;

            buffer.SetBlockIndex(binding);
            program.BindBuffer(buffer, binding);
        }
    }
}
