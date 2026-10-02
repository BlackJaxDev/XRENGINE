using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Rendering.WebGPU;

internal static partial class WebGpuImports
{
    [JSImport("createBuffer", "xrengine.webgpu")]
    internal static partial int CreateBuffer(int session, int size, int usage, string label);

    [JSImport("writeBuffer", "xrengine.webgpu")]
    internal static partial void WriteBuffer(int session, int handle, int offset,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);

    [JSImport("copyBuffer", "xrengine.webgpu")]
    internal static partial void CopyBuffer(int session, int source, int sourceOffset,
        int destination, int destinationOffset, int size);

    [JSImport("createTextureResource", "xrengine.webgpu")]
    internal static partial int CreateTextureResource(int session, int width, int height,
        int mipLevelCount, int sampleCount, string format, int usage, string label, int arrayLayerCount);

    [JSImport("uploadTextureMip", "xrengine.webgpu")]
    internal static partial void UploadTextureMip(int session, int handle, int mip,
        int x, int y, int width, int height, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes, int layer);

    [JSImport("copyTextureSubresource", "xrengine.webgpu")]
    internal static partial void CopyTextureSubresource(int session, int source, int destination,
        int sourceMip, int destinationMip, int destinationLayer, int width, int height);

    [JSImport("createTextureView", "xrengine.webgpu")]
    internal static partial int CreateTextureView(int session, int texture, int baseMip, int mipCount, string aspect,
        string label, int baseArrayLayer, int arrayLayerCount, string dimension);

    [JSImport("createSampler", "xrengine.webgpu")]
    internal static partial int CreateSampler(int session, string addressU, string addressV, string addressW,
        string minFilter, string magFilter, string mipmapFilter, string label, float lodMaxClamp, int maxAnisotropy,
        float lodMinClamp, string compare);
}
