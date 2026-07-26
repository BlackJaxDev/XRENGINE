using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials.Functions;
using XREngine.Scene.Importers;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeModularizationPhase4ImportOwnershipTests
{
    [Test]
    public void P43_FunctionGraphsAndRenderSerialization_AreOwnedByRendering()
    {
        typeof(MatFuncOverload).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");
        typeof(EGLSLVersion).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");
        typeof(XRMaterialYamlTypeConverter).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");
        typeof(IRenderAssetSerializationServices).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");
        typeof(DepthTrackingEventEmitter).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Core");
    }

    [Test]
    public void P43_UnityImporters_AreOwnedByEditor()
    {
        typeof(UnityMaterialImporter).Assembly.GetName().Name.ShouldBe("XREngine.Editor");
        typeof(UnitySceneImporter).Assembly.GetName().Name.ShouldBe("XREngine.Editor");
    }
}
