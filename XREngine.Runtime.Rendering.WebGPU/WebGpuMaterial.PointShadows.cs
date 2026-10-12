namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private static void RequirePointShadowOutput(WebGpuFrameBuffer? target)
    {
        if (target is null || !target.HasDepth || target.SampleCount != 1 ||
            target.ColorFormats.Length != 1 || target.ColorFormats[0] != "r16float")
            throw new NotSupportedException("WebGPU.Material.LocalShadowOutputUnsupported: local casters require one R16Float color attachment and independent single-sample raster depth.");
    }
}
