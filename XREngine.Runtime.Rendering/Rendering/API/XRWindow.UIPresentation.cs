using XREngine.Components;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;
using UiPublicationFrameEntry = (XREngine.Rendering.XRViewport Viewport,
    XREngine.Components.UICanvasComponent Canvas,
    XREngine.Rendering.UI.UIPublication Publication, ulong ReceiptId);

namespace XREngine.Rendering;

public sealed partial class XRWindow
{
    private readonly List<UiPublicationFrameEntry>
        _windowUiFrame = [];
    private ulong _windowUiFrameId;
    private readonly List<UiPublicationFrameEntry>
        _lastPresentedUiFrame = [];
    private readonly Dictionary<uint, List<UiPublicationFrameEntry>>
        _vulkanUiImagePackets = [];
    private readonly Dictionary<uint, ulong> _vulkanUiImageSources = [];
    private ulong _vulkanUiOutputGeneration;
    private readonly List<(XRViewport Viewport, UICanvasComponent Canvas, ulong FrameId)>
        _worldUiConsumers = [];
    private ulong _nextOpenGlDispatchToken;
    private ulong _activeOpenGlDispatchToken;
    internal ulong ActiveOpenGlDispatchToken => _activeOpenGlDispatchToken;
    internal bool HasUntrackedLateWorldUiOutput => PostRenderViewportsCallback is not null;
    private readonly HashSet<ulong> _requiredOpenGlBackdropReceipts = [];
    private bool _openGlBackdropFailed;
    private readonly HashSet<ulong> _requiredOpenGlWorldReceipts = [];
    private readonly Dictionary<ulong, ulong> _openGlWorldNativeTokens = [];
    private bool _openGlWorldFailed;
    private XRViewport? _expectedOpenGlWorldViewport;
    private RenderCommandCollection? _expectedOpenGlWorldCommands;
    private long _expectedOpenGlWorldPackageGeneration;
    private ulong _nextVulkanWorldDispatchToken;
    private ulong _activeVulkanWorldDispatchToken;
    private XRViewport? _expectedVulkanWorldViewport;
    private RenderCommandCollection? _expectedVulkanWorldCommands;
    private long _expectedVulkanWorldPackageGeneration;
    private ulong _vulkanWorldReceipt;
    private ulong _vulkanWorldNativeToken;
    private bool _vulkanWorldAttemptCompleted;
    private bool _vulkanWorldDispatchFailed;

    internal ulong ActiveWorldUiDispatchToken =>
        Renderer.BackendId == RendererBackendId.OpenGL
            ? _activeOpenGlDispatchToken
            : Renderer.BackendId == RendererBackendId.Vulkan
                ? _activeVulkanWorldDispatchToken
                : 0UL;

    private void BeginVulkanWorldUiRenderDispatch()
    {
        if (_activeVulkanWorldDispatchToken != 0UL)
            throw new InvalidOperationException("A Vulkan world UI dispatch is already active.");
        ulong token = unchecked(++_nextVulkanWorldDispatchToken);
        if (token == 0UL)
            token = unchecked(++_nextVulkanWorldDispatchToken);
        _activeVulkanWorldDispatchToken = token;
        _expectedVulkanWorldViewport = null;
        _expectedVulkanWorldCommands = null;
        _expectedVulkanWorldPackageGeneration = 0;
        _vulkanWorldReceipt = _vulkanWorldNativeToken = 0UL;
        _vulkanWorldAttemptCompleted = _vulkanWorldDispatchFailed = false;
        for (int index = 0; index < Viewports.Count; index++)
        {
            XRViewport viewport = Viewports[index];
            if (!viewport.TryGetRenderingRetainedWorldUiImageKey(
                    out RenderCommandCollection commands,
                    out long packageGeneration))
                continue;
            if (_expectedVulkanWorldViewport is not null)
            {
                _vulkanWorldDispatchFailed = true;
                break;
            }
            _expectedVulkanWorldViewport = viewport;
            _expectedVulkanWorldCommands = commands;
            _expectedVulkanWorldPackageGeneration = packageGeneration;
        }
    }

