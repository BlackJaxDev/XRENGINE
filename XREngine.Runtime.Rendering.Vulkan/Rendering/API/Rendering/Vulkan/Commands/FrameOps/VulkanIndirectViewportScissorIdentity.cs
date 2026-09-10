using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Exact scalar command bytes, including signed zero in viewport floats.</summary>
internal readonly record struct VulkanIndirectViewportScissorIdentity(
    uint X, uint Y, uint Width, uint Height, uint MinDepth, uint MaxDepth,
    int OffsetX, int OffsetY, uint ExtentWidth, uint ExtentHeight)
{
    internal static VulkanIndirectViewportScissorIdentity Capture(in Viewport viewport, in Rect2D scissor)
        => new(BitConverter.SingleToUInt32Bits(viewport.X), BitConverter.SingleToUInt32Bits(viewport.Y),
            BitConverter.SingleToUInt32Bits(viewport.Width), BitConverter.SingleToUInt32Bits(viewport.Height),
            BitConverter.SingleToUInt32Bits(viewport.MinDepth), BitConverter.SingleToUInt32Bits(viewport.MaxDepth),
            scissor.Offset.X, scissor.Offset.Y, scissor.Extent.Width, scissor.Extent.Height);
}
