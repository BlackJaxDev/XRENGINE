using System;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AmbientOcclusionModeSelectionTests
{
    [Test]
    public void NormalizeType_RoutesLegacyAliasesToSupportedModes()
    {
        AmbientOcclusionSettings.NormalizeType(AmbientOcclusionSettings.EType.MultiViewCustom)
            .ShouldBe(AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion);
        AmbientOcclusionSettings.NormalizeType(AmbientOcclusionSettings.EType.ScalableAmbientObscurance)
            .ShouldBe(AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance);
        AmbientOcclusionSettings.NormalizeType(AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype)
            .ShouldBe(AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance);
        AmbientOcclusionSettings.NormalizeType(AmbientOcclusionSettings.EType.HorizonBased)
            .ShouldBe(AmbientOcclusionSettings.EType.HorizonBasedPlus);
    }

    [TestCase(typeof(DefaultRenderPipeline))]
    [TestCase(typeof(AdvancedRenderPipeline))]
    public void AmbientOcclusionSelector_UsesCanonicalLabels(Type pipelineType)
    {
        var pipeline = (RenderPipeline)Activator.CreateInstance(pipelineType)!;
        pipeline.PostProcessSchema.TryGetStage(CommonPostProcessStages.AmbientOcclusionStageKey, out var stage).ShouldBeTrue();
        stage.ShouldNotBeNull();
        var selector = stage!.Parameters.Single(parameter => parameter.Name == nameof(AmbientOcclusionSettings.Type));
        string[] labels = selector.EnumOptions
            .Select(option => option.Label)
            .ToArray();

        labels.ShouldBe(
        [
            "SSAO",
            "MVAO",
            "MSVO",
            "HBAO+",
            "GTAO",
            "VXAO / Voxel AO (Planned)",
            "Spatial Hash AO",
        ]);

        labels.ShouldNotContain("HBAO (Deferred)");
        labels.ShouldNotContain("GTAO (Experimental)");
        labels.ShouldNotContain("Spatial Hash AO (Experimental)");
        labels.ShouldNotContain("Multi-View AO (Custom)");
        labels.ShouldNotContain("Multi-Radius AO (Prototype)");
    }
}
