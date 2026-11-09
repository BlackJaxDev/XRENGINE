namespace XREngine.Rendering.VideoStreaming;

public interface IVulkanVideoFrameTextureHandle
{
    /// <summary>
    /// Uploads one packed RGB24 video frame to mip 0 of the texture.
    /// </summary>
    /// <param name="pixelData">Packed RGB24 texels: <paramref name="width"/> x <paramref name="height"/> x 3 bytes.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="failureReason">The reason for a failed upload, or <c>null</c> when the upload succeeds.</param>
    /// <returns><c>true</c> when the texture holds the frame and is ready to sample.</returns>
    bool UploadVideoFrameData(ReadOnlySpan<byte> pixelData, uint width, uint height, out string? failureReason);
}

public interface IVulkanVideoFrameUploadContext
{
    IVulkanVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture);
}
