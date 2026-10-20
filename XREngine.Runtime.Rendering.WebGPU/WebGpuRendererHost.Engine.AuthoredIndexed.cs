using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost : IAuthoredIndexedBackendCapability
{
    private static readonly string[] MeshletProgramBindings = ["meshlets::cull-expand", "meshlets::finalize-indexed", "meshlets::refit-bounds", "meshlets::select-lod"];
    private static readonly string[] IndirectProgramBindings = ["indirect::cull-primitive", "meshlets::select-lod"];
    private static readonly string[] OrderingProgramBindings = ["authored-indexed::rank-sources", "authored-indexed::mask-ranked-arguments"];
    private readonly Dictionary<string, XRRenderProgram> _authoredIndexedPrograms = new(StringComparer.Ordinal);
    private readonly Dictionary<MeshletPayload, WebGpuMeshletGeometry> _meshletGeometry = new(ReferenceEqualityComparer.Instance);
    private WebGpuAuthoredIndexedFrameSlot[]? _authoredIndexedSlots;
    private WebPipelineArtifactCatalog? _authoredIndexedArtifacts;
    private string? _meshletProgramFailure = "WebGPU.AuthoredIndexed.CatalogMissing: the owning package has no installed compute meshlet companions.";
    private string? _indirectProgramFailure = "WebGPU.Indirect.CatalogMissing: the owning package has no installed whole-primitive culling companion.";
    private string? _orderingProgramFailure = "WebGPU.AuthoredOrdering.CatalogMissing: the owning package has no installed source-order companions.";
    private EMeshSubmissionStrategy _lastIndexedSubmissionStrategy;
    private string _lastIndexedSubmissionReason = "NeverSubmitted";
    private int _lastIndexedUnboundedDraws;

    /// <summary>CPU submission diagnostics only. GPU visibility/count buffers are never mapped for these values.</summary>
    public EMeshSubmissionStrategy LastAuthoredIndexedSubmissionStrategy => _lastIndexedSubmissionStrategy;
    public string LastAuthoredIndexedSubmissionReason => _lastIndexedSubmissionReason;
    public int LastAuthoredIndexedConservativeUnboundedDraws => _lastIndexedUnboundedDraws;

    /// <summary>Installs scoped, verified companions without changing hardware task/mesh shader capability.</summary>
    public void BindAuthoredIndexedPipelineArtifacts(WebPipelineArtifactCatalog? artifacts)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording || _authoredIndexedPrograms.Count != 0)
            throw new InvalidOperationException("WebGPU.AuthoredIndexed.CatalogActive: install companions before recording engine authored indexed work.");
        SetField(ref _authoredIndexedArtifacts, artifacts, publishNotifications: false);
        SetField(ref _meshletProgramFailure, ValidateIndexedPrograms(artifacts, MeshletProgramBindings), publishNotifications: false);
        SetField(ref _indirectProgramFailure, ValidateIndexedPrograms(artifacts, IndirectProgramBindings), publishNotifications: false);
        SetField(ref _orderingProgramFailure, ValidateIndexedPrograms(artifacts, OrderingProgramBindings), publishNotifications: false);
    }

    private static string? ValidateIndexedPrograms(WebPipelineArtifactCatalog? artifacts, string[] bindings)
    {
        foreach (string binding in bindings)
        {
            if (artifacts is null || !artifacts.TryResolve(binding, out ShaderProgramArtifact? artifact) || artifact is null)
                return $"WebGPU.AuthoredIndexed.ProgramMissing: the package requires '{binding}'.";
            if (binding.StartsWith("authored-indexed::", StringComparison.Ordinal))
                WebGpuAuthoredOrderingProgramContract.Validate(artifact, binding["authored-indexed::".Length..]);
            else if (binding == "indirect::cull-primitive") WebGpuIndirectProgramContract.Validate(artifact);
            else WebGpuMeshletProgramContract.Validate(artifact, binding["meshlets::".Length..]);
        }
        return null;
    }

    public EAuthoredIndexedSubmissionStatus GetMeshletIndexedAdmission(out string reason)
        => GetAuthoredIndexedAdmission(MeshletProgramBindings, _meshletProgramFailure, out reason);

    public EAuthoredIndexedSubmissionStatus GetIndirectIndexedAdmission(out string reason)
        => GetAuthoredIndexedAdmission(IndirectProgramBindings, _indirectProgramFailure, out reason);

    private EAuthoredIndexedSubmissionStatus GetAuthoredIndexedAdmission(string[] bindings, string? failure, out string reason)
    {
        if (State != BrowserRendererState.Ready)
        {
            reason = "WebGPU.AuthoredIndexed.DevicePending: indexed GPU submission requires the ready owning device.";
            return State == BrowserRendererState.Pending ? EAuthoredIndexedSubmissionStatus.Pending : EAuthoredIndexedSubmissionStatus.Rejected;
        }
        if (failure is not null) { reason = failure; return EAuthoredIndexedSubmissionStatus.Rejected; }
        foreach (string binding in bindings)
        {
            if (_authoredIndexedArtifacts is null || !_authoredIndexedArtifacts.TryResolve(binding, out ShaderProgramArtifact? artifact) || artifact is null)
                throw new InvalidOperationException("The installed authored indexed catalog changed after validation.");
            foreach ((string limit, int required) in artifact.RequiredLimits)
                if (DeviceCapabilities?.Limits.TryGetValue(limit, out long available) != true || available < required)
                {
                    reason = $"WebGPU.AuthoredIndexed.DeviceLimit: '{binding}' requires {limit}>={required}.";
                    return EAuthoredIndexedSubmissionStatus.Rejected;
                }
        }
        reason = "Ready";
        return EAuthoredIndexedSubmissionStatus.Ready;
    }

    public EAuthoredIndexedSubmissionStatus EnqueueAuthoredIndexed(in AuthoredIndexedBackendRequest request, out string reason)
    {
        SetField(ref _lastIndexedSubmissionStrategy, request.SubmissionStrategy, publishNotifications: false);
        try { return EnqueueAuthoredIndexedCore(in request, out reason); }
        catch (RenderResourcePreparationPendingException error)
        {
            reason = error.Message;
            return IndexedResult(EAuthoredIndexedSubmissionStatus.Pending, reason);
        }
        catch (NotSupportedException error)
        {
            reason = error.Message;
            return IndexedResult(EAuthoredIndexedSubmissionStatus.Rejected, reason);
        }
    }

    private EAuthoredIndexedSubmissionStatus EnqueueAuthoredIndexedCore(in AuthoredIndexedBackendRequest request, out string reason)
    {
        bool meshlets = request.SubmissionStrategy.IsAnyMeshletStrategy();
        EAuthoredIndexedSubmissionStatus admission = meshlets ? GetMeshletIndexedAdmission(out reason) : GetIndirectIndexedAdmission(out reason);
        if (admission != EAuthoredIndexedSubmissionStatus.Ready) return IndexedResult(admission, reason);
        if (!_engineRecording || request.Scene is null || request.Camera is null || request.FrameId == 0)
            return IndexedRejected("FrameRequired", "an exact published world, camera and active engine frame are required", out reason);
        if (request.View.View.SourceCameraIdentity != request.Camera.RenderIdentity)
            return IndexedRejected("ViewIdentity", "the frozen view must belong to the exact requested camera", out reason);
        if (request.View.View.IsXrSubmittedView)
            return IndexedRejected("ViewProfile", "stereo indexed views need their explicit authored raster profiles", out reason);
        if (request.SubmissionStrategy is not (EMeshSubmissionStrategy.GpuMeshletZeroReadback or EMeshSubmissionStrategy.GpuMeshletInstrumented or
            EMeshSubmissionStrategy.GpuIndirectZeroReadback or EMeshSubmissionStrategy.GpuIndirectInstrumented))
            return IndexedRejected("Strategy", "authored indexed submission requires an explicit indirect or compute-meshlet mode", out reason);
        if (!TryAcquireAuthoredIndexedSlot(in request, out WebGpuAuthoredIndexedFrameSlot? slot, out reason))
            return IndexedResult(EAuthoredIndexedSubmissionStatus.Pending, reason);
        using var frozenView = PushFrozenView(request.View);
        WebGpuRenderProgram cull = GetIndexedProgram(meshlets ? "meshlets::cull-expand" : "indirect::cull-primitive");
        WebGpuRenderProgram? finalize = meshlets ? GetIndexedProgram("meshlets::finalize-indexed") : null;
        WebGpuRenderProgram? refit = meshlets ? GetIndexedProgram("meshlets::refit-bounds") : null;
        WebGpuRenderProgram selectLod = GetIndexedProgram("meshlets::select-lod");
        bool programsReady = cull.TryPrepareForCompute();
        if (finalize is not null) programsReady &= finalize.TryPrepareForCompute();
        if (refit is not null) programsReady &= refit.TryPrepareForCompute();
        programsReady &= selectLod.TryPrepareForCompute();
        if (!programsReady)
        {
            reason = "WebGPU.AuthoredIndexed.ProgramsPending: the exact compute companions are preparing.";
            return IndexedResult(EAuthoredIndexedSubmissionStatus.Pending, reason);
        }
        ReadOnlySpan<GpuMeshSubmissionRecord> records = slot!.Publication.Records;
        bool shadowPass = request.View.ShadowPass;
        if (!shadowPass && slot.Publication.RequiresLodAuxiliaryPassPublication())
            return IndexedRejected("LodAuxiliaryPassPublication", "authored LOD outline geometry requires a GPU-selected auxiliary pass publication", out reason);
        if (slot.Publication.TryGetInvalidSourceOwnership(request.RenderPass, out EGpuMeshSubmissionSourceOwnership invalidOwnership))
            return invalidOwnership == EGpuMeshSubmissionSourceOwnership.MixedExplicitOwnership
                ? IndexedRejected("MixedExplicitOwnership", "one selected source mixes CPU-exempt and GPU-owned primitives; exact primitive replay is required", out reason)
                : IndexedRejected("IncompleteSource", "the selected resident publication omits an authored primitive; exact primitive publication is required", out reason);
        EAuthoredIndexedSubmissionStatus orderStatus = PrepareAuthoredOrdering(in request, slot,
            out WebGpuAuthoredOrderingBatch? ordering, out WebGpuRenderProgram? orderMask, out reason);
        if (orderStatus != EAuthoredIndexedSubmissionStatus.Ready) return IndexedResult(orderStatus, reason);
        try
        {
            if (ordering is null)
            {
                request.CpuReplay?.ReplayUnorderedCpuExempt(request.RenderPass,
                    request.CpuReplayPolicy == EAuthoredIndexedCpuReplayPolicy.MeshesAndNonMesh);
                if (_engineDrawPending)
                {
                    reason = "WebGPU.AuthoredIndexed.CpuReplayPending: an explicitly CPU-owned candidate is still preparing its exact raster resources.";
                    return IndexedResult(EAuthoredIndexedSubmissionStatus.Pending, reason);
                }
            }
            for (int index = 0; index < records.Length; index++)
            {
                ref readonly GpuMeshSubmissionRecord record = ref records[index];
                if (record.InstanceCount == 0 || (record.Metadata.LayerMask & request.View.View.CullingLayerMask) == 0) continue;
                if (!slot.Publication.IncludesRenderPass(index, request.RenderPass) ||
                    slot.Publication.GetSourceOwnership(record.Source) == EGpuMeshSubmissionSourceOwnership.ExplicitCpu) continue;
                if (record.AuthoredPrimitiveInstanceCount != 1)
                    return IndexedRejected("PrimitiveInstanceCompositionUnavailable", "renderer-local indirect instance counts require their own exact primitive submission profile", out reason);
                int authoredLodCount = slot.Publication.GetAuthoredLodCount(index);
                if (authoredLodCount > GPUScene.MaxLogicalMeshLodCount || record.LodCount > GPUScene.MaxLogicalMeshLodCount)
                    return IndexedRejected("LodCapacity", "authored LOD sets beyond four levels require a wider shared selection table", out reason);
                if (authoredLodCount != Math.Max(1u, record.LodCount))
                    return IndexedRejected("LodPublicationChanged", "the authored LOD owners do not match the shared resident table", out reason);
                ReadOnlySpan<GpuMeshSubmissionRecord> candidates = record.LodCount > 1
                    ? slot.Publication.GetLodCandidates(index) : records.Slice(index, 1);
                int residentCandidates = 0;
                for (int level = 0; level < candidates.Length; level++)
                {
                    uint expectedMesh = record.LodCount > 1 ? record.LodMetadata.GetMeshDataId(level) : record.Metadata.MeshID;
                    if (expectedMesh == 0) continue;
                    ref readonly GpuMeshSubmissionRecord candidate = ref candidates[level];
                    if (candidate.Mesh is null || candidate.Metadata.MeshID != expectedMesh ||
                        record.LodCount > 1 && candidate.Metadata.LodPolicy != (uint)level)
                        return IndexedRejected("LodCandidateMissing", "a resident LOD has no exact frozen renderer, material and geometry owner", out reason);
                    if (candidate.AuthoredPrimitiveInstanceCount != 1)
                        return IndexedRejected("PrimitiveInstanceCompositionUnavailable", "an authored LOD uses renderer-local indirect instance counts outside the command-count profile", out reason);
                    if (candidate.SourcePrimitiveCount != record.SourcePrimitiveCount)
                        return IndexedRejected("LodPrimitiveMembership", "LOD renderers with different primitive membership require a complete per-level source publication", out reason);
                    bool baseRequested = candidate.IsMaterialPassEnabled(shadowPass) && IncludesIndexedPass(in candidate, request.RenderPass);
                    bool outlineRequested = !shadowPass && candidate.TryGetOutlineCandidate(out GpuMeshSubmissionRecord outlineCandidate) &&
                        IncludesIndexedPass(in outlineCandidate, request.RenderPass);
                    if (!baseRequested && !outlineRequested) continue;
                    if (candidate.RequiresLodTransformPublication)
                        return IndexedRejected("LodTransformProfile", "mixed static and skinned LODs require proven default source matrices and their distinct frozen transform conventions", out reason);
                    if ((candidate.Metadata.Flags & (uint)GPUIndirectRenderFlags.CpuFallbackOnly) != 0)
                        return IndexedRejected("LodOwnershipMismatch", "a GPU-selected source includes a CPU-exempt authored LOD", out reason);
                    if (baseRequested)
                    {
                        if (!ValidateIndexedCandidate(in candidate, meshlets, out reason)) return IndexedResult(EAuthoredIndexedSubmissionStatus.Rejected, reason);
                        residentCandidates++;
                    }
                    if (outlineRequested)
                    {
                        candidate.TryGetOutlineCandidate(out outlineCandidate);
                        if (outlineCandidate.RenderOptionsOverride?.ExcludeFromGpuIndirect == true)
                            return IndexedRejected("OutlineExplicitCpuOwnership", "an explicitly CPU-owned outline needs an exact selected-geometry replay producer", out reason);
                        if (!ValidateIndexedCandidate(in outlineCandidate, meshlets, out reason)) return IndexedResult(EAuthoredIndexedSubmissionStatus.Rejected, reason);
                        residentCandidates++;
                    }
                }
                if (residentCandidates == 0) continue;
                slot.RequireCandidateCapacity(residentCandidates);
                WebGpuAuthoredIndexedLodSelection selection = slot.NextLodSelection();
                selection.Record(in record, request.Camera, selectLod);
                for (int level = 0; level < candidates.Length; level++)
                {
                    ref readonly GpuMeshSubmissionRecord candidate = ref candidates[level];
                    if (candidate.Mesh is null) continue;
                    if (candidate.IsMaterialPassEnabled(shadowPass) && IncludesIndexedPass(in candidate, request.RenderPass) &&
                        !TryRecordIndexedCandidate(in candidate, slot, selection, request.Camera, cull, finalize, refit,
                            ordering, candidate.Source, orderMask, out reason))
                        return IndexedResult(EAuthoredIndexedSubmissionStatus.Pending, reason);
                    if (!shadowPass && candidate.TryGetOutlineCandidate(out GpuMeshSubmissionRecord outlineCandidate) &&
                        IncludesIndexedPass(in outlineCandidate, request.RenderPass) &&
                        !TryRecordIndexedCandidate(in outlineCandidate, slot, selection, request.Camera, cull, finalize, refit,
                            ordering, candidate.MaterialOutlineCommand ?? candidate.Source, orderMask, out reason))
                        return IndexedResult(EAuthoredIndexedSubmissionStatus.Pending, reason);
                }
            }
            if (ordering is not null)
            {
                CaptureAuthoredDirectSources(in request, slot, ordering);
                if (_engineDrawPending)
                {
                    reason = "WebGPU.AuthoredOrdering.DirectRasterPending: an original direct source is still preparing its exact rank-gated raster.";
                    return IndexedResult(EAuthoredIndexedSubmissionStatus.Pending, reason);
                }
                ordering.RecordRaster();
            }
        }
        finally { if (ordering is not null) EndAuthoredOrdering(); }
        reason = _lastIndexedUnboundedDraws == 0 ? "Ready" : "Ready; undeclared vertex bounds conservatively disable geometric rejection";
        return IndexedResult(EAuthoredIndexedSubmissionStatus.Ready, reason);
    }

    private bool TryRecordIndexedCandidate(in GpuMeshSubmissionRecord candidate, WebGpuAuthoredIndexedFrameSlot slot,
        WebGpuAuthoredIndexedLodSelection selection, XRCamera camera, WebGpuRenderProgram cull, WebGpuRenderProgram? finalize,
        WebGpuRenderProgram? refit, WebGpuAuthoredOrderingBatch? ordering, IRenderCommandMesh orderSource,
        WebGpuRenderProgram? orderMask, out string reason)
    {
        WebGpuAuthoredIndexedDrawRequest drawRequest;
        if (finalize is not null && refit is not null)
        {
            WebGpuMeshletGeometry geometry = GetMeshletGeometry(candidate.Mesh, candidate.MeshletPayload!);
            WebGpuMeshletWork work = slot.NextMeshletWork();
            work.SetLodSelection(selection);
            drawRequest = new(candidate, camera, cull, work, geometry, finalize, refit, null, selection);
        }
        else
        {
            if (candidate.Mesh.Type != EPrimitiveType.Triangles || !candidate.Mesh.HasIndexData(EPrimitiveType.Triangles))
                throw new NotSupportedException("WebGPU.Indirect.IndexedTrianglesRequired: authored primitive submission requires an original triangle index stream.");
            if (candidate.SourceBindings.IndexBuffer is null)
            {
                if (candidate.Mesh.TryGetIndexBufferBuildFailure(EPrimitiveType.Triangles, out Exception? failure))
                    throw new NotSupportedException("WebGPU.Indirect.IndexPreparationFailed: original triangle index preparation failed.", failure);
                _ = candidate.Mesh.GetIndexBuffer(EPrimitiveType.Triangles, out _);
                reason = "WebGPU.Indirect.IndexPublicationPending: the prepared original index stream must cross the resident publication boundary.";
                return false;
            }
            drawRequest = new(candidate, camera, cull, null, null, null, null, slot.NextIndirectWork(), selection);
        }
        if (ordering is not null)
            drawRequest = drawRequest with { Ordering = ordering, OrderSourceIndex = ordering.FindSource(orderSource), OrderMask = orderMask };
        WebGpuMeshRenderer renderer = (WebGpuMeshRenderer)GetOrCreateAPIRenderObject(candidate.Renderer.GetDefaultVersion())!;
        int drawCountBeforeCallbacks = _engineMeshDrawCount;
        if (!renderer.TryRenderAuthoredIndexed(in drawRequest, out bool unbounded))
        {
            reason = "WebGPU.AuthoredIndexed.AuthoredRasterPending: the original material, bindings, deformation or indexed pipeline is preparing.";
            return false;
        }
        if (ordering is not null && _engineMeshDrawCount != drawCountBeforeCallbacks)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.NestedRasterCallback: candidate callbacks cannot issue independent raster draws during deferred source ordering.");
        if (unbounded) SetField(ref _lastIndexedUnboundedDraws, _lastIndexedUnboundedDraws + 1, publishNotifications: false);
        reason = string.Empty;
        return true;
    }

    private bool ValidateIndexedCandidate(in GpuMeshSubmissionRecord record, bool meshlets, out string reason)
    {
        if (meshlets && !record.HasValidatedMeshletPayload)
        { IndexedRejected("PayloadUnavailable", "every requested GPU mesh needs its validated immutable cooked meshlet payload", out reason); return false; }
        if (record.Material.IsDestroyed || record.GeometryRevision != record.Mesh.GeometryRevision ||
            meshlets && (record.MeshletPayload!.ValidationRevision != record.PayloadValidationRevision ||
                record.MeshletPayload.OwnerValidationToken != record.PayloadOwnerValidationToken) || !record.SourceBindings.AreSourceBindingsCurrent ||
            !record.SourceBindings.ArePublisherGenerationsCurrent)
        { IndexedRejected("PublicationChanged", "source geometry or bindings changed after the resident publication boundary", out reason); return false; }
        if (!meshlets && record.SourceBindings.IndexBuffer is not null)
        {
            if (!record.SourceBindings.HasValidIndexLayout)
            { IndexedRejected("IndexLayoutUnsupported", "the original index buffer must match its captured uint16 or uint32 scalar encoding and byte extent", out reason); return false; }
            if (!record.SourceBindings.AreIndexBindingsCurrent)
            { IndexedRejected("IndexPublicationChanged", "original index ownership or contents changed after the resident publication boundary", out reason); return false; }
        }
        if (record.BillboardMode != EMeshBillboardMode.None)
        { IndexedRejected("BillboardProfile", "billboarded draws need the same published vertex transform as their authored raster stage", out reason); return false; }
        if ((record.Metadata.Flags & (uint)GPUIndirectRenderFlags.Transparent) != 0 &&
            !RequireFrozenView().ShadowPass && !_authoredOrderingActive)
        { IndexedRejected("TransparentOrderUnavailable", "view-dependent transparent source ordering has no shared GPU sort publication", out reason); return false; }
        reason = string.Empty;
        return true;
    }

    private static bool IncludesIndexedPass(in GpuMeshSubmissionRecord record, int renderPass)
        => renderPass < 0 || record.RenderPass < 0 || record.RenderPass == renderPass;

    private WebGpuMeshletGeometry GetMeshletGeometry(XRMesh mesh, MeshletPayload payload)
    {
        if (_meshletGeometry.TryGetValue(payload, out WebGpuMeshletGeometry? geometry))
        {
            if (!geometry.Matches(mesh, payload))
                throw new NotSupportedException("WebGPU.AuthoredIndexed.GeometryGenerationChanged: the retained payload no longer matches its frozen mesh owner.");
            geometry.Prepare();
            return geometry;
        }
        if (_meshletGeometry.Count >= 1024)
        {
            foreach ((MeshletPayload key, WebGpuMeshletGeometry candidate) in _meshletGeometry)
                if (candidate.LeaseCount == 0)
                { _meshletGeometry.Remove(key); candidate.Dispose(); break; }
            if (_meshletGeometry.Count >= 1024)
                throw new NotSupportedException("WebGPU.AuthoredIndexed.GeometryCapacity: all 1024 immutable meshlet generations remain retained by queued raster work.");
        }
        geometry = new(this, mesh, payload);
        _meshletGeometry.Add(payload, geometry);
        geometry.Prepare();
        return geometry;
    }

    private WebGpuRenderProgram GetIndexedProgram(string binding)
    {
        if (!_authoredIndexedPrograms.TryGetValue(binding, out XRRenderProgram? program))
        {
            if (_authoredIndexedArtifacts is null || !_authoredIndexedArtifacts.TryResolve(binding, out ShaderProgramArtifact? artifact) || artifact is null)
                throw new InvalidOperationException("The installed authored indexed program is unavailable.");
            using IDisposable publication = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            program = new XRRenderProgram(false, false, Array.Empty<XRShader>())
            { Name = "Authored indexed " + binding, CookedArtifact = artifact };
            _authoredIndexedPrograms.Add(binding, program);
        }
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        api.Generate();
        return api;
    }

    private bool TryAcquireAuthoredIndexedSlot(in AuthoredIndexedBackendRequest request, out WebGpuAuthoredIndexedFrameSlot? slot, out string reason)
    {
        slot = null;
        if (_authoredIndexedSlots is null)
        {
            SetField(ref _authoredIndexedSlots, [new(this), new(this), new(this)], publishNotifications: false);
            ReclaimAuthoredIndexedSlots();
        }
        foreach (WebGpuAuthoredIndexedFrameSlot candidate in _authoredIndexedSlots!)
            if (candidate.RecordingSequence == _engineFrameSequence && ReferenceEquals(candidate.Scene, request.Scene))
            {
                if (candidate.Publication.FrameId != request.FrameId)
                    throw new InvalidOperationException("WebGPU.AuthoredIndexed.PublicationMismatch: one atomic frame cannot mix world publication identities.");
                slot = candidate;
                reason = string.Empty;
                return true;
            }
        int recorded = 0;
        foreach (WebGpuAuthoredIndexedFrameSlot candidate in _authoredIndexedSlots!)
        {
            if (candidate.RecordingSequence == _engineFrameSequence) recorded++;
            if (!candidate.IsAvailable) continue;
            if (!request.Scene.TryAcquireMeshSubmissionPublication(out GpuMeshSubmissionPublicationLease lease)) break;
            if (lease.Publication.FrameId != request.FrameId)
            {
                lease.Dispose();
                reason = "WebGPU.AuthoredIndexed.PublicationPending: the resident source closure does not yet match this world frame.";
                return false;
            }
            candidate.Begin(request.Scene, lease, _engineFrameSequence);
            slot = candidate;
            reason = string.Empty;
            return true;
        }
        if (recorded == _authoredIndexedSlots!.Length)
            throw new NotSupportedException("WebGPU.AuthoredIndexed.PublicationCapacity: an atomic frame exceeds three independent resident scene publications.");
        reason = "WebGPU.AuthoredIndexed.SlotPending: a complete resident publication and completion-reclaimed indexed slot are required.";
        return false;
    }

    private EAuthoredIndexedSubmissionStatus IndexedResult(EAuthoredIndexedSubmissionStatus status, string reason)
    {
        SetField(ref _lastIndexedSubmissionReason, reason, publishNotifications: false);
        if (status != EAuthoredIndexedSubmissionStatus.Ready) MarkEngineDrawPending(reason);
        return status;
    }

    private EAuthoredIndexedSubmissionStatus IndexedRejected(string code, string detail, out string reason)
    {
        reason = $"WebGPU.AuthoredIndexed.{code}: {detail}.";
        return IndexedResult(EAuthoredIndexedSubmissionStatus.Rejected, reason);
    }

    private void ReclaimAuthoredIndexedSlots()
    {
        SetField(ref _lastIndexedUnboundedDraws, 0, publishNotifications: false);
        if (_authoredIndexedSlots is null) return;
        foreach (WebGpuAuthoredIndexedFrameSlot slot in _authoredIndexedSlots) slot.Reclaim(_engineCompletedSequence);
    }

    private void EndAuthoredIndexedRecording(bool submitted)
    {
        if (_authoredIndexedSlots is null) return;
        foreach (WebGpuAuthoredIndexedFrameSlot slot in _authoredIndexedSlots) slot.EndRecording(_engineFrameSequence, submitted);
    }

    private void DestroyAuthoredIndexedPrograms()
    {
        foreach (XRRenderProgram program in _authoredIndexedPrograms.Values) program.Destroy(now: true);
        _authoredIndexedPrograms.Clear();
    }

    /// <summary>Called after physical device disposal; publication pins remain valid for every accepted queue prefix.</summary>
    private void DisposeAuthoredIndexedResources()
    {
        if (_authoredIndexedSlots is not null)
            foreach (WebGpuAuthoredIndexedFrameSlot slot in _authoredIndexedSlots) slot.Dispose();
        SetField(ref _authoredIndexedSlots, null, publishNotifications: false);
        foreach (WebGpuMeshletGeometry geometry in _meshletGeometry.Values) geometry.Dispose();
        _meshletGeometry.Clear();
        SetField(ref _authoredIndexedArtifacts, null, publishNotifications: false);
    }
}
