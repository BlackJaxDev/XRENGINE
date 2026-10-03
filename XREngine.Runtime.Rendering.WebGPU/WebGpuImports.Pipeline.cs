using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Rendering.WebGPU;

internal static partial class WebGpuImports
{
    [JSImport("configurePipeline", "xrengine.webgpu")]
    internal static partial void ConfigurePipeline(int session, string description);

    [JSImport("configurePipelineMaterial", "xrengine.webgpu")]
    internal static partial void ConfigurePipelineMaterial(int session, int material, string description);

    [JSImport("submitPipelinePacket", "xrengine.webgpu")]
    internal static partial void SubmitPipelinePacket(int session, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);
}
