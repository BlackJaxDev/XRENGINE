using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private WebGpuAuthoredOrderingBatch? _authoredDirectOrdering;
    private WebGpuAuthoredIndexedFrameSlot? _authoredDirectSlot;
    private XRMeshRenderer? _authoredDirectRenderer;
    private int _authoredDirectSourceIndex;
    private int _authoredDirectPrimitive;
    private int _authoredDirectCandidateDepth;
    internal bool IsAuthoredDirectOrderCapture => _authoredDirectOrdering is not null;

    internal AuthoredDirectCandidateScope EnterAuthoredDirectCandidate(XRMeshRenderer renderer)
    {
        if (_authoredDirectOrdering is null || !ReferenceEquals(renderer, _authoredDirectRenderer) || _authoredDirectCandidateDepth != 0)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.NestedDirectCandidate: direct candidate preparation requires its own non-nested captured source renderer.");
        SetField(ref _authoredDirectCandidateDepth, 1, publishNotifications: false);
        return new(this);
    }

    internal readonly ref struct AuthoredDirectCandidateScope(WebGpuRendererHost? renderer)
    {
        public void Dispose()
        {
            if (renderer is not null) renderer.SetField(ref renderer._authoredDirectCandidateDepth, 0, publishNotifications: false);
        }
    }

    private void CaptureAuthoredDirectSources(in AuthoredIndexedBackendRequest request,
        WebGpuAuthoredIndexedFrameSlot slot, WebGpuAuthoredOrderingBatch ordering)
    {
        BackendReadyFramePackage package = request.OrderPublicationPackage!;
        if (!package.TryGetPass(request.RenderPass, out BackendReadyRenderPass pass)) return;
        for (int index = 0; index < pass.CommandCount; index++)
        {
            if (package.GetPassCommand(in pass, index) is not IRenderCommandMesh source ||
                slot.Publication.GetSourceOwnership(source) != EGpuMeshSubmissionSourceOwnership.ExplicitCpu ||
                slot.Publication.IsGpuOwnedMaterialAuxiliary(source)) continue;
            if (request.CpuReplay is null)
                throw new NotSupportedException("WebGPU.AuthoredOrdering.CpuReplayMissing: an explicitly direct source requires its original collected-command replay producer.");
            XRMeshRenderer? renderer = null;
            uint instances = 0;
            foreach (ref readonly GpuMeshSubmissionRecord record in slot.Publication.Records)
                if (ReferenceEquals(record.Source, source)) { renderer = record.Renderer; instances = record.InstanceCount; break; }
            if (renderer is null)
                throw new NotSupportedException("WebGPU.AuthoredOrdering.DirectSourceMissing: the direct source has no exact resident renderer identity.");
            if (instances == 0) continue;
            int sourceIndex = ordering.FindSource(source);
            SetField(ref _authoredDirectOrdering, ordering, publishNotifications: false);
            SetField(ref _authoredDirectSlot, slot, publishNotifications: false);
            SetField(ref _authoredDirectRenderer, renderer, publishNotifications: false);
            SetField(ref _authoredDirectSourceIndex, sourceIndex, publishNotifications: false);
            SetField(ref _authoredDirectPrimitive, 0, publishNotifications: false);
            try
            {
                int immediateDraws = _engineMeshDrawCount;
                request.CpuReplay.ReplayOrderedMeshSource(package, request.RenderPass, source);
                if (_engineMeshDrawCount != immediateDraws)
                    throw new NotSupportedException("WebGPU.AuthoredOrdering.NestedDirectRasterCallback: a direct source callback emitted raster outside its captured rank-gated candidates.");
            }
            finally
            {
                SetField(ref _authoredDirectOrdering, null, publishNotifications: false);
                SetField(ref _authoredDirectSlot, null, publishNotifications: false);
                SetField(ref _authoredDirectRenderer, null, publishNotifications: false);
            }
        }
    }

    internal void PublishAuthoredDirectOrderGate(XRMeshRenderer renderer, WebGpuRenderProgram program)
    {
        if (_authoredDirectOrdering is not { } ordering || !ReferenceEquals(renderer, _authoredDirectRenderer))
            throw new NotSupportedException("WebGPU.AuthoredOrdering.DirectCallbackRenderer: a direct source callback cannot emit a different renderer without its own frozen ordering identity.");
        program.BindStorageBuffer(2, ordering.Ranks);
        program.Data.Uniform("AuthoredSourceIndex", checked((uint)_authoredDirectSourceIndex));
        program.Data.Uniform("AuthoredActiveRank", 0u);
        program.Data.Uniform("AuthoredSourceCount", checked((uint)ordering.SourceCount));
        program.Data.Uniform("AuthoredOrderEnabled", 1u);
    }

    internal void CaptureAuthoredOrderedDirect(WebGpuMeshDraw draw, WebGpuBindingSet bindings, uint instances)
    {
        WebGpuAuthoredOrderingBatch ordering = _authoredDirectOrdering
            ?? throw new InvalidOperationException("A direct ordered draw requires its shared source capture.");
        WebGpuAuthoredRasterSnapshot snapshot = _authoredDirectSlot!.NextDirectSnapshot();
        draw.CaptureRankedDirectRaster(bindings, instances, snapshot, ordering,
            _authoredDirectSourceIndex, _authoredDirectPrimitive);
        SetField(ref _authoredDirectPrimitive, _authoredDirectPrimitive + 1, publishNotifications: false);
    }
}
