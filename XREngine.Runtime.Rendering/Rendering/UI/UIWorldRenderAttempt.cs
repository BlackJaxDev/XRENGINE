using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.UI;

/// <summary>Owns one selected world UI image until native output and swap decide it.</summary>
internal sealed class UIWorldRenderAttempt
{
    [ThreadStatic] private static UIWorldRenderAttempt? t_active;
    private readonly UIWorldPassManifest _manifest = new();
    private UIWorldCollectPacketSet? _packets;
    private UIPublication? _publication;
    private UICanvasComponent? _canvas;
    private XRViewport? _viewport;
    private XRWindow? _window;
    private AbstractRenderer? _renderer;
    private RenderFrameViewDescriptor _view;
    private BoundingRectangle _region;
    private ulong _manifestToken;
    private ulong _receiptId;
    private ulong _nativeToken;
    private bool _active;
    private bool _requiredByWindow;
    private bool _passReceiptActive;
    private bool _receiptSuspended;
    private UIPublishedWorldMeshCommand? _currentDraw;

    internal static UIWorldRenderAttempt? Active => t_active;
    internal ulong NativeToken => _nativeToken;
    internal UIPublishedWorldMeshCommand? CurrentDraw => _currentDraw;

    internal DrawScope EnterDraw(UIPublishedWorldMeshCommand command)
    {
        if (!ReferenceEquals(t_active, this) || !_active ||
            _currentDraw is not null ||
            !ReferenceEquals(command.Publication, _publication))
            throw new InvalidOperationException(
                "A world UI draw has no unique active publication.");
        _currentDraw = command;
        return new DrawScope(this);
    }

    internal bool TryBegin(XRViewport viewport, XRWindow window,
        XRRenderPipelineInstance pipeline, RenderCommandCollection commands,
        long packageGeneration, UIWorldCollectPacketSet packets,
        UIPublication publication, UICanvasComponent canvas,
        BoundingRectangle region, in RenderFrameViewDescriptor view)
    {
        if (_active || t_active is not null)
        {
            packets.Release();
            return false;
        }
        _packets = packets;
        _publication = publication;
        _canvas = canvas;
        _viewport = viewport;
        _window = window;
        AbstractRenderer renderer = window.Renderer;
        _renderer = renderer;
        _view = view;
        _region = region;
        if (
            window.ActiveWorldUiDispatchToken == 0UL ||
            !ReferenceEquals(window.Renderer, AbstractRenderer.Current) ||
            !ReferenceEquals(viewport.Window, window) ||
            !ReferenceEquals(packets.ExpectedWorld, viewport.World?.WorldContext) ||
            packets.Failed || !packets.IsSealed ||
            publication.WorldCommands.Length == 0 ||
            region.Width <= 0 || region.Height <= 0)
            return RejectBeforeAuthoring();
        try
        {
            ReadOnlySpan<UIPublishedWorldMeshCommand> draws = publication.WorldCommands;
            for (int index = 0; index < draws.Length; index++)
                if (draws[index].IndexedDrawIntents.Length != 1 ||
                    draws[index].IndexedDrawIntents[0].Topology != EPrimitiveType.Triangles)
                    return RejectBeforeAuthoring();

            if (!_manifest.TryReserveAttempt(out _manifestToken) ||
                !_manifest.TryCaptureReserved(pipeline.Pipeline!.CommandChain,
                    _manifestToken, out _) ||
                !_manifest.TryBindPublication(canvas, publication) ||
                !_manifest.SetExpectedWorldColorDraws(
                    (int)EDefaultRenderPass.TransparentForward, draws.Length) ||
                !_manifest.SealPacketSet())
                return RejectBeforeAuthoring();

            if (!renderer.TryBeginUiPublicationReceipt(publication, draws.Length,
                    requiresSwapchainPresentation: true, out _receiptId))
                return RejectBeforeAuthoring();
            renderer.SuspendUiPublicationReceipt(_receiptId);
            _receiptSuspended = true;

            if (!renderer.TryBeginUiWorldOutputAttempt(window, pipeline, publication,
                    region, window.ActiveWorldUiDispatchToken, _receiptId,
                    _view,
                    out _nativeToken))
                return RejectBeforeAuthoring();
            if (renderer.BackendId == RendererBackendId.Vulkan)
                window.RequireVulkanWorldUi(viewport, commands, packageGeneration,
                    _receiptId, _nativeToken);
            else
                window.RequireOpenGlWorldUi(viewport, commands, packageGeneration,
                    _receiptId, _nativeToken);
            _requiredByWindow = true;
            _active = true;
            return true;
        }
        catch
        {
            RejectBeforeAuthoring();
            throw;
        }
    }

    private bool RejectBeforeAuthoring()
    {
        Exception? failure = null;
        if (_nativeToken != 0UL)
            try { _renderer?.AbortUiWorldOutputAttempt(_nativeToken); }
            catch (Exception error) { failure = error; }
        if (_receiptId != 0UL && _renderer is { } renderer)
        {
            try
            {
                if (_receiptSuspended)
                    renderer.ResumeUiPublicationReceipt(_receiptId);
                renderer.CancelUiPublicationReceiptBeforeAuthoring(_receiptId);
            }
            catch (Exception error)
            {
                failure ??= error;
                try { renderer.AbortUiPublicationReceipt(_receiptId,
                    "World UI output was rejected before authoring."); }
                catch (Exception abortError) { failure ??= abortError; }
            }
        }
        if (_manifestToken != 0UL)
            try
            {
                if (!_manifest.ReleaseAttempt(_manifestToken))
                    throw new InvalidOperationException("World UI graph attempt did not release.");
            }
            catch (Exception error) { failure ??= error; }
        try { _packets?.Release(); }
        catch (Exception error) { failure ??= error; }
        Clear();
        if (failure is not null)
            throw failure;
        return false;
    }

