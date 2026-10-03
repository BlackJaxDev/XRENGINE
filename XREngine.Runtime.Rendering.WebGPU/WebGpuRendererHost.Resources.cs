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

    /// <summary>Synchronously copies borrowed immutable publication bytes without retaining or modifying their source.</summary>
    public void WriteBuffer(int handle, int offset, ReadOnlySpan<byte> bytes)
    {
        // The JavaScript MemoryView marshaller accepts Span rather than ReadOnlySpan.
        // The executor only copies this borrow into its staging bytes during the import;
        // neither managed nor JavaScript code writes through or retains the source view.
        WriteBuffer(handle, offset, System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(bytes), bytes.Length));
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
        int qualityLimit = RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.MaxTextureDimension;
        if (qualityLimit > 0 && (description.Width > qualityLimit || description.Height > qualityLimit))
            throw new NotSupportedException($"WebGPU.Quality.TextureDimensionExceeded: texture '{description.Label}' size {description.Width}x{description.Height} exceeds the selected browser texture limit {qualityLimit}; authored textures are not resized implicitly.");
        return Track(WebGpuImports.CreateTextureResource(_session, description.Width, description.Height,
            description.MipLevelCount, description.SampleCount, description.Format, (int)description.Usage,
            description.Label, description.ArrayLayerCount));
    }

    public void UploadTextureMip(int handle, int mip, int x, int y, int width, int height, Span<byte> bytes)
    {
        RequireOwnedResource(handle);
        WebGpuImports.UploadTextureMip(_session, handle, mip, x, y, width, height, bytes, 0);
    }

    public void UploadTextureLayerMip(int handle, int mip, int layer, int width, int height, Span<byte> bytes)
    {
        RequireOwnedResource(handle);
        WebGpuImports.UploadTextureMip(_session, handle, mip, 0, 0, width, height, bytes, layer);
    }

    public void CopyTextureSubresource(int source, int destination, int sourceMip, int destinationMip,
        int destinationLayer, int width, int height)
    {
        RequireOwnedResource(source);
        RequireOwnedResource(destination);
        WebGpuImports.CopyTextureSubresource(_session, source, destination, sourceMip, destinationMip,
            destinationLayer, width, height);
    }

    public int CreateTextureView(BrowserTextureViewDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        RequireOwnedResource(description.TextureHandle);
        return Track(WebGpuImports.CreateTextureView(_session, description.TextureHandle,
            description.BaseMip, description.MipCount, description.Aspect, description.Label,
            description.BaseArrayLayer, description.ArrayLayerCount, description.Dimension));
    }

    public int CreateSampler(BrowserSamplerDescription description)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(description);
        return Track(WebGpuImports.CreateSampler(_session, description.AddressU, description.AddressV, description.AddressW,
            description.MinFilter, description.MagFilter, description.MipmapFilter, description.Label,
            description.LodMaxClamp, description.MaxAnisotropy, description.LodMinClamp, description.Compare ?? ""));
    }
}
