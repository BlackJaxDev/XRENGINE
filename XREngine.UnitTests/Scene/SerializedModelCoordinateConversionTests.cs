using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class SerializedModelCoordinateConversionTests
{
    [Test]
    public void CreateSerializedModelImportOptions_FlipsZHandednessAndPreservesFrontFaces()
    {
        SerializedModelImporterDocument metadata = new();

        var options = SerializedSceneImporter.CreateSerializedModelImportOptions(
            metadata,
            new Dictionary<string, XRMaterial>(StringComparer.Ordinal));

        options.MakeLeftHanded.ShouldBeTrue();
        options.FlipWindingOrder.ShouldBeTrue();
        (options.ImportSteps & ModelImportSteps.MakeLeftHanded).ShouldBe(ModelImportSteps.MakeLeftHanded);
        (options.ImportSteps & ModelImportSteps.FlipWindingOrder).ShouldBe(ModelImportSteps.FlipWindingOrder);
        options.ZUp.ShouldBeFalse();
    }
}
