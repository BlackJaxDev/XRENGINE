using NUnit.Framework;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class DefaultWebFxaaAdmissionTests
{
    private IRuntimeShaderServices? _previousShaderServices;
    private IDisposable? _wrapperSuppression;
    private readonly List<DefaultRenderPipeline> _pipelines = [];

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        _wrapperSuppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        RuntimeShaderServices.Current = new PipelineTestShaderServices();
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            foreach (DefaultRenderPipeline pipeline in _pipelines)
                pipeline.Destroy(true);
        }
        finally
        {
            _pipelines.Clear();
            RuntimeShaderServices.Current = _previousShaderServices;
            _wrapperSuppression?.Dispose();
            _wrapperSuppression = null;
        }
    }

    [TestCase(EAntiAliasingMode.None, 1u, false)]
    [TestCase(EAntiAliasingMode.Fxaa, 1u, true)]
    [TestCase(EAntiAliasingMode.Msaa, 4u, false)]
    public void OutputProfileRequiresFxaaOnlyWhenSelected(EAntiAliasingMode mode, uint samples, bool requiresFxaa)
    {
        RenderPipelineResourceProfile profile = RenderPipelineResourceProfile.Empty with
        {
            AntiAliasingMode = mode,
            MsaaSampleCount = samples,
        };

        RenderPipelineRequirements requirements = DefaultRenderPipeline.CreateWebDefaultRequirements(profile);

        Assert.That(WebGpuPipelineAdmission.GetOutputProfileRejection(requirements), Is.Null);
        Assert.That(requirements.RasterPrograms.Contains("fxaa"), Is.EqualTo(requiresFxaa));
        Assert.That(requirements.RasterPrograms.Contains("tonemap"), Is.True);
    }

    [Test]
    public void OtherPostAntiAliasingModesRemainUnsupported()
    {
        RenderPipelineResourceProfile profile = RenderPipelineResourceProfile.Empty with
        {
            AntiAliasingMode = EAntiAliasingMode.Smaa,
        };

        RenderPipelineRequirements requirements = DefaultRenderPipeline.CreateWebDefaultRequirements(profile);

        Assert.That(WebGpuPipelineAdmission.GetOutputProfileRejection(requirements), Is.Not.Null);
        Assert.That(requirements.RasterPrograms.Contains("fxaa"), Is.False);
    }

    [TestCase(EAntiAliasingMode.None)]
    [TestCase(EAntiAliasingMode.Fxaa)]
    [TestCase(EAntiAliasingMode.Msaa)]
    public void OutputGraphDeclaresFxaaOnlyForItsResourceGeneration(EAntiAliasingMode mode)
    {
        (RenderPipelineResourceLayout layout, IReadOnlyCollection<RenderPassMetadata> passes) =
            BuildOutputGraph(mode, 640u, 360u);
        bool fxaa = mode == EAntiAliasingMode.Fxaa;
        const string source = "WebFxaaSourceTexture";

        Assert.That(layout.ResourcesByName.ContainsKey(source), Is.EqualTo(fxaa));
        Assert.That(layout.ResourcesByName.ContainsKey("WebFxaaSourceFBO"), Is.EqualTo(fxaa));
        Assert.That(layout.ResourcesByName.ContainsKey("WebFxaaQuad"), Is.EqualTo(fxaa));
        Assert.That(passes.Any(pass => pass.ResourceUsages.Any(usage =>
            usage.ResourceName == RenderGraphResourceNames.MakeTexture(source))), Is.EqualTo(fxaa));
        Assert.DoesNotThrow(() => RenderGraphSynchronizationPlanner.TopologicallySort(passes));

        if (!fxaa)
            return;

        TextureSpec texture = (TextureSpec)layout.ResourcesByName[source];
        Assert.That(texture.SizePolicy.SizeClass, Is.EqualTo(RenderResourceSizeClass.WindowResolution));
        Assert.That(texture.InternalFormat, Is.EqualTo(EPixelInternalFormat.Rgba8));
        Assert.That(texture.SizedInternalFormat, Is.EqualTo(ESizedInternalFormat.Rgba8));
        Assert.That(texture.Samples, Is.EqualTo(1u));
        FrameBufferSpec fbo = (FrameBufferSpec)layout.ResourcesByName["WebFxaaSourceFBO"];
        Assert.That(fbo.Attachments.Single().ResourceName, Is.EqualTo(source));
        QuadMaterialSpec quad = (QuadMaterialSpec)layout.ResourcesByName["WebFxaaQuad"];
        Assert.That(quad.Dependencies, Does.Contain(source));

        RenderPassMetadata tonemap = passes.Single(pass => pass.Name ==
            VPRC_RenderQuadToFBO.BuildQuadBlitPassName("WebTonemapMaterial", "WebFxaaSourceFBO"));
        RenderPassMetadata filter = passes.Single(pass => pass.Name ==
            VPRC_RenderQuadToFBO.BuildQuadBlitPassName("WebFxaaQuad", RenderGraphResourceNames.OutputRenderTarget));
        RenderPassMetadata overlay = passes.Single(pass => pass.Name == EDefaultRenderPass.OnTopForward.ToString());
        Assert.That(filter.ResourceUsages.Any(usage => usage.ResourceName ==
            RenderGraphResourceNames.MakeTexture(source) && usage.ResourceType == ERenderPassResourceType.SampledTexture), Is.True);
        Assert.That(filter.ExplicitDependencies, Does.Contain(tonemap.PassIndex));
        Assert.That(overlay.ExplicitDependencies, Does.Contain(filter.PassIndex));
    }

    [Test]
    public void FxaaSourceFactoryTracksDisplayResizeRatherThanInternalResolution()
    {
        foreach ((uint width, uint height) in new[] { (640u, 360u), (321u, 191u) })
        {
            (RenderPipelineResourceLayout layout, _) = BuildOutputGraph(EAntiAliasingMode.Fxaa, width, height);
            TextureSpec spec = (TextureSpec)layout.ResourcesByName["WebFxaaSourceTexture"];
            XRTexture2D texture = (XRTexture2D)spec.Factory!();
            try
            {
                Assert.That(texture.Width, Is.EqualTo(width));
                Assert.That(texture.Height, Is.EqualTo(height));
                Assert.That(texture.SamplerName, Is.EqualTo("Texture0"));
                Assert.That(texture.SizedInternalFormat, Is.EqualTo(ESizedInternalFormat.Rgba8));
            }
            finally
            {
                texture.Destroy(true);
            }
        }
    }

    private (RenderPipelineResourceLayout Layout, IReadOnlyCollection<RenderPassMetadata> Passes)
        BuildOutputGraph(EAntiAliasingMode mode, uint width, uint height)
    {
        RenderPipelineResourceProfile profile = RenderPipelineResourceProfile.Empty with
        {
            DisplayWidth = width,
            DisplayHeight = height,
            InternalWidth = Math.Max(1u, width / 2u),
            InternalHeight = Math.Max(1u, height / 2u),
            AntiAliasingMode = mode,
            MsaaSampleCount = mode == EAntiAliasingMode.Msaa ? 4u : 1u,
        };
        DefaultRenderPipeline pipeline = new();
        _pipelines.Add(pipeline);
        RenderPipelineResourceLayoutBuilder builder = new(profile);
        pipeline.DeclareWebResources(builder);
        RenderPipelineResourceLayout layout = builder.Build(profile);
        RenderPassMetadataCollection metadata = new();
        RenderGraphDescribeContext context = new(metadata, layout);
        pipeline.CreateWebOutputCommands().BuildRenderPassMetadata(context);
        DefaultRenderPipeline.DescribeWebRenderPasses(metadata);
        return (layout, metadata.Build());
    }
}
