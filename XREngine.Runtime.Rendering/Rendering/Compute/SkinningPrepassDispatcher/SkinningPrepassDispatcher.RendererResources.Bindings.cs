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
            EmptyStorageBuffers emptyBuffers)
        {
            var mesh = _renderer.Mesh;
            if (mesh is null)
                return;

            XRDataBuffer zero = emptyBuffers.ZeroScalar;

            if (isInterleaved)
            {
                BindStorageBuffer(program, mesh.InterleavedVertexBuffer, SkinningPrepassBindings.InterleavedInput);
                BindStorageBuffer(program, _renderer.SkinnedInterleavedBuffer, SkinningPrepassBindings.InterleavedOutput);
            }
            else
            {
                BindStorageBuffer(program, mesh.PositionsBuffer, SkinningPrepassBindings.NonInterleavedPositionInput);
                BindStorageBuffer(program, mesh.NormalsBuffer ?? zero, SkinningPrepassBindings.NonInterleavedNormalInput);
                BindStorageBuffer(program, mesh.TangentsBuffer ?? zero, SkinningPrepassBindings.NonInterleavedTangentInput);

                BindStorageBuffer(program, _renderer.SkinnedPositionsBuffer, SkinningPrepassBindings.NonInterleavedPositionOutput);
                BindStorageBuffer(program, _renderer.SkinnedNormalsBuffer ?? zero, SkinningPrepassBindings.NonInterleavedNormalOutput);
                BindStorageBuffer(program, _renderer.SkinnedTangentsBuffer ?? zero, SkinningPrepassBindings.NonInterleavedTangentOutput);
            }

            BindStorageBuffer(program, doSkinning ? skinPalette : zero, SkinningPrepassBindings.SkinPalette);
            BindStorageBuffer(program, doSkinning ? mesh.BoneInfluenceCoreIndices : zero, SkinningPrepassBindings.BoneCoreIndices);
            BindStorageBuffer(program, doSkinning ? mesh.BoneInfluenceCoreWeights : zero, SkinningPrepassBindings.BoneCoreWeights);

            XRDataBuffer spillHeaders = doSkinning && mesh.HasSpillInfluences
                ? mesh.BoneInfluenceSpillHeaders ?? emptyBuffers.SpillHeaders
                : emptyBuffers.SpillHeaders;
            XRDataBuffer spillEntries = doSkinning && mesh.HasSpillInfluences
                ? mesh.BoneInfluenceSpillEntries ?? emptyBuffers.SpillEntries
                : emptyBuffers.SpillEntries;

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

            if (usePrecombinedBlendshapes)
            {
                BindStorageBuffer(program, _renderer.PrecombinedBlendshapePositionsBuffer ?? zero, SkinningPrepassBindings.BlendshapeSparseShapeRanges);
                BindStorageBuffer(program, _renderer.PrecombinedBlendshapeNormalsBuffer ?? zero, SkinningPrepassBindings.BlendshapeSparseRecords);
                BindStorageBuffer(program, _renderer.PrecombinedBlendshapeTangentsBuffer ?? zero, SkinningPrepassBindings.BlendshapeQuantizedDeltas);
            }
            else
            {
                XRDataBuffer? blendWeights = useGlobalBlendshapeWeights
                    ? globalBlendshapeWeights
                    : _renderer.BlendshapeActiveWeights;
                BindStorageBuffer(program, doBlendshapes ? blendWeights : zero, SkinningPrepassBindings.BlendshapeActiveWeights);
                BindStorageBuffer(program, doBlendshapes ? mesh.BlendshapeSparseShapeRanges : zero, SkinningPrepassBindings.BlendshapeSparseShapeRanges);
                BindStorageBuffer(program, doBlendshapes ? mesh.BlendshapeSparseRecords : zero, SkinningPrepassBindings.BlendshapeSparseRecords);
                BindStorageBuffer(program, doBlendshapes ? mesh.BlendshapeQuantizedDeltas : zero, SkinningPrepassBindings.BlendshapeQuantizedDeltas);
                BindStorageBuffer(program, doBlendshapes ? mesh.BlendshapeQuantizationMetadata : zero, SkinningPrepassBindings.BlendshapeQuantizationMetadata);
            }
        }

        private static void BindStorageBuffer(XRRenderProgram program, XRDataBuffer? buffer, uint binding)
        {
            if (buffer is null)
                return;

            buffer.BindTo(program, binding);
        }
    }
}
