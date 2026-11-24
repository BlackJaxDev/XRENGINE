using XREngine.Data.Rendering;
using XREngine.Rendering.VideoStreaming.Interfaces;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.VideoStreaming;

/// <summary>
/// Uploads decoded video frames to a Vulkan texture via staging buffers.
/// <para>
/// Each frame:
///   1. Resolve the backend texture handle for the target texture. The context returns
///      no handle when the generation does not admit synchronous uploads.
///   2. Delegate the upload to that handle, which:
///      - Writes the RGB24 texels to a synchronous frame-data staging slice in the
///        image's byte order. An RGB8 texture uses a four-byte image format, so each
///        texel receives an opaque alpha.
///      - Records the barrier to TransferDstOptimal, vkCmdCopyBufferToImage from the
///        staging slice to mip 0, and the barrier to ShaderReadOnlyOptimal in one
///        synchronous command session.
///      - Marks the texture as uploaded.
/// </para>
/// <para>
/// The caller uploads on the render thread while the frame loop prepares the video
/// material's draw. The session waits for its submission, so the frame that samples the
/// texture sees the new contents. A failed resolve, staging step, allocation, or
/// submission becomes an upload error, so one video cannot fail the render frame.
/// </para>
/// </summary>
public sealed class VulkanVideoFrameGpuActions : IVideoFrameGpuActions
{
    /// <summary>The error when the context returns no handle and no reason.</summary>
    internal const string NoTextureHandleError = "The video texture has no Vulkan upload handle.";

    /// <summary>The error when the handle reports a failure without a reason.</summary>
    internal const string NoUploadReasonError = "The Vulkan staging upload failed.";

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

        // Wrapper publication, image allocation, and the synchronous submission can throw.
        // Report those failures as upload errors.
        try
        {
            IVulkanVideoFrameTextureHandle? vkTex = _context.ResolveTexture(xrTexture, out string? failureReason);
            if (vkTex is null)
            {
                error = failureReason ?? NoTextureHandleError;
                return false;
            }

            if (!vkTex.UploadVideoFrameData(memory.Span, w, h, out failureReason))
            {
                error = failureReason ?? NoUploadReasonError;
                return false;
            }
        }
        catch (InvalidOperationException ex)
        {
            error = $"The Vulkan video frame upload failed: {ex.Message}";
            return false;
        }
        catch (VulkanOutOfMemoryException ex)
        {
            error = $"The Vulkan video frame upload ran out of device memory: {ex.Message}";
            return false;
        }

        return true;
    }

    public void Dispose()
    {
    }
}
