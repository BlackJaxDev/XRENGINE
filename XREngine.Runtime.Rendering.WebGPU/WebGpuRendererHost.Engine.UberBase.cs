using System.Numerics;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    /// <summary>Publishes the exact prepared source lighting switches while retaining a complete physical ABI.</summary>
    internal void PublishUberBaseLighting(WebGpuRenderProgram program, uint flags, Vector4? rasterViewport = null)
    {
        Vector4 viewport;
        if (rasterViewport is { } nativeViewport) viewport = nativeViewport;
        else
        {
            WebGpuFrameBuffer target = _boundEngineFrameBuffer
                ?? throw new InvalidOperationException("WebGPU.UberBase.ViewportMissing: source AO sampling requires the bound raster attachment.");
            viewport = _engineRenderArea is { } area
                ? new Vector4(area.X, target.Height - area.Y - area.Height, area.Width, area.Height)
                : new Vector4(0, 0, target.Width, target.Height);
        }
        program.SetVector4("UberAmbientOcclusionViewport", viewport);
        bool lighting = (flags & 1u) != 0, shadows = lighting && (flags & 2u) != 0;
        if (lighting) PublishForwardLights(program, shadows, shadows, sourceDisablesShadows: !shadows, sourceOwnsProbeReceiver: true);
        else
        {
            program.SetVector4("ForwardLightCounts", Vector4.Zero);
            program.SetVector4("GlobalAmbient", Vector4.Zero);
        }
        if (!shadows)
        {
            program.SetVector4("DirectionalShadowControl", Vector4.Zero);
            program.SetVector4("SpotShadowControl", Vector4.Zero);
            program.SetVector4("PointShadowControl", Vector4.Zero);
            program.Data.Sampler("DirectionalShadowMap", EnsureDefaultDirectionalShadow(), 0);
            program.Data.Sampler("SpotShadowMap", EnsureDefaultSpotShadow(), 0);
            program.Data.Sampler("PointShadowMap", EnsureDefaultPointShadow(), 0);
        }
        PublishUberBaseEnvironment(program, flags);
        if (lighting && (flags & 4u) != 0) PublishAmbientOcclusion(program);
        else
        {
            program.SetVector4("AmbientOcclusionControls", new Vector4(0, 1, 0, 0));
            program.Data.Sampler("AmbientOcclusionTexture", EnsureDisabledAmbientOcclusion(), 0);
        }
    }
}
