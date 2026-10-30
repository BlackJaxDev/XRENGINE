using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    private void DeclareWebFxaaResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourceProfile profile = builder.Profile;
        builder.Texture(WebFxaaSourceTextureName).Size(RenderResourceSizePolicy.Window())
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte)
            .SizedFormat(ESizedInternalFormat.Rgba8)
            .Factory(() => CreateWebFxaaSourceTexture(profile.DisplayWidth, profile.DisplayHeight)).Add();
        builder.FrameBuffer(WebFxaaSourceFboName).Size(RenderResourceSizePolicy.Window())
            .Usage(RenderPipelineResourceUsage.ColorAttachment).Color(0, WebFxaaSourceTextureName)
            .Factory(() => CreateWebColorFbo(WebFxaaSourceFboName, WebFxaaSourceTextureName)).Add();
        builder.QuadMaterial(WebFxaaQuadName).DependsOn(WebFxaaSourceTextureName)
            .Factory(() => CreateWebEffectQuad(WebFxaaQuadName, "fxaa", SetWebFxaaUniforms)).Add();
    }

    private static XRTexture CreateWebFxaaSourceTexture(uint width, uint height)
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(Math.Max(width, 1u), Math.Max(height, 1u),
            EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, EFrameBufferAttachment.ColorAttachment0);
        texture.Name = WebFxaaSourceTextureName;
        texture.SamplerName = "Texture0";
        texture.SizedInternalFormat = ESizedInternalFormat.Rgba8;
        texture.Resizable = false;
        texture.AutoGenerateMipmaps = false;
        texture.MaxAnisotropy = 1.0f;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = texture.VWrap = ETexWrapMode.ClampToEdge;
        return texture;
    }

    private static bool ShouldUseWebFxaa()
        => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.ActiveGeneration?.Key.AntiAliasingMode == EAntiAliasingMode.Fxaa;

    private static void SetWebFxaaUniforms(XRRenderProgram program)
    {
        XRTexture2D source = RequireWebEffectTexture(WebFxaaSourceTextureName);
        WebPipelineRasterProgram.PublishRenderArea(program);
        program.Uniform("FxaaTexelStep", new Vector2(1.0f / Math.Max(1u, source.Width),
            1.0f / Math.Max(1u, source.Height)));
        program.Sampler("Texture0", source, 0);
    }
}
