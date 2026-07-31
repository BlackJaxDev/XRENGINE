using Assimp;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class UnityModelCoordinateConversionTests
{
    [Test]
    public void CreateUnityModelImportOptions_FlipsZHandednessAndPreservesFrontFaces()
    {
        UnityModelImporterDocument metadata = new();

        var options = UnitySceneImporter.CreateUnityModelImportOptions(
            metadata,
            new Dictionary<string, XRMaterial>(StringComparer.Ordinal));

        options.MakeLeftHanded.ShouldBeTrue();
        options.FlipWindingOrder.ShouldBeTrue();
        (options.PostProcessSteps & PostProcessSteps.MakeLeftHanded).ShouldBe(PostProcessSteps.MakeLeftHanded);
        (options.PostProcessSteps & PostProcessSteps.FlipWindingOrder).ShouldBe(PostProcessSteps.FlipWindingOrder);
        options.ZUp.ShouldBeFalse();
    }
}
