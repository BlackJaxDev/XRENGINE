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
        // Direct control writes have no managed descriptor/range owner. They must
        // not overtake or poison a queued engine-owned mutation of this buffer.
        if (_engineRecording || HasUnsubmittedEngineBufferUpload(handle) || HasPendingEnginePreparation(handle))
            throw new NotSupportedException("WebGPU.Buffer.PendingWriteUnsupported: a direct buffer write cannot overtake an active engine frame or unsubmitted mutation; use its owned buffer upload path or wait for frame acceptance.");
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
        if (_engineRecording || HasUnsubmittedEngineBufferUpload(source) || HasUnsubmittedEngineBufferUpload(destination) ||
            HasPendingEnginePreparation(source) || HasPendingEnginePreparation(destination))
            throw new NotSupportedException("WebGPU.Buffer.PendingCopyUnsupported: a standalone buffer copy cannot overtake an active engine frame or unsubmitted mutations; record a retained buffer-copy command in the engine frame.");
        WebGpuImports.CopyBuffer(_session, source, sourceOffset, destination, destinationOffset, size);
    }

    public int CreateTexture(BrowserTextureDescription description)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(description);
        ValidateTextureQuality(description);
        return Track(WebGpuImports.CreateTextureResource(_session, description.Width, description.Height,
            description.MipLevelCount, description.SampleCount, description.Format, (int)description.Usage,
            description.Label, description.ArrayLayerCount, description.AllowSrgbView));
    }

    private static void ValidateTextureQuality(BrowserTextureDescription description)
    {
        int qualityLimit = RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.MaxTextureDimension;
        if (qualityLimit > 0 && (description.Width > qualityLimit || description.Height > qualityLimit))
            throw new NotSupportedException($"WebGPU.Quality.TextureDimensionExceeded: texture '{description.Label}' size {description.Width}x{description.Height} exceeds the selected browser texture limit {qualityLimit}; authored textures are not resized implicitly.");
    }

    public void UploadTextureMip(int handle, int mip, int x, int y, int width, int height, Span<byte> bytes)
    {
        RequireImmediateTextureWrite(handle);
        WebGpuImports.UploadTextureMip(_session, handle, mip, x, y, width, height, bytes, 0);
    }

    public void UploadTextureLayerMip(int handle, int mip, int layer, int width, int height, Span<byte> bytes)
    {
        RequireImmediateTextureWrite(handle);
        WebGpuImports.UploadTextureMip(_session, handle, mip, 0, 0, width, height, bytes, layer);
    }

    private void RequireImmediateTextureWrite(int handle)
    {
        RequireOwnedResource(handle);
        if (_engineRecording || HasPendingEnginePreparation(handle) || HasPendingEngineTextureCopySource(handle))
            throw new NotSupportedException("WebGPU.Texture.PendingWriteUnsupported: a standalone texture write cannot overtake an engine frame or retained texture transfer; use the owned texture update path or wait for acceptance.");
    }

    internal void StageEngineTextureMip(int handle, int mip, int x, int y, int width, int height, Span<byte> bytes)
    {
        RequireOwnedResource(handle);
        StageEngineTextureUpload(handle, mip, 0, x, y, width, height, bytes);
    }

    internal void StageEngineTextureLayerMip(int handle, int mip, int layer, int width, int height, Span<byte> bytes)
    {
        RequireOwnedResource(handle);
        StageEngineTextureUpload(handle, mip, layer, 0, 0, width, height, bytes);
    }

    public void CopyTextureSubresource(int source, int destination, int sourceMip, int destinationMip,
        int destinationLayer, int width, int height)
    {
        RequireOwnedResource(source);
        RequireImmediateTextureWrite(destination);
        if (HasPendingEnginePreparation(source))
            throw new NotSupportedException("WebGPU.Texture.PendingCopyUnsupported: a standalone copy cannot read an unsubmitted texture transfer.");
        WebGpuImports.CopyTextureSubresource(_session, source, destination, sourceMip, destinationMip,
            destinationLayer, width, height);
    }

    internal void StageEngineTextureSubresourceCopy(int source, int destination, int sourceMip, int destinationMip,
        int destinationLayer, int width, int height)
    {
        RequireOwnedResource(source);
        RequireOwnedResource(destination);
        StageEngineTextureCopy(source, destination, sourceMip, destinationMip, destinationLayer, width, height);
    }

    public int CreateTextureView(BrowserTextureViewDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        RequireOwnedResource(description.TextureHandle);
        return Track(WebGpuImports.CreateTextureView(_session, description.TextureHandle,
            description.BaseMip, description.MipCount, description.Aspect, description.Label,
            description.BaseArrayLayer, description.ArrayLayerCount, description.Dimension, description.Format));
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
