using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// Contains the default render pipeline quad descriptors for the render graph.
/// </summary>
internal static class DefaultRenderPipelineQuadDescriptors
{
    public const string AmbientOcclusionResolveVariantHBAOPlus = "HBAOPlus";
    public const string AmbientOcclusionResolveVariantGTAO = "GTAO";
    public const string AmbientOcclusionResolveVariantDisabled = "Disabled";

    private const string LightProbeIrradianceArrayTextureName = "LightProbeIrradianceArray";
    private const string LightProbePrefilterArrayTextureName = "LightProbePrefilterArray";
    private const string LightProbePositionBufferName = "LightProbePositions";
    private const string LightProbeTetraBufferName = "LightProbeTetrahedra";
    private const string LightProbeParamBufferName = "LightProbeParameters";
    private const string LightProbeGridCellBufferName = "LightProbeGridCells";
    private const string LightProbeGridIndexBufferName = "LightProbeGridIndices";

    /// <summary>
    /// Generates the descriptor for the deferred light combine pass.
    /// </summary>
    /// <returns>The render graph resource descriptor for the deferred light combine pass.</returns>
    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor DeferredLightCombine()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DefaultRenderPipeline.AlbedoOpacityTextureName)
            .SampleTexture(DefaultRenderPipeline.NormalTextureName)
            .SampleTexture(DefaultRenderPipeline.RMSETextureName)
            .SampleTexture(DefaultRenderPipeline.AmbientOcclusionIntensityTextureName)
            .SampleTexture(DefaultRenderPipeline.DepthViewTextureName)
            .SampleTexture(DefaultRenderPipeline.LightingAccumTextureName)
            .SampleTexture(DefaultRenderPipeline.BRDFTextureName)
            .SampleTexture(LightProbeIrradianceArrayTextureName)
            .SampleTexture(LightProbePrefilterArrayTextureName)
            .ReadBuffer(LightProbePositionBufferName)
            .ReadBuffer(LightProbeTetraBufferName)
            .ReadBuffer(LightProbeParamBufferName)
            .ReadBuffer(LightProbeGridCellBufferName)
            .ReadBuffer(LightProbeGridIndexBufferName)
            .DependsOn((int)EDefaultRenderPass.DeferredDecals)
            .MakePassDependOnThis((int)EDefaultRenderPass.Background, EDefaultRenderPass.Background.ToString())
            .UseDestinationDepthStencilAttachments();

    /// <summary>
    /// Generates the descriptor for the ambient occlusion generation pass.
    /// </summary>
    /// <param name="outputTextureName">The name of the output texture.</param>
    /// <param name="disabled">Indicates whether the ambient occlusion pass is disabled.</param>
    /// <returns>The render graph resource descriptor for the ambient occlusion generation pass.</returns>
    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor AmbientOcclusionGenerate(
        string outputTextureName,
        bool disabled = false)
    {
        var descriptor = new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .UseColorTexture(outputTextureName);

        if (!disabled)
        {
            descriptor
                .SampleTexture(DefaultRenderPipeline.NormalTextureName)
                .SampleTexture(DefaultRenderPipeline.DepthViewTextureName)
                .SampleTexture(DefaultRenderPipeline.AmbientOcclusionNoiseTextureName);
        }

        return descriptor;
    }

    /// <summary>
    /// Generates the descriptor for the final ambient occlusion pass.
    /// </summary>
    /// <param name="inputTextureName">The name of the input texture.</param>
    /// <param name="variant">The variant of the ambient occlusion pass.</param>
    /// <param name="disabled">Indicates whether the ambient occlusion pass is disabled.</param>
    /// <returns>The render graph resource descriptor for the final ambient occlusion pass.</returns>
    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor AmbientOcclusionFinal(
        string inputTextureName,
        string? variant,
        bool disabled = false)
    {
        var descriptor = new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .UseColorTexture(DefaultRenderPipeline.AmbientOcclusionIntensityTextureName)
            .DependsOnQuadBlit(
                DefaultRenderPipeline.AmbientOcclusionFBOName,
                DefaultRenderPipeline.AmbientOcclusionBlurFBOName,
                variant);

        if (!disabled)
            descriptor.SampleTexture(inputTextureName);

        return descriptor;
    }

    /// <summary>
    /// Generates the descriptor for the intermediate blur pass of the ambient occlusion.
    /// </summary>
    /// <param name="inputTextureName">The name of the input texture.</param>
    /// <param name="outputTextureName">The name of the output texture.</param>
    /// <param name="variant">The variant of the ambient occlusion pass.</param>
    /// <returns>The render graph resource descriptor for the intermediate blur pass of the ambient occlusion.</returns>
    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor AmbientOcclusionIntermediateBlur(
        string inputTextureName,
        string outputTextureName,
        string? variant)
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(inputTextureName)
            .SampleTexture(DefaultRenderPipeline.DepthViewTextureName)
            .SampleTexture(DefaultRenderPipeline.NormalTextureName)
            .UseColorTexture(outputTextureName)
            .DependsOnQuadBlit(
                DefaultRenderPipeline.AmbientOcclusionFBOName,
                DefaultRenderPipeline.AmbientOcclusionBlurFBOName,
                variant);

    /// <summary>
    /// Generates the descriptor for the final blur pass of the ambient occlusion.
    /// </summary>
    /// <param name="inputTextureName">The name of the input texture.</param>
    /// <param name="intermediateFboName">The name of the intermediate framebuffer object.</param>
    /// <param name="variant">The variant of the ambient occlusion pass.</param>
    /// <returns>The render graph resource descriptor for the final blur pass of the ambient occlusion.</returns>
    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor AmbientOcclusionFinalBlur(
        string inputTextureName,
        string intermediateFboName,
        string? variant)
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(inputTextureName)
            .SampleTexture(DefaultRenderPipeline.DepthViewTextureName)
            .SampleTexture(DefaultRenderPipeline.NormalTextureName)
            .UseColorTexture(DefaultRenderPipeline.AmbientOcclusionIntensityTextureName)
            .DependsOnQuadBlit(
                DefaultRenderPipeline.AmbientOcclusionBlurFBOName,
                intermediateFboName,
                variant);

    /// <summary>
    /// Generates the descriptor for the post-processing pass.
    /// </summary>
    /// <returns>The render graph resource descriptor for the post-processing pass.</returns>
    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor PostProcess()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DefaultRenderPipeline.HDRSceneTextureName)
            .SampleTextureMips(DefaultRenderPipeline.BloomBlurTextureName, 0u, 5u)
            .SampleTexture(DefaultRenderPipeline.DepthViewTextureName)
            .SampleTexture(DefaultRenderPipeline.StencilViewTextureName)
            .SampleTexture(DefaultRenderPipeline.AutoExposureTextureName)
            .SampleTexture(DefaultRenderPipeline.AtmosphereColorTextureName)
            .SampleTexture(DefaultRenderPipeline.VolumetricFogColorTextureName);

    /// <summary>
    /// Generates the descriptor for the final post-processing pass.
    /// </summary>
    /// <returns>The render graph resource descriptor for the final post-processing pass.</returns>
    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor FinalPostProcess()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DefaultRenderPipeline.PostProcessOutputTextureName);

    /// <summary>
    /// Generates the descriptor for the final post-processing pass to the output target.
    /// </summary>
    /// <returns>The render graph resource descriptor for the final post-processing pass to the output target.</returns>
    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor FinalPostProcessToOutputTarget()
        => FinalPostProcess();

    /// <summary>
    /// Generates the descriptor for the temporal super-resolution (TSR) upscale pass.
    /// </summary>
    /// <returns>The render graph resource descriptor for the temporal super-resolution (TSR) upscale pass.</returns>
    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor TsrUpscale()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DefaultRenderPipeline.FinalPostProcessOutputTextureName)
            .SampleTexture(DefaultRenderPipeline.VelocityTextureName)
            .SampleTexture(DefaultRenderPipeline.DepthViewTextureName)
            .SampleTexture(DefaultRenderPipeline.HistoryDepthViewTextureName)
            .SampleTexture(DefaultRenderPipeline.TsrHistoryColorTextureName)
            .SampleTexture(DefaultRenderPipeline.StencilViewTextureName);

    /// <summary>
    /// Generates the descriptor for the motion blur pass.
    /// </summary>
    /// <returns>The render graph resource descriptor for the motion blur pass.</returns>
    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor MotionBlur()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DefaultRenderPipeline.MotionBlurTextureName)
            .SampleTexture(DefaultRenderPipeline.VelocityTextureName)
            .SampleTexture(DefaultRenderPipeline.DepthViewTextureName);

    /// <summary>
    /// Generates the descriptor for the depth of field pass.
    /// </summary>
    /// <returns>The render graph resource descriptor for the depth of field pass.</returns>
    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor DepthOfField()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DefaultRenderPipeline.DepthOfFieldTextureName)
            .SampleTexture(DefaultRenderPipeline.DepthViewTextureName);
}
