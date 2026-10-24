using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

public sealed partial class UIBatchCollector
{
    private readonly record struct RetiredBackdropSurface(UIRenderSurface Surface,
        UIReceiptCompletionProbe Completion);

    private static readonly object BackdropQuarantineGate = new();
    private static readonly List<RetiredBackdropSurface> BackdropQuarantine = [];
    private static readonly List<UIRenderSurface> OrphanBackdropSurfaces = [];
    // One process-wide pool enforces the aggregate four-slot/128 MiB budget across canvases.
    private static readonly UIRenderSurfacePool BackdropSurfacePool = new(
        EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, 4);
    private readonly List<RetiredBackdropSurface> _pendingBackdropSurfaces = [];
    private UIRenderSurface? _activeBackdropSurface;
    private UIPublication? _activeBackdropPublication;
    private XRWindow? _activeBackdropWindow;
    private BoundingRectangle _activeBackdropRegion;
    private ulong _activeBackdropDispatch;
    private bool _activeBackdropReady;
    private readonly HashSet<(int Pass, ulong RunId)> _expectedBackdropRuns = [];
    private readonly HashSet<(int Pass, ulong RunId)> _completedBackdropRuns = [];
    private bool _backdropRequired;
    private bool _backdropConsumerMismatch;
    private UIPublication? _requiredBackdropPublication;

    internal void BeginBackdropAuthoring()
    {
        _backdropRequired = HasPublishedBackdrop();
        _backdropConsumerMismatch = false;
        _requiredBackdropPublication = _backdropRequired ? _renderingPublication : null;
        _expectedBackdropRuns.Clear();
        _completedBackdropRuns.Clear();
        if (!_backdropRequired)
            return;
        foreach (var pass in _renderMaterialGroups)
            for (int index = 0; index < pass.Value.Count; index++)
            {
                var group = pass.Value[index];
                if (group.Backdrop.IsDefined && group.Entries.Count > 0)
                    _expectedBackdropRuns.Add((pass.Key, group.RunId));
            }
    }

    internal bool AcknowledgeBackdropConsumer(int renderPass, ulong runId)
    {
        if (!_expectedBackdropRuns.Contains((renderPass, runId)) ||
            !_completedBackdropRuns.Add((renderPass, runId)))
        {
            _backdropConsumerMismatch = true;
            FailCurrentUiPublicationAuthoring(
                "UI.BackdropBlur.ConsumerDuplicate: a panel run executed outside its exact required draw set.");
            return false;
        }
        return true;
    }

    internal bool IsBackdropAuthoringComplete(bool captureReady)
        => !_backdropRequired ||
            ReferenceEquals(_requiredBackdropPublication, _renderingPublication) &&
            captureReady && !_backdropConsumerMismatch &&
            _expectedBackdropRuns.Count != 0 &&
            _expectedBackdropRuns.SetEquals(_completedBackdropRuns);

    internal void CompleteBackdropAuthoring()
    {
        if (!_backdropRequired)
            return;
        if (!IsBackdropAuthoringComplete(_activeBackdropReady && _activeBackdropSurface is not null))
            FailCurrentUiPublicationAuthoring(
                "UI.BackdropBlur.ConsumerIncomplete: required native panel draws or capture are missing.");
    }

    private bool HasPublishedBackdrop()
        => _renderingPublication?.HasVisibleBackdrop == true;

    private bool HasVisiblePreRenderDraw()
        => _renderingPublication?.HasVisiblePreRenderDraw == true;

