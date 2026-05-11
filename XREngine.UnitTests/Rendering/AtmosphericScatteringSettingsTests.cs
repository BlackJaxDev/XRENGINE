using System.Linq;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AtmosphericScatteringSettingsTests
{
    [Test]
    public void Defaults_MatchPipelineSchemaDefaults()
    {
        var settings = new AtmosphericScatteringSettings();

        settings.Enabled.ShouldBeTrue();
        settings.RenderSky.ShouldBeTrue();
        settings.AerialPerspective.ShouldBeTrue();
        settings.Quality.ShouldBe(AtmosphericScatteringSettings.EQualityMode.Balanced);
        settings.ViewSamples.ShouldBe(8);
        settings.OpticalDepthSamples.ShouldBe(0);
        settings.MaxDistance.ShouldBe(200_000.0f);
        settings.JitterStrength.ShouldBe(0.5f);
        settings.TemporalEnabled.ShouldBeTrue();
        settings.DebugMode.ShouldBe(AtmosphericScatteringSettings.EDebugMode.Off);
    }

    [Test]
    public void AuthoringProperties_ClampToShaderSafeRanges()
    {
        var settings = new AtmosphericScatteringSettings
        {
            ViewSamples = 128,
            OpticalDepthSamples = 64,
            MaxDistance = -1.0f,
            JitterStrength = 2.0f,
        };

        settings.ViewSamples.ShouldBe(64);
        settings.OpticalDepthSamples.ShouldBe(32);
        settings.MaxDistance.ShouldBe(0.0f);
        settings.JitterStrength.ShouldBe(1.0f);

        settings.ViewSamples = -10;
        settings.OpticalDepthSamples = -10;
        settings.JitterStrength = -1.0f;

        settings.ViewSamples.ShouldBe(1);
        settings.OpticalDepthSamples.ShouldBe(0);
        settings.JitterStrength.ShouldBe(0.0f);
    }

    [TestCase(typeof(DefaultRenderPipeline))]
    [TestCase(typeof(DefaultRenderPipeline2))]
    public void PipelineSchema_ContainsAtmosphericScatteringStage(Type pipelineType)
    {
        var pipeline = (RenderPipeline)Activator.CreateInstance(pipelineType)!;

        pipeline.PostProcessSchema.TryGetStage("atmosphericScattering", out var stage).ShouldBeTrue();
        stage.ShouldNotBeNull();
        stage!.BackingType.ShouldBe(typeof(AtmosphericScatteringSettings));

        stage.Parameters.Select(parameter => parameter.Name).ShouldContain(nameof(AtmosphericScatteringSettings.Enabled));
        stage.Parameters.Select(parameter => parameter.Name).ShouldContain(nameof(AtmosphericScatteringSettings.RenderSky));
        stage.Parameters.Select(parameter => parameter.Name).ShouldContain(nameof(AtmosphericScatteringSettings.AerialPerspective));
        stage.Parameters.Select(parameter => parameter.Name).ShouldContain(nameof(AtmosphericScatteringSettings.Quality));
        stage.Parameters.Select(parameter => parameter.Name).ShouldContain(nameof(AtmosphericScatteringSettings.DebugMode));

        var enabled = stage.Parameters.Single(parameter => parameter.Name == nameof(AtmosphericScatteringSettings.Enabled));
        var quality = stage.Parameters.Single(parameter => parameter.Name == nameof(AtmosphericScatteringSettings.Quality));
        var viewSamples = stage.Parameters.Single(parameter => parameter.Name == nameof(AtmosphericScatteringSettings.ViewSamples));
        var jitter = stage.Parameters.Single(parameter => parameter.Name == nameof(AtmosphericScatteringSettings.JitterStrength));

        enabled.DefaultValue.ShouldBe(true);
        quality.DefaultValue.ShouldBe((int)AtmosphericScatteringSettings.EQualityMode.Balanced);
        viewSamples.DefaultValue.ShouldBe(8);
        jitter.DefaultValue.ShouldBe(0.5f);
    }
}