    internal Scope EnterScope()
    {
        if (!_active || t_active is not null)
            throw new InvalidOperationException("World UI output attempt has no exclusive scope.");
        return new Scope(this, _manifest.EnterScope(_manifestToken));
    }

    internal bool BeginMeshPass(VPRC_RenderMeshesPassShared pass)
    {
        if (!ReferenceEquals(t_active, this) || !_active || _passReceiptActive ||
            pass.RenderPass != (int)EDefaultRenderPass.TransparentForward ||
            _renderer is null)
            return false;
        _renderer.ResumeUiPublicationReceipt(_receiptId);
        _receiptSuspended = false;
        _passReceiptActive = true;
        return true;
    }

    internal void EndMeshPass()
    {
        if (!_passReceiptActive)
            return;
        _passReceiptActive = false;
        _renderer!.SuspendUiPublicationReceipt(_receiptId);
        _receiptSuspended = true;
    }

    internal bool Complete(bool rendered)
    {
        if (!_active || t_active is not null || _renderer is null ||
            _window is null || _publication is null || _canvas is null ||
            _viewport is null)
            return false;
        AbstractRenderer renderer = _renderer;
        XRWindow window = _window;
        UIPublication publication = _publication;
        UICanvasComponent canvas = _canvas;
        XRViewport viewport = _viewport;
        bool completed = false;
        bool receiptEnded = false;
        Exception? failure = null;
        try
        {
            bool graph = rendered && _manifest.TryValidateExecutionStructure(out _);
            bool native = false;
            try { native = renderer.CompleteUiWorldOutputAttempt(_nativeToken,
                out _); }
            catch (Exception error) { failure = error; }
            if (_receiptSuspended)
            {
                renderer.ResumeUiPublicationReceipt(_receiptId);
                _receiptSuspended = false;
            }
            bool receipt = renderer.CompleteUiPublicationReceipt(_receiptId,
                graph && native, out XRGpuFence? fence);
            receiptEnded = true;
            if (failure is null && graph && native && receipt && fence is not null)
            {
                canvas.RecordAcceptedWorldUiOutput(viewport, publication,
                    _receiptId, _view, _region);
                window.RegisterUiWorldConsumer(viewport, canvas);
                completed = true;
            }
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            if (!receiptEnded)
                try { renderer.AbortUiPublicationReceipt(_receiptId,
                    "World UI output did not finish its required authoring."); }
                catch (Exception error) { failure ??= error; }
            if (!completed && _nativeToken != 0UL)
                try { renderer.AbortUiWorldOutputAttempt(_nativeToken); }
                catch (Exception error) { failure ??= error; }
            if (_requiredByWindow)
                try
                {
                    if (renderer.BackendId == RendererBackendId.Vulkan)
                        window.CompleteVulkanWorldUi(_receiptId, completed);
                    else
                        window.CompleteOpenGlWorldUi(_receiptId, completed);
                }
                catch (Exception error) { failure ??= error; }
            else if (!completed)
                try
                {
                    if (renderer.BackendId == RendererBackendId.Vulkan)
                        window.FailVulkanWorldUiAttempt();
                    else
                        window.FailOpenGlWorldUiAttempt();
                }
                catch (Exception error) { failure ??= error; }
            if (_manifestToken != 0UL)
                try
                {
                    if (!_manifest.ReleaseAttempt(_manifestToken))
                        throw new InvalidOperationException("World UI graph attempt did not release.");
                }
                catch (Exception error) { failure ??= error; }
            try { _packets?.Release(); }
            catch (Exception error) { failure ??= error; }
            Clear();
        }
        if (failure is not null)
            throw failure;
        return completed;
    }

    private void Clear()
    {
        _packets = null;
        _publication = null;
        _canvas = null;
        _viewport = null;
        _window = null;
        _renderer = null;
        _view = default;
        _region = default;
        _manifestToken = _receiptId = _nativeToken = 0UL;
        _active = _requiredByWindow = _passReceiptActive = false;
        _receiptSuspended = false;
        _currentDraw = null;
    }

    internal readonly struct DrawScope : IDisposable
    {
        private readonly UIWorldRenderAttempt? _attempt;

        internal DrawScope(UIWorldRenderAttempt attempt) => _attempt = attempt;

        public void Dispose()
        {
            if (_attempt is not null)
                _attempt._currentDraw = null;
        }
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly UIWorldRenderAttempt? _attempt;
        private readonly UIWorldPassManifestScope _manifestScope;

        internal Scope(UIWorldRenderAttempt attempt, UIWorldPassManifestScope manifestScope)
        {
            _attempt = attempt;
            _manifestScope = manifestScope;
            t_active = attempt;
        }

        public void Dispose()
        {
            if (_attempt is null)
                return;
            t_active = null;
            _manifestScope.Dispose();
        }
    }
}
