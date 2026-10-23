using XREngine.Components;
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

    private void CaptureWindowUiFrame(ulong renderFrameId)
    {
        _windowUiFrame.Clear();
        _windowUiFrameId = renderFrameId;
        for (int index = 0; index < Viewports.Count; index++)
        {
            XRViewport viewport = Viewports[index];
            if (!viewport.TryGetActiveScreenSpaceUserInterface(out var ui) ||
                ui is not UICanvasComponent canvas ||
                canvas.GetRenderedPublication(viewport, renderFrameId) is not { } publication)
                continue;
            ulong receiptId = canvas.GetRenderedPublicationReceiptId(viewport, renderFrameId);
            if (receiptId != 0UL)
                _windowUiFrame.Add((viewport, canvas, publication, receiptId));
        }
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
            _vulkanUiImagePackets.Clear();
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
            imagePackets.Clear();
            for (int index = 0; index < _windowUiFrame.Count; index++)
            {
                UiPublicationFrameEntry packet = _windowUiFrame[index];
                if (Renderer.TryAcceptPresentedUiPublicationReceipt(
                        packet.ReceiptId, imageIndex, outputGeneration, sourceFrameId))
                    imagePackets.Add(packet);
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
                imagePackets.Clear();
                _vulkanUiImageSources.Remove(imageIndex);
                PublishWindowUiFrame(imagePackets);
                return;
            }
            Renderer.InvalidatePresentedUiPublicationImage(imageIndex, outputGeneration);
            imagePackets.Clear();
            for (int index = 0; index < source.Count; index++)
            {
                UiPublicationFrameEntry packet = source[index];
                if (Renderer.TryAcceptReplayedUiPublicationReceipt(
                        packet.ReceiptId, sourceImageIndex, outputGeneration,
                        imageIndex, outputGeneration, sourceFrameId))
                    imagePackets.Add(packet);
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
            _vulkanUiImagePackets.Clear();
            _vulkanUiImageSources.Clear();
            _vulkanUiOutputGeneration = outputGeneration;
        }
        _vulkanUiImagePackets.Remove(imageIndex);
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
        _lastPresentedUiFrame.Clear();
        _lastPresentedUiFrame.AddRange(packets);
    }
}
