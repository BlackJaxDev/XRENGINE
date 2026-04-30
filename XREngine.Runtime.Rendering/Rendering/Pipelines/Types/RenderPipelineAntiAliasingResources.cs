using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

internal static class RenderPipelineAntiAliasingResources
{
    internal static readonly string[] AntiAliasingTextureDependencies =
    [
        DefaultRenderPipeline.PostProcessOutputTextureName,
        DefaultRenderPipeline.FxaaOutputTextureName,
        DefaultRenderPipeline.SmaaOutputTextureName,
        DefaultRenderPipeline.HistoryColorTextureName,
        DefaultRenderPipeline.HistoryDepthStencilTextureName,
        DefaultRenderPipeline.HistoryDepthViewTextureName,
        DefaultRenderPipeline.TemporalColorInputTextureName,
        DefaultRenderPipeline.TemporalExposureVarianceTextureName,
        DefaultRenderPipeline.HistoryExposureVarianceTextureName,
        DefaultRenderPipeline.TsrHistoryColorTextureName,
        DefaultRenderPipeline.MsaaAlbedoOpacityTextureName,
        DefaultRenderPipeline.MsaaNormalTextureName,
        DefaultRenderPipeline.MsaaRMSETextureName,
        DefaultRenderPipeline.MsaaDepthStencilTextureName,
        DefaultRenderPipeline.MsaaDepthViewTextureName,
        DefaultRenderPipeline.MsaaTransformIdTextureName,
        DefaultRenderPipeline.MsaaLightingTextureName,
        DefaultRenderPipeline.ForwardPassMsaaDepthStencilTextureName,
        DefaultRenderPipeline.ForwardPassMsaaDepthViewTextureName,
    ];

    internal static readonly string[] AntiAliasingFrameBufferDependencies =
    [
        // AmbientOcclusionFBO is managed by AO passes (not CacheOrCreateFBO),
        // so it must not be destroyed here - the AO pass owns its lifecycle.
        DefaultRenderPipeline.LightCombineFBOName,
        DefaultRenderPipeline.ForwardPassFBOName,
        DefaultRenderPipeline.PostProcessOutputFBOName,
        DefaultRenderPipeline.PostProcessFBOName,
        DefaultRenderPipeline.FxaaFBOName,
        DefaultRenderPipeline.SmaaFBOName,
        DefaultRenderPipeline.TsrHistoryColorFBOName,
        DefaultRenderPipeline.TsrUpscaleFBOName,
        DefaultRenderPipeline.HistoryCaptureFBOName,
        DefaultRenderPipeline.TemporalInputFBOName,
        DefaultRenderPipeline.TemporalAccumulationFBOName,
        DefaultRenderPipeline.HistoryExposureFBOName,
        DefaultRenderPipeline.DepthPreloadFBOName,
        DefaultRenderPipeline.ForwardPassMsaaFBOName,
        DefaultRenderPipeline.SceneCopyFBOName,
        DefaultRenderPipeline.TransparentSceneCopyFBOName,
        DefaultRenderPipeline.DeferredTransparencyBlurFBOName,
        DefaultRenderPipeline.TransparentAccumulationFBOName,
        DefaultRenderPipeline.TransparentResolveFBOName,
        DefaultRenderPipeline.VelocityFBOName,
        DefaultRenderPipeline.DeferredGBufferFBOName,
        DefaultRenderPipeline.MsaaGBufferFBOName,
        DefaultRenderPipeline.MsaaLightingFBOName,
        DefaultRenderPipeline.MsaaLightCombineFBOName,
        DefaultRenderPipeline.MsaaDeferredResolveAlbedoFBOName,
        DefaultRenderPipeline.MsaaDeferredResolveNormalFBOName,
        DefaultRenderPipeline.MsaaDeferredResolveRmseFBOName,
    ];

    internal static readonly string[] ResizeRecoveryTextureDependencies =
    [
        DefaultRenderPipeline.AmbientOcclusionIntensityTextureName,
        DefaultRenderPipeline.DepthViewTextureName,
        DefaultRenderPipeline.StencilViewTextureName,
        DefaultRenderPipeline.DiffuseTextureName,
        DefaultRenderPipeline.HDRSceneTextureName,
        DefaultRenderPipeline.BloomBlurTextureName,
        DefaultRenderPipeline.AutoExposureTextureName,
        DefaultRenderPipeline.TransparentSceneCopyTextureName,
        DefaultRenderPipeline.TransparentAccumTextureName,
        DefaultRenderPipeline.TransparentRevealageTextureName,
    ];

    internal static readonly string[] ResizeRecoveryFrameBufferDependencies =
    [
        // AmbientOcclusionFBO is managed by AO passes (not CacheOrCreateFBO),
        // so it must not be destroyed here - the AO pass owns its lifecycle.
        DefaultRenderPipeline.SceneCopyFBOName,
        DefaultRenderPipeline.TransparentSceneCopyFBOName,
        DefaultRenderPipeline.DeferredTransparencyBlurFBOName,
        DefaultRenderPipeline.TransparentAccumulationFBOName,
        DefaultRenderPipeline.TransparentResolveFBOName,
    ];

    internal static void InvalidateAntiAliasingResources(XRRenderPipelineInstance instance, string reason = "AntiAliasingSettingsChanged")
    {
        VPRC_TemporalAccumulationPass.ResetHistory(instance);
        VPRC_VolumetricFogHistoryPass.ResetHistory(instance);

        foreach (string name in AntiAliasingFrameBufferDependencies)
            instance.RemoveFrameBufferResource(name, reason);

        foreach (string name in AntiAliasingTextureDependencies)
            instance.RemoveTextureResource(name, reason);
    }

    internal static void InvalidateViewportResizeResources(XRRenderPipelineInstance instance)
    {
        const string reason = "ViewportResized";

        InvalidateAntiAliasingResources(instance, reason);

        foreach (string name in ResizeRecoveryFrameBufferDependencies)
            instance.RemoveFrameBufferResource(name, reason);

        foreach (string name in ResizeRecoveryTextureDependencies)
            instance.RemoveTextureResource(name, reason);
    }
}