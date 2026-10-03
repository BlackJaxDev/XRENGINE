using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Rendering.WebGPU;

internal static partial class WebGpuImports
{
    [JSImport("createCookedTexture", "xrengine.webgpu")]
    internal static partial int CreateCookedTexture(int session, string description,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);
}