    internal void RequireVulkanWorldUi(XRViewport viewport,
        RenderCommandCollection commands, long packageGeneration,
        ulong receiptId, ulong nativeToken)
    {
        if (_activeVulkanWorldDispatchToken == 0UL ||
            _vulkanWorldDispatchFailed || _vulkanWorldReceipt != 0UL ||
            receiptId == 0UL || nativeToken == 0UL ||
            !ReferenceEquals(viewport, _expectedVulkanWorldViewport) ||
            !ReferenceEquals(commands, _expectedVulkanWorldCommands) ||
            packageGeneration != _expectedVulkanWorldPackageGeneration)
            throw new InvalidOperationException(
                "A Vulkan world UI receipt has no unique selected image.");
        _vulkanWorldReceipt = receiptId;
        _vulkanWorldNativeToken = nativeToken;
    }

    internal void CompleteVulkanWorldUi(ulong receiptId, bool authoringSucceeded)
    {
        if (_activeVulkanWorldDispatchToken == 0UL ||
            receiptId == 0UL || receiptId != _vulkanWorldReceipt ||
            _vulkanWorldAttemptCompleted)
            throw new InvalidOperationException(
                "A Vulkan world UI attempt ended outside its selected image.");
        _vulkanWorldAttemptCompleted = authoringSucceeded;
        if (!authoringSucceeded)
            _vulkanWorldDispatchFailed = true;
    }

    internal void FailVulkanWorldUiAttempt() => _vulkanWorldDispatchFailed = true;

    internal void FailRetainedWorldUiAttempt()
    {
        if (Renderer.BackendId == RendererBackendId.Vulkan)
            FailVulkanWorldUiAttempt();
        else
            FailOpenGlWorldUiAttempt();
    }

    internal bool HasRequiredVulkanWorldUiImage =>
        _expectedVulkanWorldViewport is not null;

    internal bool TryGetRequiredVulkanWorldUiAttempt(
        out XRViewport? viewport, out RenderCommandCollection? commands,
        out long packageGeneration, out ulong receiptId, out ulong nativeToken)
    {
        viewport = _expectedVulkanWorldViewport;
        commands = _expectedVulkanWorldCommands;
        packageGeneration = _expectedVulkanWorldPackageGeneration;
        receiptId = _vulkanWorldReceipt;
        nativeToken = _vulkanWorldNativeToken;
        if (_activeVulkanWorldDispatchToken == 0UL ||
            _vulkanWorldDispatchFailed || !_vulkanWorldAttemptCompleted ||
            viewport is null || commands is null || receiptId == 0UL ||
            nativeToken == 0UL || Viewports.Count != 1 ||
            !ReferenceEquals(Viewports[0], viewport) ||
            !viewport.TryGetRenderingRetainedWorldUiImageKey(
                out RenderCommandCollection currentCommands,
                out long currentGeneration) ||
            !ReferenceEquals(currentCommands, commands) ||
            currentGeneration != packageGeneration)
            return false;
        return true;
    }

    private void EndVulkanWorldUiRenderDispatch()
    {
        _activeVulkanWorldDispatchToken = 0UL;
        _expectedVulkanWorldViewport = null;
        _expectedVulkanWorldCommands = null;
        _expectedVulkanWorldPackageGeneration = 0;
        _vulkanWorldReceipt = _vulkanWorldNativeToken = 0UL;
        _vulkanWorldAttemptCompleted = _vulkanWorldDispatchFailed = false;
    }

    private ulong BeginOpenGlRenderDispatch()
    {
        if (_activeOpenGlDispatchToken != 0UL)
            throw new InvalidOperationException("An OpenGL window dispatch is already active.");
        ulong token = unchecked(++_nextOpenGlDispatchToken);
        if (token == 0UL)
            token = unchecked(++_nextOpenGlDispatchToken);
        _activeOpenGlDispatchToken = token;
        _requiredOpenGlBackdropReceipts.Clear();
        _openGlBackdropFailed = false;
        _requiredOpenGlWorldReceipts.Clear();
        _openGlWorldNativeTokens.Clear();
        _openGlWorldFailed = false;
        _expectedOpenGlWorldViewport = null;
        _expectedOpenGlWorldCommands = null;
        _expectedOpenGlWorldPackageGeneration = 0;
        if (Viewports is { } viewports)
            for (int index = 0; index < viewports.Count; index++)
            {
                XRViewport viewport = viewports[index];
                if (!viewport.TryGetRenderingRetainedWorldUiImageKey(
                        out RenderCommandCollection commands,
                        out long packageGeneration))
                    continue;
                if (_expectedOpenGlWorldViewport is not null)
                {
                    _openGlWorldFailed = true;
                    break;
                }
                _expectedOpenGlWorldViewport = viewport;
                _expectedOpenGlWorldCommands = commands;
                _expectedOpenGlWorldPackageGeneration = packageGeneration;
            }
        return token;
    }

