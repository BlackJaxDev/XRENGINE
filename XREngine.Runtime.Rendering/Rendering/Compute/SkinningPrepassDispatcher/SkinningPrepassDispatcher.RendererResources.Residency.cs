using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    private sealed partial class RendererResources
    {
        /// <summary>
        /// Re-seeds the per-renderer skin palette from current bone render state, re-pushing every
        /// utilized bone matrix as if it had just moved (which also invalidates the cached skinned
        /// output). See call site in <see cref="Run"/> for rationale.
        /// </summary>
        public void EnsureSeededFromRenderState(
            XRMesh mesh,
            bool isInterleaved,
            bool doSkinning,
            bool doBlendshapes,
            bool useGlobalBlendWeights)
        {
            // The first compute dispatch can run before the mesh's asynchronously-uploaded INPUT
            // buffers have finished uploading AND before the runtime-imported skeleton has settled
            // into its final pose. A runtime avatar publishes several frames of intermediate bone
            // poses at startup; if this renderer subscribed to RenderMatrixChanged after the final
            // pose was already published (init-order race), no further event fires. For a static
            // (non-animated) mesh that means the very first dispatch -- taken at an intermediate
            // pose and/or with not-yet-resident inputs -- gets latched by the output cache
            // (MarkOutputValid) and reused indefinitely, leaving the mesh visibly wrong ("exploded")
            // until a bone is MANUALLY moved (which fires RenderMatrixChanged -> re-dispatch -> fix).
            //
            // Fix: keep re-seeding (re-push ALL bone matrices as if they moved, which also marks the
            // skinned output dirty so it recomputes) on every dispatch until BOTH (a) the inputs are
            // observed naturally GPU-resident and (b) the bone pose hash is stable across two
            // consecutive frames. Only then do we let the output cache hold. This deterministically
            // captures the final settled pose without permanently re-dispatching.
            //
            // The pose-stability half is shared with the vertex draw path via
            // XRMeshRenderer.ReseedSkinPaletteUntilPoseStable; this path adds the input-residency
            // gate on top because the compute output cache (unlike the live vertex shader) freezes a
            // bad first dispatch forever.
            if (_seededFromRenderState && _seedInputsSettled)
                return;

            if (!_seededFromRenderState)
                _renderer.LogBoneSeedStalenessDiagnostics("FirstDispatch");

            bool inputsResident = AreSkinningInputsNaturallyResident(
                mesh, isInterleaved, doSkinning, doBlendshapes, useGlobalBlendWeights);

            bool poseStable = _renderer.ReseedSkinPaletteUntilPoseStable();
            _seededFromRenderState = true;
            // Settle only once the inputs are resident AND the pose stopped changing; the recompute
            // we just triggered therefore uses the final input data and final pose.
            _seedInputsSettled = inputsResident && poseStable;

            if (_seedInputsSettled && !_settleLogged)
            {
                _settleLogged = true;
                if (RenderDiagnosticsFlags.SkinningPrepassDiag)
                    Debug.LogWarning(
                        $"[SkinSettle] Mesh='{mesh.Name ?? "<null>"}' verts={mesh.VertexCount} settled after " +
                        $"{_renderer.SkinPaletteReseedCount} reseeds.");
            }
        }

        /// <summary>
        /// Returns true only if every read-only compute-skinning INPUT buffer for this dispatch is
        /// already GPU-resident WITHOUT forcing the upload (i.e. its async upload genuinely finished).
        /// Used to decide when the seed/output state has settled.
        /// </summary>
        private bool AreSkinningInputsNaturallyResident(
            XRMesh mesh,
            bool isInterleaved,
            bool doSkinning,
            bool doBlendshapes,
            bool useGlobalBlendWeights)
        {
            if (isInterleaved)
            {
                if (!IsBufferNaturallyResident(mesh.InterleavedVertexBuffer))
                    return false;
            }
            else
            {
                if (!IsBufferNaturallyResident(mesh.PositionsBuffer)
                    || !IsBufferNaturallyResident(mesh.NormalsBuffer)
                    || !IsBufferNaturallyResident(mesh.TangentsBuffer))
                    return false;
            }

            if (doSkinning)
            {
                if (!IsBufferNaturallyResident(mesh.BoneInfluenceCoreIndices)
                    || !IsBufferNaturallyResident(mesh.BoneInfluenceCoreWeights))
                    return false;
                if (mesh.HasSpillInfluences
                    && (!IsBufferNaturallyResident(mesh.BoneInfluenceSpillHeaders)
                        || !IsBufferNaturallyResident(mesh.BoneInfluenceSpillEntries)))
                    return false;
            }

            if (doBlendshapes)
            {
                if (!IsBufferNaturallyResident(_renderer.BlendshapeActiveWeights)
                    || !IsBufferNaturallyResident(mesh.BlendshapeSparseShapeRanges)
                    || !IsBufferNaturallyResident(mesh.BlendshapeSparseRecords)
                    || !IsBufferNaturallyResident(mesh.BlendshapeQuantizedDeltas)
                    || !IsBufferNaturallyResident(mesh.BlendshapeQuantizationMetadata))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// True if the buffer is null (nothing to upload) or its GPU wrapper reports ready WITHOUT
        /// forcing the upload to complete.
        /// </summary>
        private static bool IsBufferNaturallyResident(XRDataBuffer? buffer)
        {
            if (buffer is null)
                return true;
            foreach (var wrapper in buffer.APIWrappers)
            {
                if (wrapper is IApiDataBuffer apiBuffer && !apiBuffer.BackendIsReadyForGpuUse)
                    return false;
            }
            return true;
        }

        private bool _residencyLogged;

        /// <summary>
        /// Forces every read-only compute-skinning INPUT buffer required for this dispatch to be
        /// fully GPU-resident (generated, pending async upload flushed, storage allocated) before
        /// the dispatch binds and reads them. Output buffers are deliberately NOT touched here: they
        /// are written by the compute shader and re-uploading them would clobber the skinned results
        /// with stale client data.
        /// </summary>
        public void EnsureSkinningInputsResident(
            XRMesh mesh,
            XRDataBuffer? skinPalette,
            XRDataBuffer? globalBlendshapeWeights,
            bool isInterleaved,
            bool doSkinning,
            bool doBlendshapes,
            bool useGlobalBlendWeights,
            bool usePrecombinedBlendshapes)
        {
            // Source vertex data (read-only inputs).
            if (isInterleaved)
                EnsureBufferResident(mesh.InterleavedVertexBuffer);
            else
            {
                EnsureBufferResident(mesh.PositionsBuffer);
                EnsureBufferResident(mesh.NormalsBuffer);
                EnsureBufferResident(mesh.TangentsBuffer);
            }

            if (doSkinning)
            {
                EnsureBufferResident(skinPalette);
                EnsureBufferResident(mesh.BoneInfluenceCoreIndices);
                EnsureBufferResident(mesh.BoneInfluenceCoreWeights);
                if (mesh.HasSpillInfluences)
                {
                    EnsureBufferResident(mesh.BoneInfluenceSpillHeaders);
                    EnsureBufferResident(mesh.BoneInfluenceSpillEntries);
                }
            }

            if (doBlendshapes)
            {
                if (usePrecombinedBlendshapes)
                {
                    EnsureBufferResident(_renderer.PrecombinedBlendshapePositionsBuffer);
                    EnsureBufferResident(_renderer.PrecombinedBlendshapeNormalsBuffer);
                    EnsureBufferResident(_renderer.PrecombinedBlendshapeTangentsBuffer);
                }
                else
                {
                    EnsureBufferResident(_renderer.BlendshapeActiveWeights);
                    EnsureBufferResident(mesh.BlendshapeSparseShapeRanges);
                    EnsureBufferResident(mesh.BlendshapeSparseRecords);
                    EnsureBufferResident(mesh.BlendshapeQuantizedDeltas);
                    EnsureBufferResident(mesh.BlendshapeQuantizationMetadata);
                }
            }

            LogBufferResidencyOnce(mesh, skinPalette, isInterleaved);
        }

        /// <summary>
        /// Forces the compute-skinning OUTPUT buffer(s) to have GPU storage allocated BEFORE the
        /// dispatch writes to them. Their storage otherwise allocates lazily at draw time (after the
        /// compute pass), so the compute shader would write into a zero-byte buffer and the draw
        /// would then read zero-initialized client data -> degenerate (missing) geometry. Unlike the
        /// inputs, this is safe to call every dispatch: <see cref="GLDataBuffer.EnsureStorageAllocatedForGpuCopy"/>
        /// only pushes client data while it has not yet been fully pushed, so once the storage exists
        /// it becomes a no-op and never clobbers the compute-written results.
        /// </summary>
        public void EnsureSkinningOutputResident(bool isInterleaved, bool doSkinning)
        {
            if (!doSkinning)
                return;

            if (isInterleaved)
                EnsureBufferResident(_renderer.SkinnedInterleavedBuffer);
            else
            {
                EnsureBufferResident(_renderer.SkinnedPositionsBuffer);
                EnsureBufferResident(_renderer.SkinnedNormalsBuffer);
                EnsureBufferResident(_renderer.SkinnedTangentsBuffer);
            }
        }

        /// <summary>
        /// If the buffer's GPU wrapper has not finished uploading, force the upload to complete now
        /// so the data is GPU-resident before the compute shader reads it. Null / already-resident
        /// buffers are a no-op.
        /// </summary>
        private static void EnsureBufferResident(XRDataBuffer? buffer)
        {
            if (buffer is null)
                return;
            foreach (var wrapper in buffer.APIWrappers)
            {
                if (wrapper is IApiDataBuffer apiBuffer && !apiBuffer.BackendIsReadyForGpuUse)
                    apiBuffer.EnsureStorageAllocatedForGpuUse();
            }
        }

        /// <summary>
        /// One-shot diagnostic: logs whether the skinning compute INPUT buffers are GPU-resident
        /// at the first dispatch (after <see cref="EnsureSkinningInputsResident"/> has run, so they
        /// should all read 'ready' if the residency fix is working).
        /// </summary>
        public void LogBufferResidencyOnce(XRMesh mesh, XRDataBuffer? skinPalette, bool isInterleaved)
        {
            if (_residencyLogged || !RenderDiagnosticsFlags.SkinningPrepassDiag)
                return;
            _residencyLogged = true;

            XRDataBuffer? positions = isInterleaved ? mesh.InterleavedVertexBuffer : mesh.PositionsBuffer;
            Debug.LogWarning(
                $"[SkinResidency] Mesh='{mesh.Name ?? "<null>"}' verts={mesh.VertexCount} " +
                $"palette={ResidencyState(skinPalette)} coreIdx={ResidencyState(mesh.BoneInfluenceCoreIndices)} " +
                $"coreWt={ResidencyState(mesh.BoneInfluenceCoreWeights)} pos={ResidencyState(positions)} " +
                $"spillHdr={ResidencyState(mesh.BoneInfluenceSpillHeaders)} spillEnt={ResidencyState(mesh.BoneInfluenceSpillEntries)} " +
                $"hasSpill={mesh.HasSpillInfluences}");
        }

        private static string ResidencyState(XRDataBuffer? buffer)
        {
            if (buffer is null)
                return "null";
            foreach (var wrapper in buffer.APIWrappers)
            {
                if (wrapper is IApiDataBuffer apiBuffer)
                    return apiBuffer.BackendIsReadyForGpuUse ? "ready" : "PENDING";
            }
            return "NOWRAP";
        }
    }
}
