using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuAdvancedVisibilityOutput
{
    private WebGpuAdvancedNativeVertexHistory[] _nativeVertexHistory = [];
    private ulong _nativeVertexHistoryFrame;
    private ulong _nativeVertexHistoryEpoch;

    private bool TryPrepareNativeVertices(WebGpuAdvancedVisibilityFrame frame,
        AdvancedGpuScenePublicationSnapshot snapshot, ulong worldFrameId, uint viewId, out string reason)
    {
        WebGpuAdvancedNativeVertexFrame native = frame.NativeVertices;
        Span<AdvancedVisibilityPayload> payloads = _inputs.MutablePayloads;
        Span<AdvancedVisibilityCandidate> candidates = _inputs.MutableCandidates;
        native.Begin(payloads.Length, worldFrameId, _renderer.EngineFrameSequence);
        uint currentBytes = Math.Max(frame.CurrentDeformationBytes, snapshot.GeometryPayloads.PreSkinnedCurrent.ByteCount);
        uint previousBytes = Math.Max(frame.PreviousDeformationBytes, snapshot.GeometryPayloads.PreSkinnedPrevious.ByteCount);
        if ((currentBytes & 63u) != 0 || (previousBytes & 63u) != 0)
        {
            reason = "WebGPU.Advanced.NativeVertexAlignment: canonical deformation streams must contain complete 64-byte vertices.";
            return false;
        }
        bool adjacentHistory = worldFrameId != 0 && _nativeVertexHistoryFrame == worldFrameId - 1 &&
            _nativeVertexHistoryEpoch == snapshot.DatabaseEpoch;
        for (int index = 0; index < payloads.Length; index++)
        {
            AdvancedVisibilityPayload source = payloads[index];
            if (source.Coverage is EAdvancedMaterialCoverageMode.Transparent or EAdvancedMaterialCoverageMode.Refractive)
                continue;
            if (!snapshot.Materials.TryGet(source.Material, out AdvancedMaterialRecord material) ||
                (material.FeatureFlags & EAdvancedMaterialFeatureFlags.VertexDeformation) == 0)
                continue;
            if (!snapshot.MaterialPayloads.TryGetNativeVertex(in material, out AdvancedNativeVertexMaterial vertex) ||
                vertex.Program is null || !vertex.Inputs.IsFinite || source.Coverage != EAdvancedMaterialCoverageMode.Opaque || viewId >= 64 ||
                !snapshot.Draws.TryGetDenseIndex(source.Draw, out uint dense) ||
                !snapshot.Draws.TryGet(source.Draw, out AdvancedDrawRecord draw))
            {
                reason = "WebGPU.Advanced.NativeVertexCompanionMissing: the retained opaque material has no verified packed local-vertex producer and exact input snapshot.";
                return false;
            }
            if (index >= snapshot.Submission.DeformationSources.Length ||
                snapshot.Submission.DeformationSources[index].Renderer is not { } renderer ||
                renderer.HasSettingUniformsHandlers || renderer.HasRenderDataPreparation || renderer.DeformMeshRenderer is not null ||
                renderer.BindingPublishers.Count != 0 || renderer.Buffers.ContainsKey("Position") || renderer.Buffers.ContainsKey("Normal") ||
                renderer.Material?.HasSettingVertexUniformHandlers == true ||
                RuntimeEngine.Rendering.State.RenderingPipelineState?.HasActiveScopedBindings == true)
            {
                reason = "WebGPU.Advanced.NativeVertexSourceUnsupported: renderer callbacks, external vertex streams, mesh deformers and scoped binding overrides require an exact source-input companion.";
                return false;
            }
            AdvancedPreparedDrawDeformationRecord original = frame.PreparedRows[checked((int)dense)];
            WebGpuAdvancedNativeVertexHistory prior = dense < _nativeVertexHistory.Length ? _nativeVertexHistory[checked((int)dense)] : default;
            bool previousValid = prior.TryResolve(source, draw.Deformation, vertex, original.PreviousValid, adjacentHistory,
                _nativeVertexHistoryFrame != 0, out AdvancedNativeVertexInputs previousInputs, out EAdvancedVelocityValidityReason temporalReason);
            uint vertexBytes = checked(source.VertexCount * 64u);
            uint outputCurrent = currentBytes, outputPrevious = previousBytes;
            currentBytes = checked(currentBytes + vertexBytes);
            previousBytes = checked(previousBytes + vertexBytes);
            if (currentBytes > _renderer.MaximumAdvancedStorageBytes || previousBytes > _renderer.MaximumAdvancedStorageBytes)
            {
                reason = "WebGPU.Advanced.NativeVertexCapacity: exact current/previous material outputs exceed the selected storage binding limit.";
                return false;
            }
            EAdvancedVisibilityPayloadFlags payloadFlags = (EAdvancedVisibilityPayloadFlags)
                AdvancedReconstructionTemporalFlags.PackVelocityReason(
                    (uint)(source.Flags | EAdvancedVisibilityPayloadFlags.Skinned), temporalReason);
            payloads[index] = source with
            {
                GeometryOffsets = source.GeometryOffsets with
                {
                    VertexOffset = outputCurrent / 64,
                    PreviousVertexOffset = outputPrevious / 64,
                },
                Flags = payloadFlags,
            };
            // An arbitrary authored local function has no inferred finite envelope.
            // The original GPU strategy still expands/culls unrelated geometry.
            candidates[index] = candidates[index] with
            {
                // Shared early classification used the undeformed sphere. Its
                // geometric view bit cannot reject the authored output position.
                ViewMask = candidates[index].ViewMask | (1UL << checked((int)viewId)),
                Flags = candidates[index].Flags | EAdvancedVisibilityPreparationFlags.Uncertain |
                    EAdvancedVisibilityPreparationFlags.ConservativeVisible,
            };
            EAdvancedPreparedDrawDeformationFlags flags = EAdvancedPreparedDrawDeformationFlags.Active |
                EAdvancedPreparedDrawDeformationFlags.TemporalStatePresent;
            if (previousValid) flags |= EAdvancedPreparedDrawDeformationFlags.PreviousValid;
            flags = (EAdvancedPreparedDrawDeformationFlags)AdvancedReconstructionTemporalFlags.PackVelocityReason(
                (uint)flags, temporalReason);
            frame.PreparedRows[checked((int)dense)] = new(source.Geometry, draw.Deformation,
                outputCurrent / 64, outputPrevious / 64, source.VertexCount, flags);
            native.Add(new(index, dense, source, vertex, previousInputs, outputCurrent, outputPrevious, previousValid));
        }
        native.PrepareGeometry(snapshot, currentBytes, previousBytes);
        reason = string.Empty;
        return true;
    }

    /// <summary>Parameter history advances only after the complete native frame was accepted for submission.</summary>
    internal void EndRecording(uint sequence, bool submitted)
    {
        foreach (WebGpuAdvancedVisibilityFrame frame in _frames)
        {
            WebGpuAdvancedNativeVertexFrame native = frame.NativeVertices;
            if (native.RecordingSequence != sequence) continue;
            if (submitted && native.Executed && frame.Scene is { } scene)
            {
                int count = scene.Snapshot.Draws.PhysicalRecords.Length;
                if (_nativeVertexHistory.Length < count) Array.Resize(ref _nativeVertexHistory, count);
                Array.Clear(_nativeVertexHistory);
                for (int index = 0; index < native.Count; index++)
                {
                    ref readonly WebGpuAdvancedNativeVertexJob job = ref native.Jobs[index];
                    scene.Snapshot.Draws.TryGet(job.Source.Draw, out AdvancedDrawRecord draw);
                    _nativeVertexHistory[checked((int)job.DrawDense)] = new(job.Source.Draw, job.Source.Geometry,
                        draw.Deformation, job.Material.Program!.Identity, job.Source.VertexCount, job.Material.Inputs);
                }
                _nativeVertexHistoryFrame = native.WorldFrameId;
                _nativeVertexHistoryEpoch = scene.Snapshot.DatabaseEpoch;
            }
            native.EndRecording(submitted);
        }
    }
}
