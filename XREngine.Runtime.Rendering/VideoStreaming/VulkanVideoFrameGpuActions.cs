using XREngine.Data.Rendering;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

/// <summary>
/// Uploads decoded video frames to a Vulkan texture via staging buffers.
/// <para>
/// Each frame:
///   1. Obtain the <see cref="VulkanRenderer.VkTexture2D"/> wrapper for the target texture.
///   2. Delegate to <see cref="VulkanRenderer.VkTexture2D.UploadVideoFrameData"/> which:
///      - Allocates a host-visible staging buffer and memcpy's the pixel data.
///      - Transitions the image to TransferDstOptimal.
///      - Issues vkCmdCopyBufferToImage from the staging buffer to mip 0.
///      - Transitions the image to ShaderReadOnlyOptimal.
///      - Releases the staging buffer.
/// </para>
/// </summary>
public sealed class VulkanVideoFrameGpuActions : IVideoFrameGpuActions
{
    private readonly IVulkanVideoFrameUploadContext _context;

    public VulkanVideoFrameGpuActions(IVulkanVideoFrameUploadContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public bool UploadVideoFrame(DecodedVideoFrame frame, object? targetTexture, out string? error)
    {
        error = null;

        if (frame.PixelFormat != VideoPixelFormat.Rgb24)
        {
            error = $"Vulkan uploader only supports {VideoPixelFormat.Rgb24} frames; got {frame.PixelFormat}.";
            return false;
        }

        if (targetTexture is not XRTexture2D xrTexture)
        {
            error = "Vulkan video upload requires an XRTexture2D target.";
            return false;
        }

        ReadOnlyMemory<byte> memory = frame.PackedData;
        if (memory.IsEmpty)
        {
            error = "Decoded video frame contains no pixel data.";
            return false;
        }

        uint w = (uint)frame.Width;
        uint h = (uint)frame.Height;

        // Obtain the Vulkan texture wrapper.
        IVulkanVideoFrameTextureHandle? vkTex = _context.ResolveTexture(xrTexture);
        if (vkTex is null)
        {
            error = "Failed to obtain VkTexture2D wrapper.";
            return false;
        }

        // Upload via staging buffer → vkCmdCopyBufferToImage.
        if (!vkTex.UploadVideoFrameData(memory.Span, w, h))
        {
            error = "Vulkan staging buffer upload failed.";
            return false;
        }

        return true;
    }

    public void Dispose()
    {
    }
}
