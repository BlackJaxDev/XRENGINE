using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using System.Collections.Generic;
using XREngine.Data.Colors;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class XRAssetSerializationTests
{
    [Test]
    public void CookedBinarySerializer_RoundTrips_XRAsset()
    {
        var original = new StubAsset
        {
            Name = "Sample",
            Payload = "payload-123",
            Value = 42
        };

        byte[] bytes = CookedBinarySerializer.Serialize(original);
        bytes.Length.ShouldBeGreaterThan(0);

        var clone = CookedBinarySerializer.Deserialize(typeof(StubAsset), bytes) as StubAsset;
        clone.ShouldNotBeNull();
        clone!.Name.ShouldBe(original.Name);
        clone.Payload.ShouldBe(original.Payload);
        clone.Value.ShouldBe(original.Value);
    }

    [Test]
    public void MemoryPackAdapter_RoundTrips_XRAsset()
    {
        var original = new StubAsset
        {
            Name = "Another",
            Payload = "adapter-payload",
            Value = 7
        };

        byte[] bytes = XRAssetMemoryPackAdapter.Serialize(original);
        bytes.Length.ShouldBeGreaterThan(0);

        var clone = XRAssetMemoryPackAdapter.Deserialize(bytes, typeof(StubAsset)) as StubAsset;
        clone.ShouldNotBeNull();
        clone!.Name.ShouldBe(original.Name);
        clone.Payload.ShouldBe(original.Payload);
        clone.Value.ShouldBe(original.Value);
    }

    [Test]
    public void CookedBinarySerializer_RoundTrips_ListOfAssets()
    {
        var list = new List<XRAsset>
        {
            new StubAsset { Name = "A", Payload = "p1", Value = 1 },
            new StubAsset { Name = "B", Payload = "p2", Value = 2 }
        };

        byte[] bytes = CookedBinarySerializer.Serialize(list);
        bytes.Length.ShouldBeGreaterThan(0);

        var clone = CookedBinarySerializer.Deserialize(typeof(List<XRAsset>), bytes) as List<XRAsset>;
        clone.ShouldNotBeNull();
        clone!.Count.ShouldBe(2);
        clone[0].ShouldBeOfType<StubAsset>();
        clone[1].ShouldBeOfType<StubAsset>();
        ((StubAsset)clone[0]).Payload.ShouldBe("p1");
        ((StubAsset)clone[1]).Payload.ShouldBe("p2");
    }

    [Test]
    public void MemoryPackAdapter_RoundTrips_DictionaryOfAssets()
    {
        var dict = new Dictionary<string, XRAsset>
        {
            ["first"] = new StubAsset { Name = "One", Payload = "p-one", Value = 11 },
            ["second"] = new StubAsset { Name = "Two", Payload = "p-two", Value = 22 }
        };

        byte[] bytes = XRAssetMemoryPackAdapter.Serialize(new StubAssetContainer { Assets = dict });
        bytes.Length.ShouldBeGreaterThan(0);

        var clone = XRAssetMemoryPackAdapter.Deserialize(bytes, typeof(StubAssetContainer)) as StubAssetContainer;
        clone.ShouldNotBeNull();
        clone!.Assets.ShouldNotBeNull();
        clone.Assets.Count.ShouldBe(2);
        clone.Assets["first"].ShouldBeOfType<StubAsset>();
        clone.Assets["second"].ShouldBeOfType<StubAsset>();
        ((StubAsset)clone.Assets["first"]).Payload.ShouldBe("p-one");
        ((StubAsset)clone.Assets["second"]).Payload.ShouldBe("p-two");
    }

    [Test]
    public void YamlDeserializer_Preserves_OuterAnchorAliases_Inside_Nested_XRAsset()
    {
        const string yaml = """
Name: Root
Shared: &o0
    Name: shared-ref
Child:
    Name: Child
    Reference: *o0
""";

        var clone = AssetManager.Deserializer.Deserialize<AnchorContainerAsset>(yaml);

        clone.ShouldNotBeNull();
        clone.Shared.ShouldNotBeNull();
        clone.Child.ShouldNotBeNull();
        clone.Child.Reference.ShouldBeSameAs(clone.Shared);
        clone.Child.Reference!.Name.ShouldBe("shared-ref");
    }

    [Test]
    public void YamlSerializer_Emits_ColorScalars()
    {
        var original = new ColorYamlContainer
        {
            Color4 = new ColorF4(0.25f, 0.5f, 0.75f, 1.0f),
            Color3 = new ColorF3(0.125f, 0.25f, 0.5f)
        };

        string yaml = AssetManager.Serializer.Serialize(original);

        yaml.ShouldContain("Color4: 0.25 0.5 0.75 1");
        yaml.ShouldContain("Color3: 0.125 0.25 0.5");
        yaml.ShouldNotContain("R:");
        yaml.ShouldNotContain("G:");
        yaml.ShouldNotContain("B:");
        yaml.ShouldNotContain("A:");
    }

    [Test]
    public void YamlDeserializer_Reads_Legacy_ColorMappings()
    {
        const string yaml = """
Color4: &o0
  R: 1
  G: 0.5
  A: 1
Color4Alias: *o0
Color3:
  G: 0.25
  B: 0.75
""";

        ColorYamlContainer clone = AssetManager.Deserializer.Deserialize<ColorYamlContainer>(yaml).ShouldNotBeNull();

        clone.Color4.ShouldBe(new ColorF4(1.0f, 0.5f, 0.0f, 1.0f));
        clone.Color4Alias.ShouldBe(clone.Color4);
        clone.Color3.ShouldBe(new ColorF3(0.0f, 0.25f, 0.75f));
    }

    private sealed class StubAsset : XRAsset
    {
        public string? Payload { get; set; }
        public int Value { get; set; }
    }

    private sealed class StubAssetContainer : XRAsset
    {
        public Dictionary<string, XRAsset> Assets { get; set; } = new();
    }

    private sealed class AnchorContainerAsset : XRAsset
    {
        public NamedReference? Shared { get; set; }
        public NestedAliasAsset? Child { get; set; }
    }

    private sealed class ColorYamlContainer
    {
        public ColorF4 Color4 { get; set; }
        public ColorF4 Color4Alias { get; set; }
        public ColorF3 Color3 { get; set; }
    }

    private sealed class NestedAliasAsset : XRAsset
    {
        public NamedReference? Reference { get; set; }
    }

    private sealed class NamedReference : XRObjectBase
    {
    }
}