    internal void RequireOpenGlBackdrop(ulong receiptId)
    {
        if (_activeOpenGlDispatchToken == 0UL || receiptId == 0UL ||
            !_requiredOpenGlBackdropReceipts.Add(receiptId))
            throw new InvalidOperationException("A required OpenGL backdrop has no unique active output attempt.");
    }

    internal void CompleteOpenGlBackdrop(ulong receiptId, bool succeeded)
    {
        if (_activeOpenGlDispatchToken == 0UL || !_requiredOpenGlBackdropReceipts.Remove(receiptId))
            throw new InvalidOperationException("An OpenGL backdrop was completed outside its required output attempt.");
        if (!succeeded)
            _openGlBackdropFailed = true;
    }

    internal void FailOpenGlBackdropAttempt() => _openGlBackdropFailed = true;

    internal void RequireOpenGlWorldUi(XRViewport viewport,
        RenderCommandCollection commands, long packageGeneration,
        ulong receiptId, ulong nativeToken)
    {
        if (_activeOpenGlDispatchToken == 0UL || receiptId == 0UL || nativeToken == 0UL ||
            !ReferenceEquals(viewport, _expectedOpenGlWorldViewport) ||
            !ReferenceEquals(commands, _expectedOpenGlWorldCommands) ||
            packageGeneration != _expectedOpenGlWorldPackageGeneration ||
            !_requiredOpenGlWorldReceipts.Add(receiptId))
            throw new InvalidOperationException("A world UI receipt has no unique window dispatch.");
        try { _openGlWorldNativeTokens.Add(receiptId, nativeToken); }
        catch
        {
            _requiredOpenGlWorldReceipts.Remove(receiptId);
            throw;
        }
    }

    internal void CompleteOpenGlWorldUi(ulong receiptId, bool succeeded)
    {
        if (_activeOpenGlDispatchToken == 0UL ||
            !_requiredOpenGlWorldReceipts.Remove(receiptId))
            throw new InvalidOperationException("A world UI receipt ended outside its window dispatch.");
        if (!succeeded)
            _openGlWorldFailed = true;
    }

    internal void FailOpenGlWorldUiAttempt() => _openGlWorldFailed = true;

    private bool CanPresentOpenGlRenderDispatch(ulong dispatchToken)
    {
        if (dispatchToken == 0UL || dispatchToken != _activeOpenGlDispatchToken ||
            _openGlBackdropFailed || _requiredOpenGlBackdropReceipts.Count != 0 ||
            _openGlWorldFailed || _requiredOpenGlWorldReceipts.Count != 0 ||
            !MatchesExpectedOpenGlWorldImage() ||
            (_openGlWorldNativeTokens.Count != 0 && HasUntrackedLateWorldUiOutput))
            return false;
        foreach (ulong nativeToken in _openGlWorldNativeTokens.Values)
            if (!Renderer.IsUiWorldOutputAttemptStillValid(nativeToken))
                return false;
        return true;
    }

    private bool MatchesExpectedOpenGlWorldImage()
    {
        if (_expectedOpenGlWorldViewport is not { } expected)
        {
            if (_openGlWorldNativeTokens.Count != 0)
                return false;
            if (Viewports is { } currentViewports)
                for (int index = 0; index < currentViewports.Count; index++)
                    if (currentViewports[index].HasRenderingRetainedWorldUiImage)
                        return false;
            return true;
        }
        if (Viewports is not { Count: 1 } viewports ||
            !ReferenceEquals(viewports[0], expected) ||
            !expected.EnableRetainedWorldUiColor ||
            _openGlWorldNativeTokens.Count != 1 ||
            !expected.TryGetRenderingRetainedWorldUiImageKey(
                out RenderCommandCollection commands,
                out long packageGeneration))
            return false;
        return ReferenceEquals(commands, _expectedOpenGlWorldCommands) &&
            packageGeneration == _expectedOpenGlWorldPackageGeneration;
    }

