using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    public const string WebMsaaHdrTextureName = "WebMsaaHdrTexture";
    public const string WebMsaaDepthTextureName = "WebMsaaDepthTexture";
    public const string WebMsaaNormalTextureName = "WebMsaaNormalTexture";
    private const string WebResolvedHdrFboName = "WebResolvedHdrFBO";
    private const string WebResolvedNormalFboName = "WebResolvedNormalFBO";
    private const string WebMsaaDepthNormalResolveQuadName = "WebMsaaDepthNormalResolveQuad";

    private static bool WebMsaaEnabled(in RenderPipelineResourceProfile profile)
        => profile.AntiAliasingMode == EAntiAliasingMode.Msaa;

    private static void ValidateWebSampleProfile(in RenderPipelineResourceProfile profile)
    {
        if (profile.Stereo || profile.ViewCount != 1 || profile.OutputHDR ||
            profile.OutputColorFormat != EPixelInternalFormat.Rgba8)
            throw new NotSupportedException("WebGPU.DefaultPipeline.ProfileUnsupported: the forward-lit output requires mono SDR RGBA8 presentation.");
        if (profile.AntiAliasingMode is not (EAntiAliasingMode.None or EAntiAliasingMode.Msaa))
            throw new NotSupportedException($"WebGPU.DefaultPipeline.AntiAliasingUnsupported: '{profile.AntiAliasingMode}' has no cooked output path; None and x4 MSAA are supported.");
        if (WebMsaaEnabled(profile) && profile.MsaaSampleCount != 4)
            throw new NotSupportedException($"WebGPU.DefaultPipeline.SampleCountUnsupported: requested x{profile.MsaaSampleCount}; the exact multisample resource and resolve contract requires x4.");
    }

    private void DeclareWebDepthTexture(RenderPipelineResourceLayoutBuilder builder, string name, uint samples)
        => builder.Texture(name).Size(RenderResourceSizePolicy.Internal()).Samples(samples)
            .Usage(RenderPipelineResourceUsage.DepthStencilAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.DepthComponent32, EPixelFormat.DepthComponent, EPixelType.Float)
            .SizedFormat(ESizedInternalFormat.DepthComponent32f)
            .Factory(() => CreateWebDepthTexture(name, samples)).Add();

    private void DeclareWebMsaaResources(RenderPipelineResourceLayoutBuilder builder)
    {
        builder.Texture(WebMsaaHdrTextureName).Size(RenderResourceSizePolicy.Internal()).Samples(4)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Factory(() => CreateWebColorTexture(WebMsaaHdrTextureName, InternalWidth, InternalHeight, 4)).Add();
        DeclareWebDepthTexture(builder, WebMsaaDepthTextureName, 4);
        DeclareWebColorFbo(builder, WebResolvedHdrFboName, HDRSceneTextureName, RenderResourceSizePolicy.Internal());
    }

    private void DeclareWebMsaaNormalResolve(RenderPipelineResourceLayoutBuilder builder)
    {
        builder.Texture(WebMsaaNormalTextureName).Size(RenderResourceSizePolicy.Internal()).Samples(4)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Factory(() => CreateWebColorTexture(WebMsaaNormalTextureName, InternalWidth, InternalHeight, 4)).Add();
        builder.FrameBuffer(WebResolvedNormalFboName).Size(RenderResourceSizePolicy.Internal())
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, WebNormalTextureName).Depth(DepthStencilTextureName)
            .Factory(() => CreateWebColorFbo(WebResolvedNormalFboName, WebNormalTextureName, DepthStencilTextureName)).Add();
        builder.QuadMaterial(WebMsaaDepthNormalResolveQuadName)
            .DependsOn(WebMsaaNormalTextureName).DependsOn(WebMsaaDepthTextureName)
            .Factory(() => CreateWebEffectQuad(WebMsaaDepthNormalResolveQuadName, "depth-normal-msaa-resolve",
                SetWebDepthNormalResolveUniforms, writesDepth: true)).Add();
    }

    private static bool ShouldUseWebMsaa()
        => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.GetTexture<XRTexture2D>(WebMsaaHdrTextureName) is not null;

    private static void SetWebDepthNormalResolveUniforms(XRRenderProgram program)
    {
        XRTexture2D depth = RequireWebEffectTexture(WebMsaaDepthTextureName);
        XRTexture2D normal = RequireWebEffectTexture(WebMsaaNormalTextureName);
        if (depth.MultiSampleCount != 4 || normal.MultiSampleCount != 4 ||
            depth.Width != normal.Width || depth.Height != normal.Height)
            throw new NotSupportedException("WebGPU.DefaultPipeline.DepthNormalResolveUnsupported: matching x4 depth and normal inputs are required.");
        XRCamera camera = RenderPipelineCameraResolver.ResolveCurrentSettingsCamera()
            ?? throw new InvalidOperationException("WebGPU.DefaultPipeline.DepthNormalResolveCameraMissing: the depth resolve requires its scene camera.");
        RenderFrameViewSelection view = WebPipelineRasterProgram.CaptureView(camera, new Vector2(depth.Width, depth.Height));
        program.Uniform("ResolveParameters", new Vector4(depth.Width, depth.Height, view.View.ReversedDepth ? 1 : 0, 0));
        program.Sampler("MultisampleDepth", depth, 0);
        program.Sampler("MultisampleNormal", normal, 1);
    }

    private void AppendWebDepthNormalResolveCommands(ViewportRenderCommandContainer commands)
    {
        VPRC_IfElse choice = commands.Add<VPRC_IfElse>();
        choice.Label = "WebMsaaDepthNormalResolve";
        choice.ConditionEvaluator = ShouldUseWebMsaa;
        ViewportRenderCommandContainer resolve = new(this);
        AddWebEffectQuad(resolve, WebMsaaDepthNormalResolveQuadName, WebResolvedNormalFboName,
            WebMsaaDepthTextureName, WebMsaaNormalTextureName)
            .ConfigureRenderGraphResources(resources => resources.DestinationDepthAccess = RenderGraph.ERenderGraphAccess.Write);
        choice.TrueCommands = resolve;
    }

    private void AppendWebColorResolveCommands(ViewportRenderCommandContainer commands)
    {
        VPRC_IfElse choice = commands.Add<VPRC_IfElse>();
        choice.Label = "WebMsaaColorResolve";
        choice.ConditionEvaluator = ShouldUseWebMsaa;
        ViewportRenderCommandContainer resolve = new(this);
        VPRC_BlitFrameBuffer blit = resolve.Add<VPRC_BlitFrameBuffer>();
        blit.RequiredDeclaredResourceName = WebResolvedHdrFboName;
        blit.SetOptions(ForwardPassFBOName, WebResolvedHdrFboName, EReadBufferMode.ColorAttachment0,
            blitColor: true, blitDepth: false, blitStencil: false, linearFilter: false);
        choice.TrueCommands = resolve;
    }
}
