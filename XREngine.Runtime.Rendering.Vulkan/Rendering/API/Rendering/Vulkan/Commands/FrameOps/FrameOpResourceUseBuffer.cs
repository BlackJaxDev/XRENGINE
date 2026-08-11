using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

[InlineArray(Capacity)]
internal struct FrameOpResourceUseBuffer
{
    // PostProcess.fs currently publishes camera/light buffers plus its sampled
    // images in one immutable descriptor snapshot. Keep the dependency packet
    // large enough for that supported program shape without allocating in the
    // per-operation hot path.
    internal const int Capacity = 64;

    private FrameOpResourceUse _element0;
}
