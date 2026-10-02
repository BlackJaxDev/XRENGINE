using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Rendering.WebGPU;

internal static partial class WebGpuImports
{
    [JSImport("beginBufferReadback", "xrengine.webgpu")]
    internal static partial int BeginBufferReadback(int session, int handle, int offset, int byteLength);

    [JSImport("beginTextureReadback", "xrengine.webgpu")]
    internal static partial int BeginTextureReadback(int session, int handle, int mipLevel, int x, int y, int width, int height);

    [JSImport("beginCanvasReadback", "xrengine.webgpu")]
    internal static partial int BeginCanvasReadback(int session, int generation, int x, int y, int width, int height);

    [JSImport("beginCompletion", "xrengine.webgpu")]
    internal static partial int BeginCompletion(int session);

    [JSImport("waitReadback", "xrengine.webgpu")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    internal static partial Task WaitReadbackAsync(int session, int ticket);

    [JSImport("copyReadback", "xrengine.webgpu")]
    internal static partial void CopyReadback(int session, int ticket, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

    [JSImport("releaseReadback", "xrengine.webgpu")]
    internal static partial void ReleaseReadback(int session, int ticket);
}
