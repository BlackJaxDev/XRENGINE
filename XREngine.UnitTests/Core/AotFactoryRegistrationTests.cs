using NUnit.Framework;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class AotFactoryRegistrationTests
{
    [Test]
    public void GeneratedFactories_RegisterRuntimeActivationTypes()
    {
        bool createdTransform = TransformFactoryRegistry.TryCreate(typeof(RigidBodyTransform), out TransformBase? transform);
        bool createdCommand = ViewportRenderCommandContainer.TryCreateRegisteredCommand(typeof(VPRC_Clear), out ViewportRenderCommand? command);
        bool createdBacking = PostProcessBackingFactoryRegistry.TryCreate(typeof(TonemappingSettings), out object? backing);

        XRCameraParameters cameraParameters = XRCameraParameters.CreateFromType(typeof(XROpenXRFovCameraParameters), previous: null);
        CustomRenderPipeline sourcePipeline = new();
        bool createdOpenXrPipeline = RenderPipeline.TryCreateOpenXrPipeline(sourcePipeline, out RenderPipeline? openXrPipeline);

        Assert.Multiple(() =>
        {
            Assert.That(createdTransform, Is.True);
            Assert.That(transform, Is.TypeOf<RigidBodyTransform>());
            Assert.That(createdCommand, Is.True);
            Assert.That(command, Is.TypeOf<VPRC_Clear>());
            Assert.That(createdBacking, Is.True);
            Assert.That(backing, Is.TypeOf<TonemappingSettings>());
            Assert.That(cameraParameters, Is.TypeOf<XROpenXRFovCameraParameters>());
            Assert.That(createdOpenXrPipeline, Is.True);
            Assert.That(openXrPipeline, Is.TypeOf<CustomRenderPipeline>());
        });
    }
}
