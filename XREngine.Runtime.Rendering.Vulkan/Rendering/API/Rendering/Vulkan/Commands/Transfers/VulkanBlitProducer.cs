namespace XREngine.Rendering.Vulkan;

/// <summary>Freezes validated blit API inputs into a frame operation.</summary>
internal static class VulkanBlitProducer
{
    internal static BlitOp? Prepare(
        XRFrameBuffer? source,
        XRFrameBuffer? destination,
        int sourceX,
        int sourceY,
        uint sourceWidth,
        uint sourceHeight,
        int destinationX,
        int destinationY,
        uint destinationWidth,
        uint destinationHeight,
        EReadBufferMode readBufferMode,
        bool copyColor,
        bool copyDepth,
        bool copyStencil,
        bool linearFilter,
        int passIndex,
        in FrameOpContext context)
    {
        if (!copyColor && !copyDepth && !copyStencil ||
            source is null && destination is null ||
            sourceWidth == 0 || sourceHeight == 0 ||
            destinationWidth == 0 || destinationHeight == 0)
        {
            return null;
        }

        return new BlitOp(
            passIndex,
            source,
            destination,
            sourceX,
            sourceY,
            sourceWidth,
            sourceHeight,
            destinationX,
            destinationY,
            destinationWidth,
            destinationHeight,
            readBufferMode,
            copyColor,
            copyDepth,
            copyStencil,
            linearFilter,
            context);
    }
}