    private void CompleteOpenGlRenderDispatch(ulong dispatchToken, bool presented)
    {
        if (dispatchToken == 0UL || dispatchToken != _activeOpenGlDispatchToken)
            throw new InvalidOperationException("The OpenGL window dispatch token is not active.");
        _activeOpenGlDispatchToken = 0UL;
        Exception? failure = null;
        if (!presented && _expectedOpenGlWorldViewport is { } failedViewport)
            try { failedViewport.DiscardRetainedWorldUiHistory(); }
            catch (Exception error) { failure ??= error; }
        foreach (ulong nativeToken in _openGlWorldNativeTokens.Values)
            try { Renderer.AbortUiWorldOutputAttempt(nativeToken); }
            catch (Exception error) { failure ??= error; }
        _openGlWorldNativeTokens.Clear();
        _requiredOpenGlBackdropReceipts.Clear();
        _openGlBackdropFailed = false;
        _requiredOpenGlWorldReceipts.Clear();
        _openGlWorldFailed = false;
        _expectedOpenGlWorldViewport = null;
        _expectedOpenGlWorldCommands = null;
        _expectedOpenGlWorldPackageGeneration = 0;
        try
        {
            if (presented)
                OnOpenGlRenderDispatched();
            else
            {
                try { ReleaseUiFrame(_windowUiFrame); }
                finally { _worldUiConsumers.Clear(); }
            }
        }
        catch (Exception error) { failure ??= error; }
        if (failure is not null)
            throw failure;
    }

