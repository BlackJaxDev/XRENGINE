using NUnit.Framework;
using Shouldly;
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
        settings.StepSize.ShouldBe(4.0f);
        settings.JitterStrength.ShouldBe(1.0f);
    }
}