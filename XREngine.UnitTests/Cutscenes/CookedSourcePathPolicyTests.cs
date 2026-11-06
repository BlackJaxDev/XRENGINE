using NUnit.Framework;
using Shouldly;
using XREngine.Cutscenes;
using XREngine.Editor.Publishing;
using XREngine.Rendering;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture, NonParallelizable]
public sealed class CookedSourcePathPolicyTests
{
    [Test]
    public void Cook_WritesPortableSourcePaths_SoThatTheTextureKeyIsTheSameAsInTheEditor()
    {
        string root = Path.Combine(Path.GetTempPath(), "cutscene-cook-policy", "Assets");
        string inside = Path.Combine(root, "Models", "floor.png");
        string outside = Path.Combine(Path.GetTempPath(), "cutscene-cook-policy", "Elsewhere", "wall.png");
        XRTexture2D cooked = new() { Name = "floor", OriginalPath = inside, SamplerName = "_MainTex" };
        XRTexture2D external = new() { Name = "wall", OriginalPath = outside, SamplerName = "_MainTex" };
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(root));
            Guid editorKey = CutsceneResidencyAssetKeys.ForTexture(cooked);

            CookedSourcePathPolicy.MakeSourcePathsPortable(cooked).ShouldBe(1);
            CookedSourcePathPolicy.MakeSourcePathsPortable(external).ShouldBe(0);

            cooked.OriginalPath.ShouldBe("game://Models/floor.png");
            external.OriginalPath.ShouldBe(outside);
            CutsceneResidencyAssetKeys.ForTexture(cooked).ShouldBe(editorKey);
        }
        finally
        {
            cooked.Destroy(now: true);
            external.Destroy(now: true);
        }
    }
}
