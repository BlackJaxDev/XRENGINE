using XREngine.Data.Rendering;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

/// <summary>
/// Uploads decoded video frames to a Vulkan texture via staging buffers.
/// <para>
/// Each frame:
///   1. Resolve the backend texture handle for the target texture.
///   2. Delegate the upload to that handle, which:
///      - Writes the RGB24 texels to a synchronous frame-data staging slice in the
///        image's byte order. An RGB8 texture uses a four-byte image format, so each
///        texel receives an opaque alpha.
///      - Transitions the image to TransferDstOptimal.
///      - Issues vkCmdCopyBufferToImage from the staging slice to mip 0.
///      - Transitions the image to ShaderReadOnlyOptimal.
///      - Marks the texture as uploaded.
/// </para>
/// <para>
/// The caller uploads on the render thread while the frame loop prepares the video
/// material's draw. Each transfer step waits for its own one-shot submission, so the
/// frame that samples the texture sees the new contents.
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
            error = "The video texture has no published Vulkan wrapper, for example after a resize. Descriptor preparation publishes it when the video material draws.";
            return false;
        }

        // Upload via staging buffer → vkCmdCopyBufferToImage. A failed staging or
        // submission step throws; report it as an upload failure so that one video
        // cannot fail the render frame that prepares its draw.
        try
        {
            if (!vkTex.UploadVideoFrameData(memory.Span, w, h, out string? failureReason))
            {
                error = failureReason ?? "The Vulkan staging upload failed.";
                return false;
            }
        }
        catch (InvalidOperationException ex)
        {
            error = $"Vulkan staging upload failed: {ex.Message}";
            return false;
        }

        return true;
    }

    public void Dispose()
    {
    }
}
