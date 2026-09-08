using Silk.NET.OpenGL;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private uint _layerBlitReadFramebuffer;
    private uint _layerBlitDrawFramebuffer;

    private static bool RequiresLayeredBlit(XRFrameBuffer? framebuffer)
    {
        if (framebuffer?.Targets is not { } targets)
            return false;
        foreach (var (target, _, _, layer) in targets)
            if (layer < 0 && target is XRTexture2DArray { Depth: > 1 } or XRTexture2DArrayView { NumLayers: > 1 })
                return true;
        return false;
    }

    /// <summary>
    /// Blits corresponding layers through reusable single-layer FBOs. OVR read
    /// framebuffers cannot be blitted, and ordinary layered blits copy layer zero only.
    /// The original attachment, read-buffer and draw-buffer state is preserved.
    /// </summary>
    private unsafe bool TryBlitLayeredFramebuffers(
        XRFrameBuffer? source, XRFrameBuffer? destination,
        int sourceX, int sourceY, uint sourceWidth, uint sourceHeight,
        int destinationX, int destinationY, uint destinationWidth, uint destinationHeight,
        EReadBufferMode readBuffer, EReadBufferMode? drawBuffer,
        ClearBufferMask mask, bool linear, out string failure)
    {
        failure = string.Empty;
        if (source?.Targets is not { Length: > 0 } || destination?.Targets is not { Length: > 0 })
        {
            failure = "A layered blit requires explicit source and destination attachments.";
            return false;
        }

        // Earlier operations already reported their debug messages. Do not attribute
        // a queued error from another operation to this copy's completion result.
        while (Api.GetError() != GLEnum.NoError) { }

        uint sourceId = GenericToAPI<GLFrameBuffer>(source)!.BindingId;
        uint destinationId = GenericToAPI<GLFrameBuffer>(destination)!.BindingId;
        int sourceLayers = GetBlitLayerCount(source, sourceId);
        int destinationLayers = GetBlitLayerCount(destination, destinationId);
        if (sourceLayers <= 0 || sourceLayers != destinationLayers)
        {
            failure = $"Layered blit view counts must agree (source={sourceLayers}, destination={destinationLayers}).";
            return false;
        }

        if (_layerBlitReadFramebuffer == 0)
            Api.CreateFramebuffers(1, out _layerBlitReadFramebuffer);
        if (_layerBlitDrawFramebuffer == 0)
            Api.CreateFramebuffers(1, out _layerBlitDrawFramebuffer);

        bool color = (mask & ClearBufferMask.ColorBufferBit) != 0;
        Span<GLEnum> drawBuffers = stackalloc GLEnum[32];
        int drawCount = 0;
        if (color && drawBuffer.HasValue)
            drawBuffers[drawCount++] = ToGLEnum(drawBuffer.Value);
        else if (color)
        {
            int maximumDrawBuffers = Api.GetInteger(GLEnum.MaxDrawBuffers);
            if (maximumDrawBuffers > drawBuffers.Length)
            {
                failure = "The layered blit draw-buffer count exceeds its bounded workspace.";
                return false;
            }
            int previousDrawFramebuffer = Api.GetInteger(GLEnum.DrawFramebufferBinding);
            try
            {
                // Query actual GL state: an earlier explicit draw-buffer operation
                // may differ from the XR framebuffer's initial attachment list.
                Api.BindFramebuffer(GLEnum.DrawFramebuffer, destinationId);
                for (int i = 0; i < maximumDrawBuffers; i++)
                    drawBuffers[drawCount++] = (GLEnum)Api.GetInteger((GLEnum)((uint)GLEnum.DrawBuffer0 + (uint)i));
            }
            finally { Api.BindFramebuffer(GLEnum.DrawFramebuffer, (uint)previousDrawFramebuffer); }
        }
        if (color && (readBuffer == EReadBufferMode.None || !drawBuffers[..drawCount].ContainsAnyExcept(GLEnum.None)))
        {
            failure = "A color blit requires an enabled read buffer and at least one enabled draw buffer.";
            return false;
        }
        if (drawCount == 0)
            drawBuffers[drawCount++] = GLEnum.None;

        try
        {
            for (int layer = 0; layer < sourceLayers; layer++)
            {
                ConfigureBlitLayer(source, sourceId, _layerBlitReadFramebuffer, layer);
                ConfigureBlitLayer(destination, destinationId, _layerBlitDrawFramebuffer, layer);
                Api.NamedFramebufferReadBuffer(_layerBlitReadFramebuffer, color ? ToGLEnum(readBuffer) : GLEnum.None);
                Api.NamedFramebufferReadBuffer(_layerBlitDrawFramebuffer, GLEnum.None);
                Api.NamedFramebufferDrawBuffer(_layerBlitReadFramebuffer, GLEnum.None);
                fixed (GLEnum* buffers = drawBuffers)
                    Api.NamedFramebufferDrawBuffers(_layerBlitDrawFramebuffer, (uint)drawCount, buffers);
                GLEnum sourceStatus = Api.CheckNamedFramebufferStatus(_layerBlitReadFramebuffer, FramebufferTarget.ReadFramebuffer);
                GLEnum destinationStatus = Api.CheckNamedFramebufferStatus(_layerBlitDrawFramebuffer, FramebufferTarget.DrawFramebuffer);
                if (sourceStatus != GLEnum.FramebufferComplete || destinationStatus != GLEnum.FramebufferComplete)
                {
                    failure = $"Layer {layer} blit attachments are incomplete (source={sourceStatus}, destination={destinationStatus}).";
                    return false;
                }
                GLEnum setupError = Api.GetError();
                if (setupError != GLEnum.NoError)
                {
                    failure = $"Layer {layer} framebuffer setup failed with {setupError}.";
                    return false;
                }
                Api.BlitNamedFramebuffer(_layerBlitReadFramebuffer, _layerBlitDrawFramebuffer,
                    sourceX, sourceY, sourceX + (int)sourceWidth, sourceY + (int)sourceHeight,
                    destinationX, destinationY, destinationX + (int)destinationWidth, destinationY + (int)destinationHeight,
                    mask, linear ? BlitFramebufferFilter.Linear : BlitFramebufferFilter.Nearest);
                GLEnum error = Api.GetError();
                if (error != GLEnum.NoError)
                {
                    failure = $"Layer {layer} framebuffer blit failed with {error}.";
                    return false;
                }
            }
            return true;
        }
        finally
        {
            // Do not keep texture references in reusable FBOs after resource retirement.
            DetachBlitLayer(source, _layerBlitReadFramebuffer);
            DetachBlitLayer(destination, _layerBlitDrawFramebuffer);
        }
    }

    private int GetBlitLayerCount(XRFrameBuffer framebuffer, uint framebufferId)
    {
        int count = -1;
        foreach (var (target, point, mip, explicitLayer) in framebuffer.Targets!)
        {
            int layers = 1;
            if (target is XRTexture texture && explicitLayer < 0)
            {
                if (texture is XRTexture2DArray array)
                    layers = checked((int)array.Depth);
                else if (texture is XRTexture2DArrayView view)
                    layers = checked((int)view.NumLayers);
                if (OVRMultiView is not null)
                {
                    Api.GetNamedFramebufferAttachmentParameter(framebufferId, ToGLEnum(point), (GLEnum)0x9630, out int views);
                    if (views > 0)
                        layers = views;
                }
            }
            if (count >= 0 && count != layers)
                return 0;
            count = layers;
        }
        return count;
    }

    private void ConfigureBlitLayer(XRFrameBuffer framebuffer, uint originalId, uint temporaryId, int layer)
    {
        foreach (var (target, point, mip, explicitLayer) in framebuffer.Targets!)
        {
            GLEnum attachment = ToGLEnum(point);
            Api.GetNamedFramebufferAttachmentParameter(originalId, attachment, GLEnum.FramebufferAttachmentObjectName, out int objectId);
            if (target is XRRenderBuffer)
            {
                Api.NamedFramebufferRenderbuffer(temporaryId, attachment, GLEnum.Renderbuffer, (uint)objectId);
                continue;
            }
            if (target is XRTexture2DArray or XRTexture2DArrayView { NumLayers: > 1 } or XRTexture2DView { Array: true })
            {
                int baseLayer = 0;
                if (OVRMultiView is not null && explicitLayer < 0)
                    Api.GetNamedFramebufferAttachmentParameter(originalId, attachment, (GLEnum)0x9632, out baseLayer);
                Api.NamedFramebufferTextureLayer(temporaryId, attachment, (uint)objectId, mip,
                    explicitLayer >= 0 ? explicitLayer : baseLayer + layer);
            }
            else
                Api.NamedFramebufferTexture(temporaryId, attachment, (uint)objectId, mip);
        }
    }

    private void DetachBlitLayer(XRFrameBuffer framebuffer, uint temporaryId)
    {
        foreach (var (_, point, _, _) in framebuffer.Targets!)
            Api.NamedFramebufferTexture(temporaryId, ToGLEnum(point), 0, 0);
    }

    private void DisposeLayeredBlitFramebuffers(bool orphanHandles)
    {
        if (!orphanHandles)
        {
            if (_layerBlitReadFramebuffer != 0)
                Api.DeleteFramebuffer(_layerBlitReadFramebuffer);
            if (_layerBlitDrawFramebuffer != 0)
                Api.DeleteFramebuffer(_layerBlitDrawFramebuffer);
        }
        _layerBlitReadFramebuffer = _layerBlitDrawFramebuffer = 0;
    }
}
