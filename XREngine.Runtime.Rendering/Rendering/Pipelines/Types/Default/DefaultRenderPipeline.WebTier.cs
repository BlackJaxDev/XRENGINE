namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    /// <summary>
    /// Prevents desktop resource and shader recipes from materializing for an output whose
    /// required forward lighting, shadow, and tonemap route is not yet available.
    /// </summary>
    private static void RequireSupportedOutputResources()
    {
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer?.BackendId != RendererBackendId.WebGPU)
        {
            if (OperatingSystem.IsBrowser())
                throw new NotSupportedException(
                    "WebGPU.DefaultPipeline.OutputUnavailable: a browser output must bind its WebGPU renderer before building pipeline resources.");
            return;
        }

        throw new NotSupportedException(
            "WebGPU.DefaultPipeline.RequiredPassesUnavailable: cooked forward-lit and tonemap shaders, " +
            "engine color/depth/shadow attachments, and the WebGPU material route must all be installed before this output can render.");
    }
}
