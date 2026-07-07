using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

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

    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor PostProcess()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DefaultRenderPipeline.HDRSceneTextureName)
            .SampleTextureMips(DefaultRenderPipeline.BloomBlurTextureName, 0u, 5u)
            .SampleTexture(DefaultRenderPipeline.DepthViewTextureName)
            .SampleTexture(DefaultRenderPipeline.StencilViewTextureName)
            .SampleTexture(DefaultRenderPipeline.AutoExposureTextureName)
            .SampleTexture(DefaultRenderPipeline.AtmosphereColorTextureName)
            .SampleTexture(DefaultRenderPipeline.VolumetricFogColorTextureName);

    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor FinalPostProcess()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DefaultRenderPipeline.PostProcessOutputTextureName);

    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor TsrUpscale()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DefaultRenderPipeline.FinalPostProcessOutputTextureName)
            .SampleTexture(DefaultRenderPipeline.VelocityTextureName)
            .SampleTexture(DefaultRenderPipeline.DepthViewTextureName)
            .SampleTexture(DefaultRenderPipeline.HistoryDepthViewTextureName)
            .SampleTexture(DefaultRenderPipeline.TsrHistoryColorTextureName)
            .SampleTexture(DefaultRenderPipeline.StencilViewTextureName);

    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor MotionBlur()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DefaultRenderPipeline.MotionBlurTextureName)
            .SampleTexture(DefaultRenderPipeline.VelocityTextureName)
            .SampleTexture(DefaultRenderPipeline.DepthViewTextureName);

    public static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor DepthOfField()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DefaultRenderPipeline.DepthOfFieldTextureName)
            .SampleTexture(DefaultRenderPipeline.DepthViewTextureName);
}
