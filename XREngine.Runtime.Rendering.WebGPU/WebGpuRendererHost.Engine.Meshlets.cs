using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost : IMeshletIndexedBackendCapability
{
    private static readonly string[] MeshletProgramPasses = ["cull-expand", "finalize-indexed", "refit-bounds"];
    private static readonly string[] MeshletProgramBindings = ["meshlets::cull-expand", "meshlets::finalize-indexed", "meshlets::refit-bounds"];
    private readonly Dictionary<string, XRRenderProgram> _meshletPrograms = new(StringComparer.Ordinal);
    private readonly Dictionary<MeshletPayload, WebGpuMeshletGeometry> _meshletGeometry = new(ReferenceEqualityComparer.Instance);
    private WebGpuMeshletFrameSlot[]? _meshletSlots;
    private WebPipelineArtifactCatalog? _meshletArtifacts;
    private string? _meshletProgramFailure = "WebGPU.Meshlets.CatalogMissing: the owning package has no installed compute meshlet companions.";
    private string _lastMeshletSubmissionReason = "NeverSubmitted";
    private int _lastMeshletUnboundedDraws;

    /// <summary>CPU submission diagnostics only. GPU visibility/count buffers are never mapped for these values.</summary>
    public string LastMeshletSubmissionReason => _lastMeshletSubmissionReason;
    public int LastMeshletConservativeUnboundedDraws => _lastMeshletUnboundedDraws;

    /// <summary>Installs scoped, verified companions without changing hardware task/mesh shader capability.</summary>
    public void BindMeshletPipelineArtifacts(WebPipelineArtifactCatalog? artifacts)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording || _meshletPrograms.Count != 0)
            throw new InvalidOperationException("WebGPU.Meshlets.CatalogActive: install companions before recording engine meshlet work.");
        string? failure = null;
        for (int index = 0; index < MeshletProgramPasses.Length; index++)
        {
            string pass = MeshletProgramPasses[index];
            if (artifacts is null || !artifacts.TryResolve(MeshletProgramBindings[index], out ShaderProgramArtifact? artifact) || artifact is null)
            {
                failure = $"WebGPU.Meshlets.ProgramMissing: the package requires 'meshlets::{pass}'.";
                break;
            }
            WebGpuMeshletProgramContract.Validate(artifact, pass);
        }
        SetField(ref _meshletArtifacts, artifacts, publishNotifications: false);
        SetField(ref _meshletProgramFailure, failure, publishNotifications: false);
    }

    public EMeshletSubmissionStatus GetMeshletIndexedAdmission(out string reason)
    {
        if (State != BrowserRendererState.Ready)
        {
            reason = "WebGPU.Meshlets.DevicePending: compute meshlet submission requires the ready owning device.";
            return State == BrowserRendererState.Pending ? EMeshletSubmissionStatus.Pending : EMeshletSubmissionStatus.Rejected;
        }
        if (_meshletProgramFailure is { } failure)
        { reason = failure; return EMeshletSubmissionStatus.Rejected; }
        for (int index = 0; index < MeshletProgramPasses.Length; index++)
        {
            string pass = MeshletProgramPasses[index];
            if (_meshletArtifacts is null || !_meshletArtifacts.TryResolve(MeshletProgramBindings[index], out ShaderProgramArtifact? artifact) || artifact is null)
                throw new InvalidOperationException("The installed meshlet catalog changed after validation.");
            foreach ((string limit, int required) in artifact.RequiredLimits)
                if (DeviceCapabilities?.Limits.TryGetValue(limit, out long available) != true || available < required)
                {
                    reason = $"WebGPU.Meshlets.DeviceLimit: '{pass}' requires {limit}>={required}.";
                    return EMeshletSubmissionStatus.Rejected;
                }
        }
        reason = "Ready";
        return EMeshletSubmissionStatus.Ready;
    }

    public EMeshletSubmissionStatus EnqueueMeshletIndexed(in MeshletIndexedBackendRequest request, out string reason)
    {
        try { return EnqueueMeshletIndexedCore(in request, out reason); }
        catch (NotSupportedException error)
        {
            reason = error.Message;
            return MeshletResult(EMeshletSubmissionStatus.Rejected, reason);
        }
    }

    private EMeshletSubmissionStatus EnqueueMeshletIndexedCore(in MeshletIndexedBackendRequest request, out string reason)
    {
        EMeshletSubmissionStatus admission = GetMeshletIndexedAdmission(out reason);
        if (admission != EMeshletSubmissionStatus.Ready) return MeshletResult(admission, reason);
        if (!_engineRecording || request.Scene is null || request.Camera is null || request.FrameId == 0)
            return MeshletRejected("FrameRequired", "an exact published world, camera and active engine frame are required", out reason);
        if (RuntimeEngine.Rendering.State.IsStereoPass || request.Camera.DepthMode != XRCamera.EDepthMode.Normal)
            return MeshletRejected("ViewProfile", "stereo and reversed-Z meshlet views need their explicit authored raster profiles", out reason);
        if (request.SubmissionStrategy is not (EMeshSubmissionStrategy.GpuMeshletZeroReadback or EMeshSubmissionStrategy.GpuIndirectZeroReadback))
            return MeshletRejected("Strategy", "the browser meshlet lane admits the strict zero-readback submission mode", out reason);
        if (!TryAcquireMeshletSlot(in request, out WebGpuMeshletFrameSlot? slot, out reason))
            return MeshletResult(EMeshletSubmissionStatus.Pending, reason);
        WebGpuRenderProgram cull = GetMeshletProgram("cull-expand");
        WebGpuRenderProgram finalize = GetMeshletProgram("finalize-indexed");
        WebGpuRenderProgram refit = GetMeshletProgram("refit-bounds");
        bool programsReady = cull.TryPrepareForCompute();
        programsReady &= finalize.TryPrepareForCompute();
        programsReady &= refit.TryPrepareForCompute();
        if (!programsReady)
        {
            reason = "WebGPU.Meshlets.ProgramsPending: the exact compute companions are preparing.";
            return MeshletResult(EMeshletSubmissionStatus.Pending, reason);
        }
        ReadOnlySpan<GpuMeshSubmissionRecord> records = slot!.Publication.Records;
        if (slot.Publication.TryGetInvalidSourceOwnership(request.RenderPass, out EGpuMeshSubmissionSourceOwnership invalidOwnership))
            return invalidOwnership == EGpuMeshSubmissionSourceOwnership.MixedExplicitOwnership
                ? MeshletRejected("MixedExplicitOwnership", "one selected source mixes CPU-exempt and GPU-owned primitives; exact primitive replay is required", out reason)
                : MeshletRejected("IncompleteSource", "the selected resident publication omits an authored primitive; exact primitive publication is required", out reason);
        for (int index = 0; index < records.Length; index++)
        {
            ref readonly GpuMeshSubmissionRecord record = ref records[index];
            if (record.InstanceCount == 0) continue;
            if (!IncludesMeshletPass(in record, request.RenderPass) ||
                slot.Publication.GetSourceOwnership(record.Source) == EGpuMeshSubmissionSourceOwnership.ExplicitCpu) continue;
            if (record.InstanceCount > 1)
                return MeshletRejected("InstanceProfile", "GPU meshlet instance expansion requires an explicit per-instance transform and bounds publication", out reason);
            if (record.LodCount > 1)
                return MeshletRejected("DynamicLodUnavailable", "the shared GPU dynamic-LOD selection publication is not installed for authored indexed meshlets", out reason);
            if (!record.HasValidatedMeshletPayload || record.MeshletPayload is not { } payload)
                return MeshletRejected("PayloadUnavailable", "every requested GPU mesh needs its validated immutable cooked meshlet payload", out reason);
            if (record.GeometryRevision != record.Mesh.GeometryRevision || payload.ValidationRevision != record.PayloadValidationRevision ||
                payload.OwnerValidationToken != record.PayloadOwnerValidationToken || !record.SourceBindings.AreSourceBindingsCurrent ||
                !record.SourceBindings.ArePublisherGenerationsCurrent)
                return MeshletRejected("PublicationChanged", "source geometry or bindings changed after the resident publication boundary", out reason);
            if (record.BillboardMode != EMeshBillboardMode.None)
                return MeshletRejected("BillboardProfile", "billboarded meshlets need the same published vertex transform as their authored raster stage", out reason);
            if ((record.Metadata.Flags & (uint)GPUIndirectRenderFlags.Transparent) != 0 &&
                RuntimeEngine.Rendering.State.RenderingPipelineState?.ShadowPass != true)
                return MeshletRejected("TransparentOrderUnavailable", "view-dependent transparent source ordering has no shared GPU sort publication", out reason);
            WebGpuMeshletGeometry geometry = GetMeshletGeometry(record.Mesh, payload);
            WebGpuMeshletWork work = slot.NextWork();
            WebGpuMeshRenderer renderer = (WebGpuMeshRenderer)GetOrCreateAPIRenderObject(record.Renderer.GetDefaultVersion())!;
            if (!renderer.TryRenderMeshlet(in record, work, geometry, request.Camera, cull, finalize, refit, out bool unbounded))
            {
                reason = "WebGPU.Meshlets.AuthoredRasterPending: the original material, bindings, deformation or generated-index pipeline is preparing.";
                return MeshletResult(EMeshletSubmissionStatus.Pending, reason);
            }
            if (unbounded) SetField(ref _lastMeshletUnboundedDraws, _lastMeshletUnboundedDraws + 1, publishNotifications: false);
        }
        reason = _lastMeshletUnboundedDraws == 0 ? "Ready" : "Ready; undeclared vertex bounds conservatively disable meshlet rejection";
        return MeshletResult(EMeshletSubmissionStatus.Ready, reason);
    }

    private static bool IncludesMeshletPass(in GpuMeshSubmissionRecord record, int renderPass)
        => renderPass < 0 || record.RenderPass < 0 || record.RenderPass == renderPass;

    private WebGpuMeshletGeometry GetMeshletGeometry(XRMesh mesh, MeshletPayload payload)
    {
        if (_meshletGeometry.TryGetValue(payload, out WebGpuMeshletGeometry? geometry))
        {
            if (!geometry.Matches(mesh, payload))
                throw new NotSupportedException("WebGPU.Meshlets.GeometryGenerationChanged: the retained payload no longer matches its frozen mesh owner.");
            return geometry;
        }
        if (_meshletGeometry.Count >= 1024)
        {
            foreach ((MeshletPayload key, WebGpuMeshletGeometry candidate) in _meshletGeometry)
                if (candidate.LeaseCount == 0)
                { _meshletGeometry.Remove(key); candidate.Dispose(); break; }
            if (_meshletGeometry.Count >= 1024)
                throw new NotSupportedException("WebGPU.Meshlets.GeometryCapacity: all 1024 immutable meshlet generations remain retained by queued raster work.");
        }
        geometry = new(this, mesh, payload);
        _meshletGeometry.Add(payload, geometry);
        return geometry;
    }

    private WebGpuRenderProgram GetMeshletProgram(string pass)
    {
        if (!_meshletPrograms.TryGetValue(pass, out XRRenderProgram? program))
        {
            if (_meshletArtifacts is null || !_meshletArtifacts.TryResolve("meshlets::" + pass, out ShaderProgramArtifact? artifact) || artifact is null)
                throw new InvalidOperationException("The installed meshlet program is unavailable.");
            using IDisposable publication = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            program = new XRRenderProgram(false, false, Array.Empty<XRShader>())
            { Name = "Authored meshlet " + pass, CookedArtifact = artifact };
            _meshletPrograms.Add(pass, program);
        }
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        api.Generate();
        return api;
    }

    private bool TryAcquireMeshletSlot(in MeshletIndexedBackendRequest request, out WebGpuMeshletFrameSlot? slot, out string reason)
    {
        slot = null;
        if (_meshletSlots is null)
        {
            SetField(ref _meshletSlots, [new(this), new(this), new(this)], publishNotifications: false);
            ReclaimMeshletSlots();
        }
        foreach (WebGpuMeshletFrameSlot candidate in _meshletSlots!)
            if (candidate.RecordingSequence == _engineFrameSequence && ReferenceEquals(candidate.Scene, request.Scene))
            {
                if (candidate.Publication.FrameId != request.FrameId)
                    throw new InvalidOperationException("WebGPU.Meshlets.PublicationMismatch: one atomic frame cannot mix world publication identities.");
                slot = candidate;
                reason = string.Empty;
                return true;
            }
        int recorded = 0;
        foreach (WebGpuMeshletFrameSlot candidate in _meshletSlots!)
        {
            if (candidate.RecordingSequence == _engineFrameSequence) recorded++;
            if (!candidate.IsAvailable) continue;
            if (!request.Scene.TryAcquireMeshSubmissionPublication(out GpuMeshSubmissionPublicationLease lease)) break;
            if (lease.Publication.FrameId != request.FrameId)
            {
                lease.Dispose();
                reason = "WebGPU.Meshlets.PublicationPending: the resident source closure does not yet match this world frame.";
                return false;
            }
            candidate.Begin(request.Scene, lease, _engineFrameSequence);
            slot = candidate;
            reason = string.Empty;
            return true;
        }
        if (recorded == _meshletSlots!.Length)
            throw new NotSupportedException("WebGPU.Meshlets.PublicationCapacity: an atomic frame exceeds three independent resident scene publications.");
        reason = "WebGPU.Meshlets.SlotPending: a complete resident publication and completion-reclaimed meshlet slot are required.";
        return false;
    }

    private EMeshletSubmissionStatus MeshletResult(EMeshletSubmissionStatus status, string reason)
    {
        SetField(ref _lastMeshletSubmissionReason, reason, publishNotifications: false);
        if (status != EMeshletSubmissionStatus.Ready) MarkEngineDrawPending();
        return status;
    }

    private EMeshletSubmissionStatus MeshletRejected(string code, string detail, out string reason)
    {
        reason = $"WebGPU.Meshlets.{code}: {detail}.";
        return MeshletResult(EMeshletSubmissionStatus.Rejected, reason);
    }

    private void ReclaimMeshletSlots()
    {
        SetField(ref _lastMeshletUnboundedDraws, 0, publishNotifications: false);
        if (_meshletSlots is null) return;
        double completed = WebGpuImports.PollEngineFrameCompletion(_session);
        if (!double.IsFinite(completed) || completed < 0 || completed > _engineFrameSequence || completed != Math.Truncate(completed))
            throw new InvalidOperationException("WebGPU.Meshlets.CompletionInvalid: invalid queue completion watermark.");
        foreach (WebGpuMeshletFrameSlot slot in _meshletSlots) slot.Reclaim(checked((uint)completed));
    }

    private void EndMeshletRecording(bool submitted)
    {
        if (_meshletSlots is null) return;
        foreach (WebGpuMeshletFrameSlot slot in _meshletSlots) slot.EndRecording(_engineFrameSequence, submitted);
    }

    private void DestroyMeshletPrograms()
    {
        foreach (XRRenderProgram program in _meshletPrograms.Values) program.Destroy(now: true);
        _meshletPrograms.Clear();
    }

    /// <summary>Called after physical device disposal; publication pins remain valid for every accepted queue prefix.</summary>
    private void DisposeMeshletResources()
    {
        if (_meshletSlots is not null)
            foreach (WebGpuMeshletFrameSlot slot in _meshletSlots) slot.Dispose();
        SetField(ref _meshletSlots, null, publishNotifications: false);
        foreach (WebGpuMeshletGeometry geometry in _meshletGeometry.Values) geometry.Dispose();
        _meshletGeometry.Clear();
        SetField(ref _meshletArtifacts, null, publishNotifications: false);
    }
}
