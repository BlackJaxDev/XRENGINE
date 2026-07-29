using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Scene;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class WorldSettingsYamlSerializationTests
{
    [Test]
    public void YamlSerializer_RoundTrips_NonDefaultEnabledFlags()
    {
        WorldSettings original = new()
        {
            EnableContinuousCollision = false,
            RenderSkybox = false,
            AutoCaptureLightProbes = false,
            PreviewWorldBounds = false,
        };

        string yaml = AssetManager.Serializer.Serialize(original);

        yaml.ShouldContain("EnableContinuousCollision: false");
        yaml.ShouldContain("RenderSkybox: false");
        yaml.ShouldContain("AutoCaptureLightProbes: false");
        yaml.ShouldContain("PreviewWorldBounds: false");

        WorldSettings clone = AssetManager.Deserializer
            .Deserialize<WorldSettings>(yaml)
            .ShouldNotBeNull();

        clone.EnableContinuousCollision.ShouldBeFalse();
        clone.RenderSkybox.ShouldBeFalse();
        clone.AutoCaptureLightProbes.ShouldBeFalse();
        clone.PreviewWorldBounds.ShouldBeFalse();
    }
}
