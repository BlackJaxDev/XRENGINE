using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Rendering.WebGPU;

/// <summary>Generated synchronous imports into the module-owned JavaScript executor.</summary>
internal static partial class WebGpuImports
{
    [JSImport("createMesh", "xrengine.webgpu")]
    internal static partial int CreateMesh(int session, [JSMarshalAs<JSType.MemoryView>] Span<byte> vertices,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> indices);

    [JSImport("createTexture", "xrengine.webgpu")]
    internal static partial int CreateTexture(int session, int width, int height,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> rgba);

    [JSImport("createMaterial", "xrengine.webgpu")]
    internal static partial int CreateMaterial(int session, int texture, float r, float g, float b, float a);

    [JSImport("destroyResource", "xrengine.webgpu")]
    internal static partial void DestroyResource(int session, int handle);

    [JSImport("submitPacket", "xrengine.webgpu")]
    internal static partial void SubmitPacket(int session, [JSMarshalAs<JSType.MemoryView>] Span<byte> packet);

    [JSImport("disposeRenderer", "xrengine.webgpu")]
    internal static partial void DisposeRenderer(int session);
}