    internal void BeginBackdropCapture(XRWindow? window, BoundingRectangle region)
    {
        if (!HasPublishedBackdrop())
            return;
        if (_activeBackdropSurface is not null)
        {
            FailCurrentUiPublicationAuthoring("UI.BackdropBlur.DuplicateCapture: a canvas prefix was captured twice.");
            return;
        }
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null || renderer.BackendId != RendererBackendId.OpenGL ||
            window is null || region.Width <= 0 || region.Height <= 0 ||
            HasVisiblePreRenderDraw() ||
            _renderingPublication is not { AuthoringDrawSpace: ECanvasDrawSpace.Screen,
                DrawSpace: ECanvasDrawSpace.Screen, AuthoringOffscreen: false } publication ||
            !ReferenceEquals(publication.RenderStorage, _renderPublishedStorage) ||
            !ReferenceEquals(publication.CompositionStorage, _renderCompositionStorage) ||
            _activeReceipt is null ||
            !renderer.TryGetCurrentUiBackdropOutputOverwriteProof(window, out var proof) ||
            !ReferenceEquals(proof.Renderer, renderer) ||
            proof.BackendGeneration != renderer.BackendGeneration ||
            proof.DispatchToken == 0UL || proof.DispatchToken != window.ActiveOpenGlDispatchToken ||
            !RegionContains(proof.Region, region))
        {
            FailCurrentUiPublicationAuthoring("UI.BackdropBlur.OutputUnproven: no current checked full-output replacement covers the capture region.");
            return;
        }
        SweepBackdropSurfacesForRenderer(renderer);
        if (!BackdropSurfacePool.TryReserve(checked((uint)region.Width), checked((uint)region.Height), renderer,
                out UIRenderSurface? surface, out string? failure))
        {
            FailCurrentUiPublicationAuthoring(failure ?? "UI.BackdropBlur.Capacity: no retired capture surface is available.");
            return;
        }
        _activeBackdropSurface = surface;
        _activeBackdropPublication = publication;
        _activeBackdropWindow = window;
        _activeBackdropRegion = region;
        _activeBackdropDispatch = proof.DispatchToken;
        FrameBufferBlitSubmission copy = renderer.TryBlitBackbufferRegionToFBO(window, region,
            surface!.FrameBuffer);
        if (!copy.Accepted || !copy.Completed)
        {
            FailCurrentUiPublicationAuthoring(copy.Reason ?? "UI.BackdropBlur.CopyFailed: the required backbuffer copy was not issued.");
            return;
        }
        _activeBackdropReady = true;
    }

    private static bool RegionContains(in BoundingRectangle outer, in BoundingRectangle inner)
        => outer.X <= inner.X && outer.Y <= inner.Y &&
           (long)outer.X + outer.Width >= (long)inner.X + inner.Width &&
           (long)outer.Y + outer.Height >= (long)inner.Y + inner.Height;

    private bool TryGetBackdropCapture(out XRTexture2D? texture, out BoundingRectangle region)
    {
        texture = _activeBackdropSurface?.Texture;
        region = _activeBackdropRegion;
        AbstractRenderer? renderer = AbstractRenderer.Current;
        return _activeBackdropReady && texture is not null && _activeBackdropWindow is not null &&
            ReferenceEquals(_activeBackdropPublication, _renderingPublication) &&
            renderer?.BackendId == RendererBackendId.OpenGL &&
            _activeBackdropDispatch != 0UL &&
            _activeBackdropDispatch == _activeBackdropWindow.ActiveOpenGlDispatchToken &&
            _activeReceipt is { } receipt && ReferenceEquals(receipt.Renderer, renderer);
    }

    private void SealBackdropSurface(UIReceiptCompletionProbe completion)
    {
        if (_activeBackdropSurface is { } surface)
            _pendingBackdropSurfaces.Add(new(surface, completion));
        _activeBackdropSurface = null;
        _activeBackdropPublication = null;
        _activeBackdropWindow = null;
        _activeBackdropRegion = default;
        _activeBackdropDispatch = 0UL;
        _activeBackdropReady = false;
        _backdropRequired = false;
        _backdropConsumerMismatch = false;
        _requiredBackdropPublication = null;
        _expectedBackdropRuns.Clear();
        _completedBackdropRuns.Clear();
    }

    private void SweepBackdropSurfaces(AbstractRenderer renderer)
    {
        for (int index = _pendingBackdropSurfaces.Count - 1; index >= 0; index--)
            if (ReferenceEquals(_pendingBackdropSurfaces[index].Completion.Renderer, renderer) &&
                _pendingBackdropSurfaces[index].Completion.BackendGeneration == renderer.BackendGeneration &&
                _pendingBackdropSurfaces[index].Completion.IsRetired)
            {
                _pendingBackdropSurfaces[index].Surface.Release();
                _pendingBackdropSurfaces.RemoveAt(index);
            }
    }

    private static void SweepBackdropSurfacesForRenderer(AbstractRenderer renderer)
    {
        lock (CollectorRegistryGate)
            for (int index = CollectorRegistry.Count - 1; index >= 0; index--)
                if (CollectorRegistry[index].TryGetTarget(out UIBatchCollector? collector) && !collector._disposed)
                    collector.SweepBackdropSurfaces(renderer);
        lock (BackdropQuarantineGate)
            for (int index = BackdropQuarantine.Count - 1; index >= 0; index--)
                if (ReferenceEquals(BackdropQuarantine[index].Completion.Renderer, renderer) &&
                    BackdropQuarantine[index].Completion.BackendGeneration == renderer.BackendGeneration &&
                    BackdropQuarantine[index].Completion.IsRetired)
                {
                    BackdropQuarantine[index].Surface.Release();
                    BackdropQuarantine.RemoveAt(index);
                }
    }

    private void DisposeBackdropSurfaces()
    {
        if (AbstractRenderer.Current is { } renderer)
            SweepBackdropSurfaces(renderer);
        lock (BackdropQuarantineGate)
            BackdropQuarantine.AddRange(_pendingBackdropSurfaces);
        _pendingBackdropSurfaces.Clear();
        if (_activeBackdropSurface is { } active)
        {
            lock (BackdropQuarantineGate)
                if (_activeReceipt is { } receipt)
                    BackdropQuarantine.Add(new(active, receipt.Completion));
                else
                    OrphanBackdropSurfaces.Add(active);
            _activeBackdropSurface = null;
        }
    }

    private void RetireBackdropKnownIdle(AbstractRenderer renderer, long backendGeneration)
    {
        for (int index = _pendingBackdropSurfaces.Count - 1; index >= 0; index--)
        {
            RetiredBackdropSurface pending = _pendingBackdropSurfaces[index];
            if (!ReferenceEquals(pending.Completion.Renderer, renderer) ||
                pending.Completion.BackendGeneration != backendGeneration)
                continue;
            pending.Surface.Release();
            _pendingBackdropSurfaces.RemoveAt(index);
        }
        if (_activeBackdropSurface is { } active &&
            ReferenceEquals(active.OwnerRenderer, renderer) &&
            active.OwnerBackendGeneration == backendGeneration)
        {
            active.Release();
            _activeBackdropSurface = null;
            _activeBackdropReady = false;
        }
    }

    private static void RetireQuarantinedBackdropKnownIdle(
        AbstractRenderer renderer, long backendGeneration)
    {
        lock (BackdropQuarantineGate)
        {
            for (int index = BackdropQuarantine.Count - 1; index >= 0; index--)
            {
                RetiredBackdropSurface pending = BackdropQuarantine[index];
                if (!ReferenceEquals(pending.Completion.Renderer, renderer) ||
                    pending.Completion.BackendGeneration != backendGeneration)
                    continue;
                pending.Surface.Release();
                BackdropQuarantine.RemoveAt(index);
            }
            for (int index = OrphanBackdropSurfaces.Count - 1; index >= 0; index--)
            {
                UIRenderSurface orphan = OrphanBackdropSurfaces[index];
                if (!ReferenceEquals(orphan.OwnerRenderer, renderer) ||
                    orphan.OwnerBackendGeneration != backendGeneration)
                    continue;
                orphan.Release();
                OrphanBackdropSurfaces.RemoveAt(index);
            }
        }
        BackdropSurfacePool.RetireOwner(renderer, backendGeneration);
    }

    private XRMeshRenderer EnsureBackdropMesh(XRTexture2D texture)
    {
        if (_matQuadBackdropMesh is not null && ReferenceEquals(_matQuadBackdropTexture, texture))
            return _matQuadBackdropMesh;
        XRMaterial? retiredMaterial = _matQuadBackdropMesh?.Material;
        _matQuadBackdropMesh?.Destroy();
        retiredMaterial?.Destroy();
        _matQuadBackdropMesh = null;
        _matQuadBackdropTexture = null;
        EnsureMaterialQuadMesh();
        XRMaterial material = new(
            [new ShaderVector2(Vector2.Zero, "CaptureOrigin"),
             new ShaderVector2(Vector2.One, "CaptureSize"),
             new ShaderFloat(0.0f, "BlurStrength")],
            [texture], UIBatchMaterialPrototype.Backdrop.CreateDesktopShaders())
        {
            Name = "UIBatchBackdropBlur",
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            RenderOptions = UIBatchMaterialPrototype.Backdrop.CreateRenderOptions(false)
        };
        XRMeshRenderer mesh = new(_matQuadMesh!.Mesh!, material)
        {
            Name = "UIBatchBackdropBlurRenderer",
            GenerationPriority = EMeshGenerationPriority.RenderPipeline,
            CaptureUniformsOnRender = true
        };
        mesh.Buffers["QuadTransformBuffer"] = _matQuadTransformBuf!;
        mesh.Buffers["QuadColorBuffer"] = _matQuadColorBuf!;
        mesh.Buffers["QuadBoundsBuffer"] = _matQuadBoundsBuf!;
        mesh.Buffers["QuadUvBuffer"] = _matQuadUvBuf!;
        mesh.Buffers["QuadDrawIndexBuffer"] = _matQuadDrawIndexBuf!;
        mesh.BindingPublishers.Add(new UIRequiredTextureBindings(texture));
        DisableShaderPipelines(mesh);
        mesh.EnsureRenderPipelineVersionsCreated();
        _matQuadBackdropMesh = mesh;
        _matQuadBackdropTexture = texture;
        return mesh;
    }
}
