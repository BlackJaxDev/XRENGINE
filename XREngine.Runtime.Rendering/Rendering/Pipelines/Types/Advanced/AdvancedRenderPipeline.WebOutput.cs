using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private XRQuadFrameBuffer CreateAdvancedWebQuad(string name, string bindingKey, XRTexture?[] textures)
    {
        XRMaterial material = new(textures, WebPipelineRasterProgram.CreateShaders(this, bindingKey))
        {
            Name = name,
            RenderOptions = new RenderingParameters
            {
                DepthTest = { Enabled = ERenderParamUsage.Disabled, UpdateDepth = false, Function = EComparison.Always },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions,
            },
        };
        XRQuadFrameBuffer quad = new(material, deriveRenderTargetsFromMaterial: false, prepareForInitialRendering: false)
        { Name = name };
        WebPipelineRasterProgram.OwnMaterial(quad);
        quad.SettingUniforms += WebPipelineRasterProgram.PublishRenderArea;
        return quad;
    }

    private XRTexture RequireAdvancedWebPostInput(string name)
        => GetTexture<XRTexture>(name) ?? RequirePostProcessTexture(WebPostProcessNeutralTextureName);

    private XRFrameBuffer CreateAdvancedWebPostProcessFbo()
    {
        XRQuadFrameBuffer quad = CreateAdvancedWebQuad(PostProcessFBOName, "advanced::post-process",
        [
            RequirePostProcessTexture(HDRSceneTextureName), RequireAdvancedWebPostInput(BloomBlurTextureName),
            RequirePostProcessTexture(DepthViewTextureName), RequirePostProcessTexture(StencilViewTextureName),
            RequireAdvancedWebPostInput(AutoExposureTextureName), RequireAdvancedWebPostInput(AtmosphereColorTextureName),
            RequireAdvancedWebPostInput(VolumetricFogColorTextureName), RequirePostProcessTexture(AdvancedVisibilityResourceNames.Metadata),
        ]);
        quad.SettingUniforms += program => ApplyPostProcessProgramBindings(quad.Material!, program);
        return quad;
    }

    private XRFrameBuffer CreateAdvancedWebFinalPostProcessFbo()
    {
        XRQuadFrameBuffer quad = CreateAdvancedWebQuad(FinalPostProcessFBOName, "advanced::final-post-process",
            [RequirePostProcessTexture(PostProcessOutputTextureName)]);
        quad.SettingUniforms += ApplyFinalPostProcessProgramBindings;
        return quad;
    }

    private XRFrameBuffer CreateAdvancedWebForwardFbo()
    {
        XRQuadFrameBuffer quad = CreateAdvancedWebQuad(ForwardPassFBOName, "advanced::scene-copy",
            [RequirePostProcessTexture(HDRSceneTextureName)]);
        quad.SetRenderTargets(
            ((IFrameBufferAttachement)RequirePostProcessTexture(HDRSceneTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1),
            ((IFrameBufferAttachement)RequirePostProcessTexture(AdvancedVisibilityResourceNames.DepthStencil), EFrameBufferAttachment.DepthStencilAttachment, 0, -1));
        quad.SettingUniforms += BindAdvancedWebSceneCopy;
        return quad;
    }

    private XRFrameBuffer CreateAdvancedWebSceneCopyFbo()
    {
        XRQuadFrameBuffer quad = CreateAdvancedWebQuad(SceneCopyFBOName, "advanced::scene-copy",
            [RequirePostProcessTexture(HDRSceneTextureName)]);
        quad.SettingUniforms += BindAdvancedWebSceneCopy;
        return quad;
    }

    private void BindAdvancedWebSceneCopy(XRRenderProgram program)
        => program.Sampler(HDRSceneTextureName, RequirePostProcessTexture(HDRSceneTextureName), 0);

    private XRFrameBuffer CreateAdvancedWebTransparentResolveFbo()
    {
        XRQuadFrameBuffer quad = CreateAdvancedWebQuad(TransparentResolveFBOName, "advanced::transparent-resolve",
        [RequirePostProcessTexture(TransparentSceneCopyTextureName), RequirePostProcessTexture(TransparentAccumTextureName),
            RequirePostProcessTexture(TransparentRevealageTextureName)]);
        quad.SetRenderTargets(((IFrameBufferAttachement)RequirePostProcessTexture(HDRSceneTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1));
        quad.SettingUniforms += program =>
        {
            program.Sampler(TransparentSceneCopyTextureName, RequirePostProcessTexture(TransparentSceneCopyTextureName), 0);
            program.Sampler(TransparentAccumTextureName, RequirePostProcessTexture(TransparentAccumTextureName), 1);
            program.Sampler(TransparentRevealageTextureName, RequirePostProcessTexture(TransparentRevealageTextureName), 2);
        };
        return quad;
    }

    private XRFrameBuffer CreateAdvancedWebMotionBlurFbo()
    {
        XRQuadFrameBuffer quad = CreateAdvancedWebQuad(MotionBlurFBOName, "advanced::motion-blur",
        [RequirePostProcessTexture(MotionBlurTextureName), RequirePostProcessTexture(VelocityTextureName), RequirePostProcessTexture(DepthViewTextureName)]);
        quad.SettingUniforms += program =>
        {
            program.Sampler(MotionBlurTextureName, RequirePostProcessTexture(MotionBlurTextureName), 0);
            program.Sampler(VelocityTextureName, RequirePostProcessTexture(VelocityTextureName), 1);
            program.Sampler(DepthViewTextureName, RequirePostProcessTexture(DepthViewTextureName), 2);
            ApplyMotionBlurProgramBindings(program);
        };
        return quad;
    }

    private XRFrameBuffer CreateAdvancedWebDepthOfFieldFbo()
    {
        XRQuadFrameBuffer quad = CreateAdvancedWebQuad(DepthOfFieldFBOName, "advanced::depth-of-field",
            [RequirePostProcessTexture(DepthOfFieldTextureName), RequirePostProcessTexture(DepthViewTextureName)]);
        quad.SettingUniforms += program =>
        {
            program.Sampler("ColorSource", RequirePostProcessTexture(DepthOfFieldTextureName), 0);
            program.Sampler(DepthViewTextureName, RequirePostProcessTexture(DepthViewTextureName), 1);
            ApplyDepthOfFieldProgramBindings(program);
        };
        return quad;
    }
}
