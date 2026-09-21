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

            XRMeshSkinningBufferState skinningState = mesh.GetSkinningBufferStateSnapshot();
            XRMeshBlendshapeBufferState blendshapeState = mesh.GetBlendshapeBufferStateSnapshot();
            XRMeshRenderer.BlendshapeResourceSnapshot rendererBlendshapeState =
                _renderer.CaptureBlendshapeResources();
            XRMeshRenderer.SkinnedOutputResourceSnapshot outputState =
                _renderer.CaptureSkinnedOutputResources();
            XRDataBuffer zero = emptyBuffers.ZeroScalar;

            if (isInterleaved)
            {
                BindStorageBuffer(program, mesh.InterleavedVertexBuffer, SkinningPrepassBindings.InterleavedInput);
                BindStorageBuffer(program, outputState.Interleaved, SkinningPrepassBindings.InterleavedOutput);
            }
            else
            {
                BindStorageBuffer(program, mesh.PositionsBuffer, SkinningPrepassBindings.NonInterleavedPositionInput);
                BindStorageBuffer(program, mesh.NormalsBuffer ?? zero, SkinningPrepassBindings.NonInterleavedNormalInput);
                BindStorageBuffer(program, mesh.TangentsBuffer ?? zero, SkinningPrepassBindings.NonInterleavedTangentInput);

                BindStorageBuffer(program, outputState.Positions, SkinningPrepassBindings.NonInterleavedPositionOutput);
                BindStorageBuffer(program, outputState.Normals ?? zero, SkinningPrepassBindings.NonInterleavedNormalOutput);
                BindStorageBuffer(program, outputState.Tangents ?? zero, SkinningPrepassBindings.NonInterleavedTangentOutput);
            }

            BindStorageBuffer(program, doSkinning ? skinPalette : zero, SkinningPrepassBindings.SkinPalette);
            BindStorageBuffer(program, doSkinning ? skinningState.CoreIndices : zero, SkinningPrepassBindings.BoneCoreIndices);
            BindStorageBuffer(program, doSkinning ? skinningState.CoreWeights : zero, SkinningPrepassBindings.BoneCoreWeights);

            XRDataBuffer spillHeaders = doSkinning && skinningState.HasSpillInfluences
                ? skinningState.SpillHeaders ?? emptyBuffers.SpillHeaders
                : emptyBuffers.SpillHeaders;
            XRDataBuffer spillEntries = doSkinning && skinningState.HasSpillInfluences
                ? skinningState.SpillEntries ?? emptyBuffers.SpillEntries
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
                BindStorageBuffer(program, rendererBlendshapeState.PrecombinedPositions ?? zero, SkinningPrepassBindings.BlendshapeSparseShapeRanges);
                BindStorageBuffer(program, rendererBlendshapeState.PrecombinedNormals ?? zero, SkinningPrepassBindings.BlendshapeSparseRecords);
                BindStorageBuffer(program, rendererBlendshapeState.PrecombinedTangents ?? zero, SkinningPrepassBindings.BlendshapeQuantizedDeltas);
            }
            else
            {
                XRDataBuffer? blendWeights = useGlobalBlendshapeWeights
                    ? globalBlendshapeWeights
                    : rendererBlendshapeState.ActiveWeights;
                BindStorageBuffer(program, doBlendshapes ? blendWeights : zero, SkinningPrepassBindings.BlendshapeActiveWeights);
                BindStorageBuffer(program, doBlendshapes ? blendshapeState.SparseShapeRanges : zero, SkinningPrepassBindings.BlendshapeSparseShapeRanges);
                BindStorageBuffer(program, doBlendshapes ? blendshapeState.SparseRecords : zero, SkinningPrepassBindings.BlendshapeSparseRecords);
                BindStorageBuffer(program, doBlendshapes ? blendshapeState.QuantizedDeltas : zero, SkinningPrepassBindings.BlendshapeQuantizedDeltas);
                BindStorageBuffer(program, doBlendshapes ? blendshapeState.QuantizationMetadata : zero, SkinningPrepassBindings.BlendshapeQuantizationMetadata);
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
