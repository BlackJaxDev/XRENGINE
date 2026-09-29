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
        int mipLevelCount, int sampleCount, string format, int usage, string label);

    [JSImport("uploadTextureMip", "xrengine.webgpu")]
    internal static partial void UploadTextureMip(int session, int handle, int mip,
        int x, int y, int width, int height, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);

    [JSImport("createTextureView", "xrengine.webgpu")]
    internal static partial int CreateTextureView(int session, int texture, int baseMip, int mipCount, string aspect, string label);

    [JSImport("createSampler", "xrengine.webgpu")]
    internal static partial int CreateSampler(int session, string addressU, string addressV,
        string minFilter, string magFilter, string mipmapFilter, string label, float lodMaxClamp, int maxAnisotropy);
}
