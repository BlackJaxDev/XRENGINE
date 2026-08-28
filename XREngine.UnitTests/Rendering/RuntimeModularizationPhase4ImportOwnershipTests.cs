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
        typeof(IAssetSerializationServices).Assembly.GetName().Name.ShouldBe("XREngine.Data");
        typeof(AssetManagerAssetSerializationServices).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Core");
        typeof(DepthTrackingEventEmitter).Assembly.GetName().Name.ShouldBe("XREngine.Data");
    }

    [Test]
    public void P43_SourceImporters_AreOwnedByEditor()
    {
        typeof(SerializedMaterialImporter).Assembly.GetName().Name.ShouldBe("XREngine.Editor");
        typeof(SerializedSceneImporter).Assembly.GetName().Name.ShouldBe("XREngine.Editor");
    }
}
