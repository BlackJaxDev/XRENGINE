using System.Collections.Generic;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class YamlCollectionInterfaceDeserializationTests
{
    [Test]
    public void YamlDeserializer_Deserializes_IReadOnlyList_RootSequence()
    {
        const string yaml = """
- LightCombineFBO
- AmbientOcclusionFBO
""";

        IReadOnlyList<string> fboNames = AssetManager.Deserializer.Deserialize<IReadOnlyList<string>>(yaml).ShouldNotBeNull();

        fboNames.Count.ShouldBe(2);
        fboNames[0].ShouldBe("LightCombineFBO");
        fboNames[1].ShouldBe("AmbientOcclusionFBO");
    }

    [Test]
    public void YamlDeserializer_Deserializes_IReadOnlyDictionary_RootMapping()
    {
        const string yaml = """
LightCombineFBO: 1
AmbientOcclusionFBO: 2
""";

        IReadOnlyDictionary<string, int> fboIndices = AssetManager.Deserializer.Deserialize<IReadOnlyDictionary<string, int>>(yaml).ShouldNotBeNull();

        fboIndices.Count.ShouldBe(2);
        fboIndices["LightCombineFBO"].ShouldBe(1);
        fboIndices["AmbientOcclusionFBO"].ShouldBe(2);
    }

    [Test]
    public void YamlDeserializer_Deserializes_IReadOnlyList_Property_On_SpatialHashAoPass()
    {
        const string yaml = """
DependentFboNames:
  - LightCombineFBO
  - BloomFBO
""";

        VPRC_SpatialHashAOPass pass = AssetManager.Deserializer.Deserialize<VPRC_SpatialHashAOPass>(yaml).ShouldNotBeNull();

        pass.DependentFboNames.Count.ShouldBe(2);
        pass.DependentFboNames[0].ShouldBe("LightCombineFBO");
        pass.DependentFboNames[1].ShouldBe("BloomFBO");
    }
}
