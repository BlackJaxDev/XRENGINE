using System;
using System.Linq;
using System.Reflection;
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
    [TestCase(typeof(DefaultRenderPipeline2))]
    public void AmbientOcclusionSelector_UsesCanonicalLabels(Type pipelineType)
    {
        string[] labels = InvokeBuildAmbientOcclusionTypeOptions(pipelineType)
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
            "Spatial Hash AO (Experimental)",
        ]);

        labels.ShouldNotContain("HBAO (Deferred)");
        labels.ShouldNotContain("GTAO (Experimental)");
        labels.ShouldNotContain("Multi-View AO (Custom)");
        labels.ShouldNotContain("Multi-Radius AO (Prototype)");
    }

    private static PostProcessEnumOption[] InvokeBuildAmbientOcclusionTypeOptions(Type pipelineType)
    {
        MethodInfo? method = pipelineType.GetMethod(
            "BuildAmbientOcclusionTypeOptions",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.ShouldNotBeNull();

        object? result = method.Invoke(null, null);
        result.ShouldBeOfType<PostProcessEnumOption[]>();
        return (PostProcessEnumOption[])result;
    }
}