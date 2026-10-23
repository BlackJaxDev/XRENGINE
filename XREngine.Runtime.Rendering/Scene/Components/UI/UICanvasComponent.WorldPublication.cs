using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasComponent
{
    private UIRenderSurfacePool? _offscreenSurfacePool;
    private UIDrawPayloadBuilder? _offscreenQuadPayloadBuilder;
    private RenderCommandMesh2D? _offscreenQuadCaptureCommand;
    private readonly List<PendingOffscreenProducer> _pendingOffscreenProducers = [];
    private readonly PendingOffscreenProducer?[] _pendingOffscreenScratch = new PendingOffscreenProducer?[4];
    private int _advancingOffscreenProducers;
    private UIPublication? _displayedOffscreenPublication;
    private bool _offscreenProducerQuarantined;
    private int _offscreenRecaptureRequired;

    private void RequestOffscreenRecapture() =>
        Volatile.Write(ref _offscreenRecaptureRequired, 1);

    internal static bool CanPromoteOffscreenPublication(UIPublication candidate,
        UIPublication? displayed, ulong canvasLifetime, bool completed) =>
        completed && candidate.CanvasLifetime == canvasLifetime &&
        (displayed is null || displayed.Generation < candidate.Generation);

    internal static bool MustRejectWorldConsumer(bool completed, bool requiresAtomic) =>
        !completed && requiresAtomic;

    private UIPublication? AcquireDisplayedOffscreenPublication()
    {
        lock (_publicationGate)
        {
            UIPublication? publication = _displayedOffscreenPublication;
            publication?.Retain();
            return publication;
        }
    }

    private bool HasPendingOffscreenProducer(UIPublication publication)
    {
        lock (_publicationGate)
        {
            for (int index = 0; index < _pendingOffscreenProducers.Count; index++)
                if (ReferenceEquals(_pendingOffscreenProducers[index].Publication, publication))
                    return true;
            return false;
        }
    }

    private void TrackOffscreenProducer(UIPublication publication,
        UIReceiptCompletionProbe completion)
    {
        UIRenderSurface surface = publication.OffscreenSurface ??
            throw new InvalidOperationException("An offscreen producer has no reserved surface.");
        publication.Retain();
        try
        {
            lock (_publicationGate)
                _pendingOffscreenProducers.Add(new PendingOffscreenProducer(publication,
                    surface, publication.OffscreenReservationGeneration, completion));
        }
        catch
        {
            _offscreenProducerQuarantined = true;
            publication.Release();
            throw;
        }
    }

    private void AdvanceOffscreenProducers()
    {
        if (Interlocked.Exchange(ref _advancingOffscreenProducers, 1) != 0)
            return;
        int count;
        try
        {
            lock (_publicationGate)
            {
                count = _pendingOffscreenProducers.Count;
                if (count > _pendingOffscreenScratch.Length)
                    throw new InvalidOperationException("The bounded offscreen producer capacity was exceeded.");
                for (int index = 0; index < count; index++)
                {
                    PendingOffscreenProducer entry = _pendingOffscreenProducers[index];
                    entry.Publication.Retain();
                    _pendingOffscreenScratch[index] = entry;
                }
            }
            for (int index = 0; index < count; index++)
            {
                PendingOffscreenProducer entry = _pendingOffscreenScratch[index]!;
                bool retired = entry.Completion.IsRetired;
                if (!retired)
                    continue;
                bool completed = entry.Completion.IsCompleted &&
                    ReferenceEquals(AbstractRenderer.Current, entry.Completion.Renderer) &&
                    AbstractRenderer.Current?.BackendGeneration == entry.Completion.BackendGeneration;
                if (entry.Completion.IsCompleted && !completed)
                    RequestOffscreenRecapture();
                UIPublication? oldDisplayed = null;
                bool removed;
                lock (_publicationGate)
                {
                    removed = _pendingOffscreenProducers.Remove(entry);
                    if (removed && IsActive &&
                        CanPromoteOffscreenPublication(entry.Publication,
                            _displayedOffscreenPublication, (ulong)_canvasLifetime, completed) &&
                        _offscreenSurfacePool?.TryPublish(entry.Surface, entry.Generation,
                            entry.Completion) == true)
                    {
                        entry.Publication.Retain();
                        oldDisplayed = _displayedOffscreenPublication;
                        _displayedOffscreenPublication = entry.Publication;
                    }
                }
                oldDisplayed?.Release();
                if (removed)
                    entry.Publication.Release();
            }
        }
        finally
        {
            for (int index = 0; index < _pendingOffscreenScratch.Length; index++)
            {
                PendingOffscreenProducer? entry = _pendingOffscreenScratch[index];
                _pendingOffscreenScratch[index] = null;
                entry?.Publication.Release();
            }
            Volatile.Write(ref _advancingOffscreenProducers, 0);
        }
    }

    private void ClearOffscreenPublicationHolders()
    {
        UIPublication? displayed;
        PendingOffscreenProducer[] pending;
        lock (_publicationGate)
        {
            displayed = _displayedOffscreenPublication;
            _displayedOffscreenPublication = null;
            pending = _pendingOffscreenProducers.ToArray();
            _pendingOffscreenProducers.Clear();
        }
        displayed?.Release();
        foreach (PendingOffscreenProducer entry in pending)
            entry.Publication.Release();
        _offscreenQuadPayloadBuilder?.Dispose();
        _offscreenQuadPayloadBuilder = null;
        _offscreenProducerQuarantined = false;
        Volatile.Write(ref _offscreenRecaptureRequired, 0);
    }

    private bool TryPrepareOffscreenPublication(out UIRenderSurface? writer,
        out UIDrawPayload? quadPayload, out Matrix4x4 quadWorldMatrix,
        out string? failure)
    {
        writer = null;
        quadPayload = null;
        quadWorldMatrix = Matrix4x4.Identity;
        failure = null;
        EnsureOffscreenResourcesInitialized();
        if (_ownedOffscreenRenderer is null || _offscreenMaterial is null)
        {
            failure = "UI.Canvas.SurfacePending: the quad draw resources are unavailable.";
            return false;
        }
        bool webGpu = OperatingSystem.IsBrowser() ||
            AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU ||
            RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked;
        _offscreenSurfacePool ??= webGpu
            ? new UIRenderSurfacePool(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba,
                EPixelType.HalfFloat, 8)
            : new UIRenderSurfacePool(EPixelInternalFormat.Rgba8, EPixelFormat.Rgba,
                EPixelType.UnsignedByte, 4);
        var bounds = CanvasTransform.GetActualBounds();
        float width = Math.Max(1.0f, bounds.Width);
        float height = Math.Max(1.0f, bounds.Height);
        if (width <= 1.0f && height <= 1.0f)
        {
            width = 1920.0f;
            height = 1080.0f;
        }
        if (!float.IsFinite(width) || !float.IsFinite(height) ||
            width > 16384 || height > 16384)
        {
            failure = "UI.Canvas.InvalidSurfaceSize: the canvas extent cannot be captured.";
            return false;
        }
        if (!_offscreenSurfacePool.TryReserve((uint)MathF.Ceiling(width),
                (uint)MathF.Ceiling(height), out writer, out failure))
            return false;
        bool prepared = false;
        try
        {
            _offscreenQuadPayloadBuilder ??= new UIDrawPayloadBuilder();
            _offscreenQuadPayloadBuilder.ProducerLease = writer;
            _offscreenQuadPayloadBuilder.ProducerTextureSlot = 0;
            _offscreenQuadPayloadBuilder.Bounds = new Vector4(0, 0, width, height);
            _offscreenQuadCaptureCommand ??= new RenderCommandMesh2D(
                (int)EDefaultRenderPass.TransparentForward)
            {
                Mesh = _ownedOffscreenRenderer,
                MaterialOverride = _offscreenMaterial,
            };
            if (!_offscreenQuadPayloadBuilder.TryCapture(_offscreenQuadCaptureCommand,
                    allowRendererCallbacks: false, out quadPayload, out failure))
                return false;
            Matrix4x4 world = GetWorldSpaceCanvasMatrix(out Vector2 size);
            quadWorldMatrix = Matrix4x4.CreateScale(size.X, size.Y, 1.0f) * world;
            prepared = true;
            return true;
        }
        finally
        {
            if (!prepared)
            {
                writer!.Release();
                writer = null;
            }
        }
    }

    internal void RenderPublishedWorldCommand(UIPublishedWorldMeshCommand command,
        Action draw)
    {
        UIPublication publication = command.Publication;
        publication.Retain();
        try
        {
            if (!publication.AuthoringOffscreen)
            {
                draw();
                return;
            }
            AbstractRenderer? renderer = AbstractRenderer.Current;
            if (publication.OffscreenSurface?.IsCompletedFor(
                    publication.OffscreenReservationGeneration, renderer) != true)
            {
                RequestOffscreenRecapture();
                if (renderer?.RequiresAtomicFrameAuthoring == true)
                    throw new RenderResourcePreparationPendingException(
                        "UI.Canvas.QuadPending: the sampled surface belongs to another renderer generation.");
                return;
            }
            XRViewport? viewport = RuntimeEngine.Rendering.State.RenderingViewport;
            bool presentsWindow = viewport?.Window is not null &&
                RuntimeEngine.Rendering.State.RenderingTargetOutputFBO is null;
            if (renderer is null || viewport is null ||
                !renderer.TryBeginUiPublicationReceipt(publication, 1,
                    requiresSwapchainPresentation: presentsWindow,
                    out ulong receiptId))
                return;
            try { BatchCollector.BeginPublicationReceipt(receiptId); }
            catch
            {
                renderer.CancelUiPublicationReceiptBeforeAuthoring(receiptId);
                throw;
            }
            bool authored = false;
            bool complete = false;
            XRGpuFence? completionFence = null;
            try
            {
                string failure = "the frozen quad is unavailable";
                XRMeshRenderer? mesh = command.Mesh;
                if (mesh is null || !command.Enabled || command.Instances == 0 ||
                    !mesh.TryPrepareForRendering(out failure))
                {
                    renderer.FailCurrentUiPublicationAuthoring(
                        $"UI.Canvas.QuadPending: {failure}.");
                }
                else
                {
                    draw();
                    authored = true;
                }
            }
            finally
            {
                try
                {
                    complete = renderer.CompleteUiPublicationReceipt(receiptId, authored,
                        out completionFence);
                }
                finally
                {
                    BatchCollector.SealPublicationReceipt(receiptId, completionFence, complete);
                }
            }
            if (complete && presentsWindow)
            {
                RecordRenderedPublication(viewport, publication, receiptId);
                viewport.Window?.RegisterUiWorldConsumer(viewport, this);
            }
            else if (MustRejectWorldConsumer(complete, renderer.RequiresAtomicFrameAuthoring))
                throw new RenderResourcePreparationPendingException(
                    "UI.Canvas.QuadPending: the sampled canvas did not complete its output receipt.");
        }
        finally { publication.Release(); }
    }
}
