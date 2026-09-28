namespace XREngine.Rendering;

/// <summary>Device-owned buffers and image subresources without native GPU handles or mapping pointers.</summary>
public interface IBrowserGpuResourceCapability
{
    int CreateBuffer(BrowserBufferDescription description);
    void WriteBuffer(int handle, int offset, Span<byte> bytes);
    void CopyBuffer(int source, int sourceOffset, int destination, int destinationOffset, int size);
    int CreateTexture(BrowserTextureDescription description);
    void UploadTextureMip(int handle, int mip, int x, int y, int width, int height, Span<byte> bytes);
    int CreateTextureView(BrowserTextureViewDescription description);
    int CreateSampler(BrowserSamplerDescription description);
}
