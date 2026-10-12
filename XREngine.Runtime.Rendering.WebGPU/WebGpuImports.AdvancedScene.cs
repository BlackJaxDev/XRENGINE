using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Rendering.WebGPU;

internal static partial class WebGpuImports
{
    /// <summary>Reads a CPU receipt watermark; never maps or reads GPU buffer contents.</summary>
    [JSImport("pollEngineFrameCompletion", "xrengine.webgpu")]
    internal static partial double PollEngineFrameCompletion(int session);
}
