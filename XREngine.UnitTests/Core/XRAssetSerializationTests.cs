using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using System.Collections.Generic;

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

    private sealed class StubAsset : XRAsset
    {
        public string? Payload { get; set; }
        public int Value { get; set; }
    }

    private sealed class StubAssetContainer : XRAsset
    {
        public Dictionary<string, XRAsset> Assets { get; set; } = new();
    }
}
