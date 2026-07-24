using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class UICanvasPipelineInitializationTests
{
    private IRuntimeShaderServices? _previousShaderServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousShaderServices;

    [Test]
    public void FreshCanvas_UsesUserInterfacePipelineWithoutCreatingDefaultScenePipeline()
    {
        var canvas = new UICanvasComponent();

        UserInterfaceRenderPipeline pipeline = canvas.RenderPipeline.ShouldBeOfType<UserInterfaceRenderPipeline>();

        canvas.RenderPipelineInstance.Pipeline.ShouldBeSameAs(pipeline);
        pipeline.BatchCollector.ShouldBeSameAs(canvas.BatchCollector);
    }
}
