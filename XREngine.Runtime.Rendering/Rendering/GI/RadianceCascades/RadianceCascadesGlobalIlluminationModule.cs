using XREngine.Data.Rendering;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.GI.RadianceCascades;

/// <summary>
/// Provider-owned radiance-cascade screen resolve. Cascades remain authored
/// scene data; this module owns only per-pipeline output, history, and presentation.
/// </summary>
public sealed class RadianceCascadesGlobalIlluminationModule : IGlobalIlluminationModule
{
    public GlobalIlluminationProviderDescriptor Descriptor
        => GlobalIlluminationProviderRegistry.GetRequiredDescriptor(EGlobalIlluminationMode.RadianceCascades);

    public GlobalIlluminationSupportResult EvaluateSupport(in GlobalIlluminationModuleContext context)
        => context.Plan.Support;

    public RenderPipelineResourceVariant BuildResourceVariant(XRViewport? viewport) => default;

    public void DeclareResources(RenderPipelineResourceLayoutBuilder builder, in GlobalIlluminationModuleContext context)
    {
        uint layers = Math.Max(builder.Profile.ViewCount, builder.Profile.Stereo ? 2u : 1u);
        uint width = Math.Max(1u, builder.Profile.InternalWidth);
        uint height = Math.Max(1u, builder.Profile.InternalHeight);
        string depthTexture = context.Resources?.DepthTexture ?? string.Empty;
        string normalTexture = context.Resources?.NormalTexture ?? string.Empty;
        RenderResourceSizePolicy size = RenderResourceSizePolicy.Internal();

        DeclareScreenTexture(builder, RadianceCascadeResourceNames.ScreenDiffuse, size, layers,
            () => CreateScreenTexture(RadianceCascadeResourceNames.ScreenDiffuse, width, height, layers, linear: false),
            RenderResourceLifetime.Persistent, history: false);
        DeclareScreenTexture(builder, RadianceCascadeResourceNames.HistoryA, size, layers,
            () => CreateScreenTexture(RadianceCascadeResourceNames.HistoryA, width, height, layers, linear: true),
            RenderResourceLifetime.Persistent, history: true);
        DeclareScreenTexture(builder, RadianceCascadeResourceNames.HistoryB, size, layers,
            () => CreateScreenTexture(RadianceCascadeResourceNames.HistoryB, width, height, layers, linear: true),
            RenderResourceLifetime.Persistent, history: true);

        builder.QuadMaterial(RadianceCascadeResourceNames.CompositeMaterial)
            .Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(RadianceCascadeResourceNames.ScreenDiffuse, depthTexture, normalTexture)
            .Factory(() => CreateCompositeMaterial(builder.Profile.Stereo,
                depthTexture, normalTexture))
            .Add();
    }

    public void ContributePasses(ViewportRenderCommandContainer commands, in GlobalIlluminationModuleContext context)
    {
        if (context.Anchor != EGlobalIlluminationExecutionAnchor.SurfaceResolve || context.Resources is not { } resources)
            return;

        VPRC_RadianceCascadesPass resolve = commands.Add<VPRC_RadianceCascadesPass>();
        resolve.DepthTextureName = resources.DepthTexture;
        resolve.NormalTextureName = resources.NormalTexture;
        resolve.AlbedoTextureName = resources.AlbedoTexture;
        resolve.RmseTextureName = resources.RmseTexture;
        resolve.OutputTextureName = RadianceCascadeResourceNames.ScreenDiffuse;
        resolve.HistoryTextureAName = RadianceCascadeResourceNames.HistoryA;
        resolve.HistoryTextureBName = RadianceCascadeResourceNames.HistoryB;

        VPRC_GlobalIlluminationCompositePass presentation = commands.Add<VPRC_GlobalIlluminationCompositePass>();
        presentation.SourceQuadFBOName = RadianceCascadeResourceNames.CompositeMaterial;
        presentation.DestinationFBOName = resources.CompositionTarget;
        presentation.OutputTextureName = RadianceCascadeResourceNames.ScreenDiffuse;
        presentation.ProducerPassName = nameof(VPRC_RadianceCascadesPass);
    }

