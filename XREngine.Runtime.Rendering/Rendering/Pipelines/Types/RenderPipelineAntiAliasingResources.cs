using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Contains the resources used by the anti-aliasing passes of the default render pipeline.
/// </summary>
internal static class RenderPipelineAntiAliasingResources
{
    /// <summary>
    /// Contains the names of the textures used by the anti-aliasing passes of the default render pipeline.
    /// </summary>
    internal static readonly string[] AntiAliasingTextureDependencies =
    [
        DefaultRenderPipeline.PostProcessOutputTextureName,
        DefaultRenderPipeline.FinalPostProcessOutputTextureName,
        DefaultRenderPipeline.FxaaOutputTextureName,
        DefaultRenderPipeline.SmaaEdgeTextureName,
        DefaultRenderPipeline.SmaaBlendTextureName,
        DefaultRenderPipeline.SmaaOutputTextureName,
        DefaultRenderPipeline.TsrOutputTextureName,
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

    /// <summary>
    /// Contains the names of the frame buffers used by the anti-aliasing passes of the default render pipeline.
    /// </summary>
    internal static readonly string[] AntiAliasingFrameBufferDependencies =
    [
        // AmbientOcclusionFBO is managed by AO passes (not CacheOrCreateFBO),
        // so it must not be destroyed here - the AO pass owns its lifecycle.
        DefaultRenderPipeline.LightingAccumFBOName,
        DefaultRenderPipeline.LightCombineFBOName,
        DefaultRenderPipeline.ForwardPassFBOName,
        DefaultRenderPipeline.PostProcessOutputFBOName,
        DefaultRenderPipeline.PostProcessFBOName,
        DefaultRenderPipeline.FinalPostProcessFBOName,
        DefaultRenderPipeline.FinalPostProcessOutputFBOName,
        DefaultRenderPipeline.FxaaFBOName,
        DefaultRenderPipeline.SmaaEdgeFBOName,
        DefaultRenderPipeline.SmaaBlendFBOName,
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

    /// <summary>
    /// Contains the names of the textures used by the anti-aliasing passes of the default render pipeline.
    /// </summary>
    internal static readonly string[] ResizeRecoveryTextureDependencies =
    [
        DefaultRenderPipeline.AmbientOcclusionIntensityTextureName,
        DefaultRenderPipeline.DepthViewTextureName,
        DefaultRenderPipeline.StencilViewTextureName,
        DefaultRenderPipeline.LightingAccumTextureName,
        DefaultRenderPipeline.DiffuseTextureName,
        DefaultRenderPipeline.HDRSceneTextureName,
        DefaultRenderPipeline.BloomBlurTextureName,
        DefaultRenderPipeline.TransparentSceneCopyTextureName,
        DefaultRenderPipeline.TransparentAccumTextureName,
        DefaultRenderPipeline.TransparentRevealageTextureName,
    ];

    /// <summary>
    /// Contains the names of the frame buffers used by the anti-aliasing passes of the default render pipeline.
    /// </summary>
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

    /// <summary>
    /// Invalidates the anti-aliasing resources of the default render pipeline, forcing them to be recreated on the next frame.
    /// </summary>
    /// <param name="instance">The render pipeline instance.</param>
    /// <param name="reason">The reason for invalidation.</param>
    internal static void InvalidateAntiAliasingResources(XRRenderPipelineInstance instance, string reason = "AntiAliasingSettingsChanged")
    {
        VPRC_TemporalAccumulationPass.ResetHistory(instance);
        VPRC_AtmosphereHistoryPass.ResetHistory(instance);
        VPRC_VolumetricFogHistoryPass.ResetHistory(instance);

        foreach (string name in AntiAliasingFrameBufferDependencies)
            instance.RemoveFrameBufferResource(name, reason);

        foreach (string name in AntiAliasingTextureDependencies)
            instance.RemoveTextureResource(name, reason);
    }

    /// <summary>
    /// Invalidates the anti-aliasing resources of the default render pipeline, forcing them to be recreated on the next frame due to a viewport resize.
    /// </summary>
    /// <param name="instance">The render pipeline instance.</param>
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
