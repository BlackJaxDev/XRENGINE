using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Rendering.WebGPU;

internal static partial class WebGpuImports
{
    [JSImport("prepareLuminance", "xrengine.webgpu")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    internal static partial Task PrepareLuminanceAsync(int session, string source);

    [JSImport("beginTextureLuminance", "xrengine.webgpu")]
    internal static partial int BeginTextureLuminance(int session, int handle, int mip, int width, int height,
        int layers, float redWeight, float greenWeight, float blueWeight);

    [JSImport("beginCanvasLuminance", "xrengine.webgpu")]
    internal static partial int BeginCanvasLuminance(int session, int generation, int x, int y, int width, int height,
        float redWeight, float greenWeight, float blueWeight);
}
