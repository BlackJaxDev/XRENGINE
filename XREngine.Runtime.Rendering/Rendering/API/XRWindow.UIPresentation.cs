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
        ReleaseUiFrame(_windowUiFrame);
        ReleaseUiFrame(_lastPresentedUiFrame);
        ReleaseVulkanUiImages();
        _vulkanUiImageSources.Clear();
    }
}
