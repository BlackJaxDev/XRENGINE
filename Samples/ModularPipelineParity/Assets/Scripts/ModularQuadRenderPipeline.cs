using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;

namespace ModularPipelineParity;

/// <summary>Owns one authored fullscreen program independently of the built-in output pipelines.</summary>
public sealed class ModularQuadRenderPipeline : ModularSampleRenderPipeline
{
    public const string BindingKey = "custom::custom-pass";
    public const string QuadName = "ModularGradientQuad";

    protected override string? FullscreenQuadName => QuadName;

    protected override void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
    {
        builder.QuadMaterial(QuadName).Factory(CreateQuad).Add();
    }

    private XRFrameBuffer CreateQuad()
    {
        XRMaterial material = new(Array.Empty<XRTexture?>(), WebPipelineRasterProgram.CreateShaders(this, BindingKey))
        {
            Name = QuadName,
            RenderOptions = new RenderingParameters
            {
                CullMode = ECullMode.None,
                DepthTest = { Enabled = ERenderParamUsage.Disabled, UpdateDepth = false, Function = EComparison.Always },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                AlphaToCoverage = ERenderParamUsage.Disabled,
            },
        };
        try
        {
            XRQuadFrameBuffer quad = new(material, deriveRenderTargetsFromMaterial: false,
                prepareForInitialRendering: false) { Name = QuadName };
            WebPipelineRasterProgram.OwnMaterial(quad);
            return quad;
        }
        catch
        {
            foreach (XRShader shader in material.Shaders)
                shader.Destroy(true);
            material.Destroy(true);
            throw;
        }
    }
}
