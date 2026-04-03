using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class ColorGradingSettingsTests
{
    [Test]
    public void Defaults_MatchPipelineSchemaDefaults()
    {
        var settings = new ColorGradingSettings();

        settings.AutoExposureMetering.ShouldBe(ColorGradingSettings.AutoExposureMeteringMode.LogAverage);
        settings.ExposureDividend.ShouldBe(0.1f);
        settings.MinExposure.ShouldBe(0.0001f);
        settings.MaxExposure.ShouldBe(100.0f);
    }

    [Test]
    public void GetResolvedExposureBounds_OrdersInvertedBounds()
    {
        var settings = new ColorGradingSettings
        {
            MinExposure = 10.0f,
            MaxExposure = 0.0001f,
        };

        settings.GetResolvedExposureBounds(out float minExposure, out float maxExposure);

        minExposure.ShouldBe(0.0001f);
        maxExposure.ShouldBe(10.0f);
    }

    [Test]
    public void ClampExposureToResolvedBounds_UsesOrderedBounds()
    {
        var settings = new ColorGradingSettings
        {
            MinExposure = 10.0f,
            MaxExposure = 0.0001f,
        };

        settings.ClampExposureToResolvedBounds(-1.0f).ShouldBe(0.0001f);
        settings.ClampExposureToResolvedBounds(5.0f).ShouldBe(5.0f);
        settings.ClampExposureToResolvedBounds(20.0f).ShouldBe(10.0f);
    }
}