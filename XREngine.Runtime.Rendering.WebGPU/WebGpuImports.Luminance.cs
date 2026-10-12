using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Rendering.WebGPU;

internal static partial class WebGpuImports
{
    [JSImport("prepareLuminance", "xrengine.webgpu")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    internal static partial Task PrepareLuminanceAsync(int session, string source);

    [JSImport("prepareLuminance2D", "xrengine.webgpu")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    internal static partial Task PrepareLuminance2DAsync(int session, string source);

    [JSImport("prepareLuminanceMipmap", "xrengine.webgpu")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    internal static partial Task PrepareLuminanceMipmapAsync(int session, string source);

    [JSImport("beginTextureLuminance", "xrengine.webgpu")]
    internal static partial int BeginTextureLuminance(int session, int handle, int mip, int width, int height,
        int layers, float redWeight, float greenWeight, float blueWeight, int firstGeneratedMip,
        int lastGeneratedMip, bool detailPreserving, bool strictEncodedSrgb);

    [JSImport("beginCanvasLuminance", "xrengine.webgpu")]
    internal static partial int BeginCanvasLuminance(int session, int generation, int x, int y, int width, int height,
        float redWeight, float greenWeight, float blueWeight);
}
