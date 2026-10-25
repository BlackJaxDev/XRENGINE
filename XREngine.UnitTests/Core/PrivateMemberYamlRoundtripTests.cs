using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Scene;

namespace XREngine.UnitTests.Core;

[TestFixture]
[NonParallelizable]
public sealed class PrivateMemberYamlRoundtripTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void CanonicalSceneRoundtripRestoresPrivateMembersAndInheritedAssetAliases(bool shared)
    {
        using IDisposable services = AssetSerializationServices.Install(new InlineYamlTestAssetServices());
        TextFile asset = new() { Text = "inline payload" };
        PrivateMemberYamlTransformProbe transform = new()
        {
            InheritedAsset = shared ? asset : null,
            IgnoredAsset = asset
        };
        transform.SetPrivateMembers(asset, 37);
        SceneNode source = new("Private member source", transform);
        SceneNode? clone = null;
        TextFile? clonedAsset = null;
        try
        {
            string yaml = AssetManager.Serializer.Serialize(source);
            Assert.That(yaml, Does.Contain("PrivateAssetAlias").And.Contain("PrivateNumber"));
            Assert.That(yaml, Does.Not.Contain(nameof(PrivateMemberYamlTransformProbe.IgnoredAsset)));
            if (shared)
            {
                Assert.That(yaml, Does.Contain("InheritedAssetAlias"));
                Assert.That(yaml.IndexOf("PrivateAssetAlias", StringComparison.Ordinal),
                    Is.LessThan(yaml.IndexOf("InheritedAssetAlias", StringComparison.Ordinal)));
            }

            clone = AssetManager.Deserializer.Deserialize<SceneNode>(yaml);
            Assert.That(clone.Transform, Is.TypeOf<PrivateMemberYamlTransformProbe>());
            PrivateMemberYamlTransformProbe result = (PrivateMemberYamlTransformProbe)clone.Transform;
            clonedAsset = result.ReadPrivateAsset();
            Assert.That(clonedAsset, Is.Not.Null);
            Assert.That(clonedAsset!.Text, Is.EqualTo("inline payload"));
            Assert.That(clonedAsset, Is.Not.SameAs(asset));
            Assert.That(result.ReadPrivateNumber(), Is.EqualTo(37));
            Assert.That(result.IgnoredAsset, Is.Null);
            if (shared)
                Assert.That(result.InheritedAsset, Is.SameAs(clonedAsset));
            else
                Assert.That(result.InheritedAsset, Is.Null);
        }
        finally
        {
            clone?.Destroy(true);
            clonedAsset?.Destroy(true);
            source.Destroy(true);
            asset.Destroy(true);
        }
    }
}