    internal void RegisterUiWorldConsumer(XRViewport viewport, UICanvasComponent canvas)
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        for (int index = 0; index < _worldUiConsumers.Count; index++)
            if (ReferenceEquals(_worldUiConsumers[index].Viewport, viewport) &&
                ReferenceEquals(_worldUiConsumers[index].Canvas, canvas) &&
                _worldUiConsumers[index].FrameId == frameId)
                return;
        _worldUiConsumers.Add((viewport, canvas, frameId));
    }

    private void CaptureWindowUiFrame(ulong renderFrameId)
    {
        // A reserved multipass UI packet cannot be attributed to a submitted
        // output when one of its required passes never reached authoring.
        Renderer.AbortUnfinishedUiPublicationReceipts(renderFrameId);
        ReleaseUiFrame(_windowUiFrame);
        _windowUiFrameId = renderFrameId;
        for (int index = 0; index < Viewports.Count; index++)
        {
            XRViewport viewport = Viewports[index];
            if (!viewport.TryGetActiveScreenSpaceUserInterface(out var ui) ||
                ui is not UICanvasComponent canvas ||
                !canvas.TryAcquireRenderedPublication(viewport, renderFrameId,
                    out UIPublication? publication, out ulong receiptId) || publication is null)
                continue;
            if (receiptId != 0UL)
                _windowUiFrame.Add((viewport, canvas, publication, receiptId));
            else
                publication.Release();
        }
        for (int index = 0; index < _worldUiConsumers.Count; index++)
        {
            var consumer = _worldUiConsumers[index];
            if (consumer.FrameId != renderFrameId ||
                !consumer.Canvas.TryAcquireRenderedPublication(consumer.Viewport,
                    renderFrameId, out UIPublication? publication, out ulong receiptId) ||
                publication is null)
                continue;
            if (receiptId != 0UL)
                _windowUiFrame.Add((consumer.Viewport, consumer.Canvas, publication, receiptId));
            else
                publication.Release();
        }
        _worldUiConsumers.Clear();
    }

    private void OnOpenGlRenderDispatched()
    {
        XRWindowCompletedRenderInterval interval = LastCompletedRenderWindowInterval;
        if (!interval.Succeeded || interval.EngineRenderFrameId != _windowUiFrameId)
            return;
        PublishWindowUiFrame(_windowUiFrame);
    }

    /// <summary>Accepts the UI packet from the source image after Vulkan accepts presentation.</summary>
    public void MarkVulkanUiPresented(uint imageIndex, ulong outputGeneration,
        ulong sourceFrameId, bool newSceneContent)
    {
        if (_vulkanUiOutputGeneration != outputGeneration)
        {
            ReleaseVulkanUiImages();
            _vulkanUiImageSources.Clear();
            _vulkanUiOutputGeneration = outputGeneration;
        }

        if (!_vulkanUiImagePackets.TryGetValue(imageIndex, out var imagePackets))
        {
            imagePackets = [];
            _vulkanUiImagePackets.Add(imageIndex, imagePackets);
        }

        if (newSceneContent)
        {
            ReleaseUiFrame(imagePackets);
            for (int index = 0; index < _windowUiFrame.Count; index++)
            {
                UiPublicationFrameEntry packet = _windowUiFrame[index];
                if (Renderer.TryAcceptPresentedUiPublicationReceipt(
                        packet.ReceiptId, imageIndex, outputGeneration, sourceFrameId))
                {
                    packet.Publication.Retain();
                    imagePackets.Add(packet);
                }
            }
            _vulkanUiImageSources[imageIndex] = sourceFrameId;
        }
        else if (sourceFrameId != 0 &&
            (!_vulkanUiImageSources.TryGetValue(imageIndex, out ulong currentSource) ||
             currentSource != sourceFrameId))
        {
            List<UiPublicationFrameEntry>? source = null;
            uint sourceImageIndex = 0;
            foreach (var pair in _vulkanUiImageSources)
                if (pair.Value == sourceFrameId &&
                    _vulkanUiImagePackets.TryGetValue(pair.Key, out source))
                {
                    sourceImageIndex = pair.Key;
                    break;
                }
            if (source is null)
            {
                Renderer.InvalidatePresentedUiPublicationImage(imageIndex, outputGeneration);
                ReleaseUiFrame(imagePackets);
                _vulkanUiImageSources.Remove(imageIndex);
                PublishWindowUiFrame(imagePackets);
                return;
            }
            Renderer.InvalidatePresentedUiPublicationImage(imageIndex, outputGeneration);
            ReleaseUiFrame(imagePackets);
            for (int index = 0; index < source.Count; index++)
            {
                UiPublicationFrameEntry packet = source[index];
                if (Renderer.TryAcceptReplayedUiPublicationReceipt(
                        packet.ReceiptId, sourceImageIndex, outputGeneration,
                        imageIndex, outputGeneration, sourceFrameId))
                {
                    packet.Publication.Retain();
                    imagePackets.Add(packet);
                }
            }
            _vulkanUiImageSources[imageIndex] = sourceFrameId;
        }

        PublishWindowUiFrame(imagePackets);
    }

    /// <summary>Revokes UI input when recovery presents content without a matching UI receipt.</summary>
    public void InvalidateVulkanUiPresentedImage(uint imageIndex, ulong outputGeneration)
    {
        Renderer.InvalidatePresentedUiPublicationImage(imageIndex, outputGeneration);
        if (_vulkanUiOutputGeneration != outputGeneration)
        {
            ReleaseVulkanUiImages();
            _vulkanUiImageSources.Clear();
            _vulkanUiOutputGeneration = outputGeneration;
        }
        if (_vulkanUiImagePackets.Remove(imageIndex, out var oldImage))
            ReleaseUiFrame(oldImage);
        _vulkanUiImageSources.Remove(imageIndex);
        PublishWindowUiFrame([]);
    }

    private void PublishWindowUiFrame(
        List<UiPublicationFrameEntry> packets)
    {
        for (int oldIndex = 0; oldIndex < _lastPresentedUiFrame.Count; oldIndex++)
        {
            var old = _lastPresentedUiFrame[oldIndex];
            bool retained = false;
            for (int index = 0; index < packets.Count; index++)
                if (ReferenceEquals(old.Viewport, packets[index].Viewport) &&
                    ReferenceEquals(old.Canvas, packets[index].Canvas))
                {
                    retained = true;
                    break;
                }
            if (!retained)
                old.Canvas.RemovePublicationOutput(old.Viewport);
        }
        for (int index = 0; index < packets.Count; index++)
        {
            var entry = packets[index];
            entry.Canvas.MarkPresented(entry.Viewport, entry.Publication);
        }
        ReleaseUiFrame(_lastPresentedUiFrame);
        for (int index = 0; index < packets.Count; index++)
        {
            UiPublicationFrameEntry packet = packets[index];
            packet.Publication.Retain();
            _lastPresentedUiFrame.Add(packet);
        }
    }

    private static void ReleaseUiFrame(List<UiPublicationFrameEntry> frame)
    {
        while (frame.Count > 0)
        {
            int index = frame.Count - 1;
            UIPublication publication = frame[index].Publication;
            frame.RemoveAt(index);
            publication.Release();
        }
    }

    private void ReleaseVulkanUiImages()
    {
        while (_vulkanUiImagePackets.Count > 0)
        {
            uint imageIndex = 0;
            List<UiPublicationFrameEntry>? frame = null;
            foreach (var image in _vulkanUiImagePackets)
            {
                imageIndex = image.Key;
                frame = image.Value;
                break;
            }
            _vulkanUiImagePackets.Remove(imageIndex);
            ReleaseUiFrame(frame!);
        }
    }

    private void ReleaseWindowUiPublications()
    {
        _worldUiConsumers.Clear();
        ReleaseUiFrame(_windowUiFrame);
        ReleaseUiFrame(_lastPresentedUiFrame);
        ReleaseVulkanUiImages();
        _vulkanUiImageSources.Clear();
    }
}
