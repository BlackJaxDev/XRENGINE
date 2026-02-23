using System.Linq;
using XREngine.Data.Rendering;
using XREngine.Rendering.VideoStreaming.Interfaces;
using XREngine.Rendering.Vulkan;

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
internal sealed class VulkanVideoFrameGpuActions : IVideoFrameGpuActions
{
    public bool TryPrepareOutput(XRMaterialFrameBuffer frameBuffer, XRMaterial? material, out uint framebufferId, out string? error)
    {
        error = null;
        framebufferId = 0;

        frameBuffer.Material = material;
        frameBuffer.Generate();

        if (frameBuffer.APIWrappers.OfType<VulkanRenderer.VkFrameBuffer>().FirstOrDefault() is not VulkanRenderer.VkFrameBuffer vkFbo)
        {
            error = "Unable to locate Vulkan framebuffer wrapper for streaming video output.";
            return false;
        }

        vkFbo.Generate();
        return true;
    }

    public bool UploadVideoFrame(DecodedVideoFrame frame, XRTexture2D? targetTexture, out string? error)
    {
        error = null;

        if (frame.PixelFormat != VideoPixelFormat.Rgb24)
        {
            error = $"Vulkan uploader only supports {VideoPixelFormat.Rgb24} frames; got {frame.PixelFormat}.";
            return false;
        }

        if (targetTexture is null)
        {
            error = "No target texture is available for Vulkan upload.";
            return false;
        }

        if (AbstractRenderer.Current is not VulkanRenderer renderer)
        {
            error = "No active Vulkan renderer.";
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
        var vkTex = renderer.GenericToAPI<VulkanRenderer.VkTexture2D>(targetTexture);
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

    public void Present(IMediaStreamSession session, uint framebufferId)
    {
        session.SetTargetFramebuffer(framebufferId);
        session.Present();
    }

    public void Dispose()
    {
    }
}
