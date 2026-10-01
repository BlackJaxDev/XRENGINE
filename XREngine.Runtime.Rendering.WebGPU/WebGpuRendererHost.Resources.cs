namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost : IBrowserGpuResourceCapability
{
    private void RequireOwnedResource(int handle)
    {
        RequireReady();
        if (!_resources.Contains(handle))
            throw new InvalidOperationException("Resource does not belong to this WebGPU renderer.");
    }

    public int CreateBuffer(BrowserBufferDescription description)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(description);
        return Track(WebGpuImports.CreateBuffer(_session, description.Size, (int)description.Usage, description.Label));
    }

    public void WriteBuffer(int handle, int offset, Span<byte> bytes)
    {
        RequireOwnedResource(handle);
        WebGpuImports.WriteBuffer(_session, handle, offset, bytes);
    }

    public void CopyBuffer(int source, int sourceOffset, int destination, int destinationOffset, int size)
    {
        RequireOwnedResource(source);
        RequireOwnedResource(destination);
        WebGpuImports.CopyBuffer(_session, source, sourceOffset, destination, destinationOffset, size);
    }

    public int CreateTexture(BrowserTextureDescription description)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(description);
        return Track(WebGpuImports.CreateTextureResource(_session, description.Width, description.Height,
            description.MipLevelCount, description.SampleCount, description.Format, (int)description.Usage, description.Label));
    }

    public void UploadTextureMip(int handle, int mip, int x, int y, int width, int height, Span<byte> bytes)
    {
        RequireOwnedResource(handle);
        WebGpuImports.UploadTextureMip(_session, handle, mip, x, y, width, height, bytes);
    }

    public int CreateTextureView(BrowserTextureViewDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        RequireOwnedResource(description.TextureHandle);
        return Track(WebGpuImports.CreateTextureView(_session, description.TextureHandle,
            description.BaseMip, description.MipCount, description.Aspect, description.Label));
    }

    public int CreateSampler(BrowserSamplerDescription description)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(description);
        return Track(WebGpuImports.CreateSampler(_session, description.AddressU, description.AddressV,
            description.MinFilter, description.MagFilter, description.MipmapFilter, description.Label,
            description.LodMaxClamp, description.MaxAnisotropy, description.LodMinClamp));
    }
}