    public void Invalidate(string reason) { }
    public void Release() { }

    private static void DeclareScreenTexture(
        RenderPipelineResourceLayoutBuilder builder, string name, RenderResourceSizePolicy size, uint layers,
        Func<XRTexture> factory, RenderResourceLifetime lifetime, bool history)
    {
        var declaration = builder.Texture(name)
            .Size(size)
            .Lifetime(lifetime)
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .RequiresStorageUsage()
            .Factory(factory);
        if (history)
            declaration.History(RenderResourceHistoryPolicy.ClearOnCommit);
        declaration.Add();
    }

    private static XRTexture CreateScreenTexture(string name, uint width, uint height, uint layers, bool linear)
    {
        XRTexture texture;
        if (layers > 1u)
        {
            var array = XRTexture2DArray.CreateFrameBufferTexture(layers, width, height,
                EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat);
            array.OVRMultiViewParameters = new(0, layers);
            array.Resizable = false;
            array.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            array.MinFilter = linear ? ETexMinFilter.Linear : ETexMinFilter.Nearest;
            array.MagFilter = linear ? ETexMagFilter.Linear : ETexMagFilter.Nearest;
            texture = array;
        }
        else
        {
            var texture2D = XRTexture2D.CreateFrameBufferTexture(width, height,
                EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat);
            texture2D.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            texture2D.MinFilter = linear ? ETexMinFilter.Linear : ETexMinFilter.Nearest;
            texture2D.MagFilter = linear ? ETexMagFilter.Linear : ETexMagFilter.Nearest;
            texture = texture2D;
        }

        texture.Name = name;
        texture.SamplerName = name;
        return texture;
    }

    private static XRFrameBuffer CreateCompositeMaterial(bool stereo, string depthTextureName, string normalTextureName)
    {
        XRShader shader = XRShader.EngineShader(
            $"Scene3D/{(stereo ? "RadianceCascadeCompositeStereo.fs" : "RadianceCascadeComposite.fs")}", EShaderType.Fragment);
        XRMaterial material = new(Array.Empty<XRTexture?>(), shader)
        {
            RenderOptions = new RenderingParameters
            {
                DepthTest = new DepthTest { Enabled = ERenderParamUsage.Unchanged, Function = EComparison.Always, UpdateDepth = false },
                BlendModeAllDrawBuffers = new BlendMode
                {
                    Enabled = ERenderParamUsage.Enabled,
                    RgbSrcFactor = EBlendingFactor.One,
                    AlphaSrcFactor = EBlendingFactor.One,
                    RgbDstFactor = EBlendingFactor.One,
                    AlphaDstFactor = EBlendingFactor.One,
                    RgbEquation = EBlendEquationMode.FuncAdd,
                    AlphaEquation = EBlendEquationMode.FuncAdd,
                },
            },
        };
        material.SettingUniforms += (_, program) => BindCompositeInputs(program, depthTextureName, normalTextureName);
        return new XRQuadFrameBuffer(material, useMultiview: stereo) { Name = RadianceCascadeResourceNames.CompositeMaterial };
    }

    private static void BindCompositeInputs(XRRenderProgram program, string depthTextureName, string normalTextureName)
    {
        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (pipeline is null || !GlobalIlluminationPlanSelection.IsSelectedAndSupported(
                pipeline.Pipeline,
                EGlobalIlluminationMode.RadianceCascades))
            return;

        XRTexture? output = pipeline.GetTexture<XRTexture>(RadianceCascadeResourceNames.ScreenDiffuse);
        XRTexture? depth = pipeline.GetTexture<XRTexture>(depthTextureName);
        XRTexture? normal = pipeline.GetTexture<XRTexture>(normalTextureName);
        if (output is null || depth is null || normal is null)
            return;

        program.Sampler("RadianceCascadeGITexture", output, 0);
        program.Sampler("DepthView", depth, 1);
        program.Sampler("Normal", normal, 2);
        BoundingRectangle region = pipeline.RenderState.CurrentRenderRegion;
        program.Uniform("ScreenWidth", (float)region.Width);
        program.Uniform("ScreenHeight", (float)region.Height);
    }
}
