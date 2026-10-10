using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    public override void Blit(XRFrameBuffer? inFBO, XRFrameBuffer? outFBO,
        int inX, int inY, uint inW, uint inH, int outX, int outY, uint outW, uint outH,
        EReadBufferMode readBufferMode, bool colorBit, bool depthBit, bool stencilBit, bool linearFilter)
        => RecordColorResolve(inFBO, outFBO, inX, inY, inW, inH, outX, outY, outW, outH,
            readBufferMode, null, colorBit, depthBit, stencilBit, linearFilter);

    public override void BlitWithDrawBuffer(XRFrameBuffer? inFBO, XRFrameBuffer? outFBO,
        uint inW, uint inH, uint outW, uint outH, EReadBufferMode readBufferMode,
        EReadBufferMode drawBufferMode, bool colorBit, bool depthBit, bool stencilBit, bool linearFilter)
        => RecordColorResolve(inFBO, outFBO, 0, 0, inW, inH, 0, 0, outW, outH,
            readBufferMode, drawBufferMode, colorBit, depthBit, stencilBit, linearFilter);

    public override FrameBufferBlitSubmission TryBlitFBOToFBO(XRFrameBuffer inFBO, XRFrameBuffer outFBO,
        EReadBufferMode readBufferMode, bool colorBit, bool depthBit, bool stencilBit, bool linearFilter)
        => RecordColorResolve(inFBO, outFBO, 0, 0, 0, 0, 0, 0, 0, 0,
            readBufferMode, null, colorBit, depthBit, stencilBit, linearFilter, useAttachmentExtents: true);

    public override FrameBufferBlitSubmission TryBlitFBOToFBOSingleAttachment(XRFrameBuffer inFBO,
        XRFrameBuffer outFBO, EReadBufferMode readBufferMode, EReadBufferMode drawBufferMode, bool linearFilter)
        => RecordColorResolve(inFBO, outFBO, 0, 0, 0, 0, 0, 0, 0, 0,
            readBufferMode, drawBufferMode, colorBit: true, depthBit: false, stencilBit: false, linearFilter,
            useAttachmentExtents: true);

    private FrameBufferBlitSubmission RecordColorResolve(XRFrameBuffer? source, XRFrameBuffer? destination,
        int sourceX, int sourceY, uint sourceWidth, uint sourceHeight,
        int destinationX, int destinationY, uint destinationWidth, uint destinationHeight,
        EReadBufferMode readBuffer, EReadBufferMode? drawBuffer,
        bool colorBit, bool depthBit, bool stencilBit, bool linearFilter, bool useAttachmentExtents = false)
    {
        RequireReady();
        if (source is null || destination is null || ReferenceEquals(source, destination) ||
            !colorBit || depthBit || stencilBit || linearFilter || sourceX != 0 || sourceY != 0 ||
            destinationX != 0 || destinationY != 0 || !useAttachmentExtents && (sourceWidth == 0 || sourceHeight == 0 ||
            sourceWidth != destinationWidth || sourceHeight != destinationHeight))
            throw UnsupportedEngineOperation("ColorResolve", "only distinct engine framebuffers and full-extent, unfiltered color-only MSAA resolves are admitted; canvas, depth/stencil, offset, scaled and unrelated blits are unsupported");
        if (!_engineRecording || CurrentFrameOutput is null)
            throw new InvalidOperationException("WebGPU.ColorResolve.OutsideFrame: resolves require the current output's active engine frame.");
        int sourceSlot = ResolveColorSlot(readBuffer);
        int? requestedDestinationSlot = drawBuffer is { } selected ? ResolveColorSlot(selected) : null;
        WebGpuFrameBuffer sourceApi = (WebGpuFrameBuffer)GetOrCreateAPIRenderObject(source, generateNow: true)!;
        WebGpuFrameBuffer destinationApi = (WebGpuFrameBuffer)GetOrCreateAPIRenderObject(destination, generateNow: true)!;
        sourceApi.EnsureCurrent();
        destinationApi.EnsureCurrent();
        if (useAttachmentExtents)
        {
            // XRFrameBuffer exposes the backing texture's base size. Whole-target
            // resolves instead cover the actual attached mip subresource.
            sourceWidth = sourceApi.Width;
            sourceHeight = sourceApi.Height;
            destinationWidth = destinationApi.Width;
            destinationHeight = destinationApi.Height;
        }
        if (sourceApi.Width != sourceWidth || sourceApi.Height != sourceHeight ||
            destinationApi.Width != destinationWidth || destinationApi.Height != destinationHeight)
            throw UnsupportedEngineOperation("ColorResolve", "both rectangles must cover their complete attachment subresources");
        if (_engineCroppingEnabled && (_engineCropArea is not { } crop || crop.X != 0 || crop.Y != 0 ||
            crop.Width != destinationWidth || crop.Height != destinationHeight))
            throw UnsupportedEngineOperation("ColorResolve", "scissored color resolves are unsupported");
        int destinationSlot = requestedDestinationSlot ?? destinationApi.GetSingleDrawColorSlot();
        int command = sourceApi.GetColorResolveCommand(destinationApi, sourceSlot, destinationSlot);
        AbstractRenderAPIObject sourceOwner = sourceApi.GetColorAttachment(sourceSlot).Owner;
        AbstractRenderAPIObject destinationOwner = destinationApi.GetColorAttachment(destinationSlot).Owner;
        if (sourceOwner is not IWebGpuProducedTexture producer || destinationOwner is not IWebGpuProducedTexture output)
            throw UnsupportedEngineOperation("ColorResolve", "both color attachments must track engine GPU production");
        if (!producer.WasProducedInFrame(_engineFrameSequence) && !producer.HasCommittedProduction)
        {
            // A pending asynchronous producer invalidates the whole frame. Never mark
            // its unexecuted resolve destination as ready or submit a partial result.
            if (_engineDrawPending)
                return FrameBufferBlitSubmission.Rejected("WebGPU.ColorResolve.ProducerPending");
            throw new InvalidOperationException("WebGPU.ColorResolve.ProducerRequired: record or commit a producing pass before resolving its samples.");
        }

        RecordEngineCommands(command, []);
        WebGpuFrameBuffer.MarkAttachmentRecorded(sourceOwner);
        output.MarkProduced();
        return FrameBufferBlitSubmission.Enqueued();
    }

    private static int ResolveColorSlot(EReadBufferMode mode)
        => mode is >= EReadBufferMode.ColorAttachment0 and <= EReadBufferMode.ColorAttachment7
            ? (int)mode - (int)EReadBufferMode.ColorAttachment0
            : throw UnsupportedEngineOperation("ColorResolve", "the read/draw mode must select a color attachment from zero through seven");
}
