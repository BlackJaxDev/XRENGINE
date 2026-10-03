using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Rendering.WebGPU;

internal static partial class WebGpuImports
{
    [JSImport("configureSkinning", "xrengine.webgpu")]
    internal static partial void ConfigureSkinning(int session, int mesh, [JSMarshalAs<JSType.MemoryView>] Span<byte> packet);

    [JSImport("updateSkinning", "xrengine.webgpu")]
    internal static partial void UpdateSkinning(int session, int mesh, [JSMarshalAs<JSType.MemoryView>] Span<byte> palette,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> activeMorphs);

    [JSImport("releaseSkinning", "xrengine.webgpu")]
    internal static partial void ReleaseSkinning(int session, int mesh);
}
