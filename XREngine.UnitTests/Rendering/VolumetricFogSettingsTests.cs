using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Volumes;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class VolumetricFogSettingsTests
{
    [Test]
    public void Defaults_MatchPipelineSchemaDefaults()
    {
        var settings = new VolumetricFogSettings();

        settings.Enabled.ShouldBeFalse();
        settings.Intensity.ShouldBe(1.0f);
        settings.MaxDistance.ShouldBe(150.0f);
        settings.StepSize.ShouldBe(1.0f);
        settings.JitterStrength.ShouldBe(0.5f);
    }

    [Test]
    public void VolumeEdgeFade_IsAuthorableDistance()
    {
        var volume = new VolumetricFogVolumeComponent();

        volume.EdgeFade.ShouldBe(2.0f);

        volume.EdgeFade = 4.5f;
        volume.EdgeFade.ShouldBe(4.5f);

        volume.EdgeFade = -1.0f;
        volume.EdgeFade.ShouldBe(0.0f);
    }
}
