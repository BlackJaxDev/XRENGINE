using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Public/API producer and frame-state translation boundary for Vulkan blits.
/// Native resource resolution and transitions are owned by the command runtime.
/// </summary>
internal sealed partial class VulkanFrameLoop
{
    internal void Blit(
        XRFrameBuffer? inFBO,
        XRFrameBuffer? outFBO,
        int inX,
        int inY,
        uint inW,
        uint inH,
        int outX,
        int outY,
        uint outW,
        uint outH,
        EReadBufferMode readBufferMode,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter)
    {
        FrameOpContext context = CaptureFrameOpContextForCurrentPipelineScope();
        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        BlitOp? operation = VulkanBlitProducer.Prepare(
            inFBO,
            outFBO,
            inX,
            inY,
            inW,
            inH,
            outX,
            outY,
            outW,
            outH,
            readBufferMode,
            colorBit,
            depthBit,
            stencilBit,
            linearFilter,
            VulkanCommandRuntime.EnsureValidPassIndex(passIndex, "Blit", context.PassMetadata),
            context);
        if (operation is not null)
            EnqueueFrameOp(operation);
    }

    internal void BlitWithDrawBuffer(
        XRFrameBuffer? inFBO,
        XRFrameBuffer? outFBO,
        uint inW,
        uint inH,
        uint outW,
        uint outH,
        EReadBufferMode readBufferMode,
        EReadBufferMode drawBufferMode,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter)
        => Blit(
            inFBO,
            outFBO,
            0,
            0,
            inW,
            inH,
            0,
            0,
            outW,
            outH,
            readBufferMode,
            colorBit,
            depthBit,
            stencilBit,
            linearFilter);
}
