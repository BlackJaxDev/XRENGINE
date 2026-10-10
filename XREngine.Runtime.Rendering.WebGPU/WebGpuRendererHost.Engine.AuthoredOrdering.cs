using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private readonly HashSet<int> _authoredOrderingReadTextures = new();
    private bool _authoredOrderingActive;
    private int _authoredOrderingCopyBytes;
    private uint _authoredOrderingCopyFrame;

    private EAuthoredIndexedSubmissionStatus PrepareAuthoredOrdering(in AuthoredIndexedBackendRequest request,
        WebGpuAuthoredIndexedFrameSlot slot, out WebGpuAuthoredOrderingBatch? ordering,
        out WebGpuRenderProgram? mask, out string reason)
    {
        ordering = null;
        mask = null;
        reason = string.Empty;
        if (request.View.ShadowPass) return EAuthoredIndexedSubmissionStatus.Ready;
        bool required = request.RenderPass == (int)EDefaultRenderPass.TransparentForward;
        int candidateCount = InspectAuthoredOrderingCandidates(in request, slot.Publication, null, null, ref required);
        if (!required || candidateCount == 0) return EAuthoredIndexedSubmissionStatus.Ready;
        if (request.OrderPublicationPackage is null ||
            !request.OrderPublicationPackage.TryGetFullResidentMeshOrder(request.Scene, request.FrameId,
                request.Camera, request.View, request.RenderPass, out GpuMeshSubmissionOrderPublication? publication, out reason))
        {
            if (string.IsNullOrEmpty(reason)) reason = "WebGPU.AuthoredOrdering.PublicationMissing: ordered raster requires the exact full-resident collection insertion publication.";
            return EAuthoredIndexedSubmissionStatus.Rejected;
        }
        RequireAuthoredOrderingPassReplay(in request, slot.Publication);
        EAuthoredIndexedSubmissionStatus admission = GetAuthoredIndexedAdmission(OrderingProgramBindings, _orderingProgramFailure, out reason);
        if (admission != EAuthoredIndexedSubmissionStatus.Ready) return admission;
        WebGpuRenderProgram rank = GetIndexedProgram("authored-indexed::rank-sources");
        mask = GetIndexedProgram("authored-indexed::mask-ranked-arguments");
        if (!rank.TryPrepareForCompute() || !mask.TryPrepareForCompute())
        {
            reason = "WebGPU.AuthoredOrdering.ProgramsPending: the exact source-rank companions are preparing.";
            return EAuthoredIndexedSubmissionStatus.Pending;
        }
        WebGpuAuthoredOrderingBatch candidateOrdering = slot.NextOrdering();
        _ = InspectAuthoredOrderingCandidates(in request, slot.Publication, publication, candidateOrdering, ref required);
        if (candidateCount > 256 || (long)candidateOrdering.SourceCount * candidateCount > 4096)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.ReplayCapacity: an ordered pass exceeds 256 candidates or 4096 rank-by-candidate raster records.");
        slot.RequireCandidateCapacity(candidateCount);
        RequireAuthoredOrderingCommandCapacity(checked(1 + candidateOrdering.SourceCount * candidateCount + candidateCount * 2));
        BeginAuthoredOrdering();
        try { candidateOrdering.Record(publication!, rank); }
        catch { EndAuthoredOrdering(); throw; }
        ordering = candidateOrdering;
        reason = string.Empty;
        return EAuthoredIndexedSubmissionStatus.Ready;
    }

    private static int InspectAuthoredOrderingCandidates(in AuthoredIndexedBackendRequest request,
        GpuMeshSubmissionPublication resident, GpuMeshSubmissionOrderPublication? publication,
        WebGpuAuthoredOrderingBatch? ordering, ref bool required)
    {
        int count = 0;
        ReadOnlySpan<GpuMeshSubmissionRecord> records = resident.Records;
        for (int index = 0; index < records.Length; index++)
        {
            ref readonly GpuMeshSubmissionRecord source = ref records[index];
            if (source.InstanceCount == 0 || (source.Metadata.LayerMask & request.View.View.CullingLayerMask) == 0 ||
                !resident.IncludesRenderPass(index, request.RenderPass)) continue;
            if (resident.GetSourceOwnership(source.Source) == EGpuMeshSubmissionSourceOwnership.ExplicitCpu)
            {
                // Explicit direct sources retain their CPU-selected command/LOD.
                // GPU-selected alternate candidates belong only to GPU-owned routes.
                if (IncludesIndexedPass(in source, request.RenderPass) && source.IsMaterialPassEnabled(false))
                {
                    required |= RequiresAuthoredSourceOrdering(in source);
                    count++;
                    CaptureAuthoredOrderSource(publication, ordering, source.Source);
                }
                continue;
            }
            ReadOnlySpan<GpuMeshSubmissionRecord> candidates = source.LodCount > 1
                ? resident.GetLodCandidates(index) : records.Slice(index, 1);
            foreach (ref readonly GpuMeshSubmissionRecord candidate in candidates)
            {
                if (candidate.Mesh is null) continue;
                if (candidate.IsMaterialPassEnabled(false) && IncludesIndexedPass(in candidate, request.RenderPass))
                {
                    required |= RequiresAuthoredSourceOrdering(in candidate);
                    count++;
                    CaptureAuthoredOrderSource(publication, ordering, candidate.Source);
                }
                if (candidate.TryGetOutlineCandidate(out GpuMeshSubmissionRecord outline) && IncludesIndexedPass(in outline, request.RenderPass))
                {
                    required |= RequiresAuthoredSourceOrdering(in outline);
                    count++;
                    if (publication is not null && candidate.MaterialOutlineCommand is null)
                        throw new NotSupportedException("WebGPU.AuthoredOrdering.OutlineSourceMissing: an auxiliary candidate has no exact collection command identity.");
                    CaptureAuthoredOrderSource(publication, ordering, candidate.MaterialOutlineCommand ?? candidate.Source);
                }
            }
        }
        return count;
    }

    private static void CaptureAuthoredOrderSource(GpuMeshSubmissionOrderPublication? publication,
        WebGpuAuthoredOrderingBatch? ordering, IRenderCommandMesh source)
    {
        if (publication is null || ordering is null) return;
        if (!publication.TryGetSource(source, out GpuMeshSubmissionOrderSource entry))
            throw new NotSupportedException("WebGPU.AuthoredOrdering.SourceMissing: every resident candidate requires its exact full-resident collection insertion token before any GPU recording.");
        ordering.AddSource(in entry);
    }

    private static bool RequiresAuthoredSourceOrdering(in GpuMeshSubmissionRecord record)
        => (record.Metadata.Flags & (uint)GPUIndirectRenderFlags.Transparent) != 0 || record.Material.IsTransparentLike() ||
           record.RenderOptionsOverride?.BlendModeAllDrawBuffers?.Enabled == ERenderParamUsage.Enabled ||
           record.RenderOptionsOverride?.BlendModesPerDrawBuffer is { Count: > 0 };

    private static void RequireAuthoredOrderingPassReplay(in AuthoredIndexedBackendRequest request,
        GpuMeshSubmissionPublication resident)
    {
        BackendReadyFramePackage package = request.OrderPublicationPackage!;
        if (!package.TryGetPass(request.RenderPass, out BackendReadyRenderPass pass)) return;
        for (int index = 0; index < pass.CommandCount; index++)
        {
            RenderCommand command = package.GetPassCommand(in pass, index);
            if (command is not IRenderCommandMesh source)
            {
                if (request.CpuReplayPolicy == EAuthoredIndexedCpuReplayPolicy.MeshesAndNonMesh)
                    throw new NotSupportedException("WebGPU.AuthoredOrdering.MixedCommandOrder: non-mesh commands require a shared interleaved replay producer in an ordered GPU pass.");
                continue;
            }
            if (resident.IsGpuOwnedMaterialAuxiliary(source)) continue;
            EGpuMeshSubmissionSourceOwnership ownership = resident.GetSourceOwnership(source);
            if (ownership == EGpuMeshSubmissionSourceOwnership.Missing)
            {
                bool hasRecord = false, nonempty = false;
                foreach (ref readonly GpuMeshSubmissionRecord record in resident.Records)
                    if (ReferenceEquals(record.Source, source)) { hasRecord = true; nonempty |= record.InstanceCount != 0; }
                if (hasRecord && !nonempty) continue;
                throw new NotSupportedException("WebGPU.AuthoredOrdering.SourceOwnershipMissing: a collected source has no frozen resident route ownership; dropping or rerouting it is forbidden.");
            }
            if (ownership is EGpuMeshSubmissionSourceOwnership.MixedExplicitOwnership or EGpuMeshSubmissionSourceOwnership.IncompleteSource)
                throw new NotSupportedException("WebGPU.AuthoredOrdering.SourceOwnershipIncomplete: ordered replay requires a complete single-route source publication.");
            if (ownership == EGpuMeshSubmissionSourceOwnership.ExplicitCpu && request.CpuReplay is null)
                throw new NotSupportedException("WebGPU.AuthoredOrdering.CpuReplayMissing: explicitly CPU-owned sources require their original collected-command replay producer.");
        }
    }

    internal void ReserveAuthoredOrderingCopyBytes(int bytes)
    {
        if (!_authoredOrderingActive || bytes <= 0 || bytes > 8 * 1024 * 1024 - _authoredOrderingCopyBytes)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.CopyCapacity: ordered raster exceeds the atomic frame's 8 MiB candidate-owned GPU input snapshot budget.");
        SetField(ref _authoredOrderingCopyBytes, _authoredOrderingCopyBytes + bytes, publishNotifications: false);
    }

    internal void RequireAuthoredOrderingCommandCapacity(int additional)
    {
        if (additional < 0 || additional > EngineFrameMaximumRecords - _engineCommandCount)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.CommandCapacity: source ranking, input copies and rank-major replay exceed the remaining atomic frame command capacity.");
    }

    internal void RetainAuthoredOrderingTexture(AbstractRenderAPIObject texture)
    {
        if (!_authoredOrderingActive) throw new InvalidOperationException("Ordered sampled images require an active capture.");
        int handle = PhysicalTextureHandle(texture);
        if (handle == 0) throw new NotSupportedException("WebGPU.AuthoredOrdering.TextureOwner: a sampled image has no retained physical texture.");
        _authoredOrderingReadTextures.Add(handle);
        WebGpuTextureResource.MarkRecorded(texture);
    }

    internal void RequireAuthoredOrderingTextureWritable(AbstractRenderAPIObject texture)
    {
        if (_authoredOrderingActive && _authoredOrderingReadTextures.Contains(PhysicalTextureHandle(texture)))
            throw new NotSupportedException("WebGPU.AuthoredOrdering.TextureWriteConflict: a GPU producer would overwrite a sampled image retained by deferred ordered raster.");
    }

    private static int PhysicalTextureHandle(AbstractRenderAPIObject texture)
        => texture switch
        {
            WebGpuTexture2D value => value.ResourceHandle,
            WebGpuTexture2DArray value => value.ResourceHandle,
            WebGpuTextureCube value => value.ResourceHandle,
            WebGpuTextureView value => value.Resource.Handle,
            _ => 0,
        };

    private void BeginAuthoredOrdering()
    {
        if (_authoredOrderingActive)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.NestedSubmission: ordered authored submissions cannot recursively enter one another.");
        SetField(ref _authoredOrderingActive, true, publishNotifications: false);
        if (_authoredOrderingCopyFrame != _engineFrameSequence)
        {
            SetField(ref _authoredOrderingCopyBytes, 0, publishNotifications: false);
            SetField(ref _authoredOrderingCopyFrame, _engineFrameSequence, publishNotifications: false);
        }
        _authoredOrderingReadTextures.Clear();
    }

    private void EndAuthoredOrdering()
    {
        _authoredOrderingReadTextures.Clear();
        SetField(ref _authoredOrderingActive, false, publishNotifications: false);
    }
}
