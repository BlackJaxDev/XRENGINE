using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AmbientOcclusionGtaoDefaultsTests
{
    [Test]
    public void AmbientOcclusionSettings_DefaultToBalancedGtaoTuning()
    {
        AmbientOcclusionSettings settings = new();

        settings.Type.ShouldBe(AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion);
        settings.Radius.ShouldBe(2.2f, 0.0001f);
        settings.Power.ShouldBe(1.35f, 0.0001f);
        settings.Bias.ShouldBe(0.06f, 0.0001f);

        settings.GroundTruth.SliceCount.ShouldBe(5);
        settings.GTAOSliceCount.ShouldBe(5);
        settings.GroundTruth.StepsPerSlice.ShouldBe(10);
        settings.GTAOStepsPerSlice.ShouldBe(10);
        settings.GroundTruth.DenoiseEnabled.ShouldBeTrue();
        settings.GTAODenoiseEnabled.ShouldBeTrue();
        settings.GroundTruth.DenoiseRadius.ShouldBe(5);
        settings.GTAODenoiseRadius.ShouldBe(5);
        settings.GroundTruth.DenoiseSharpness.ShouldBe(10.0f, 0.0001f);
        settings.GTAODenoiseSharpness.ShouldBe(10.0f, 0.0001f);
        settings.GroundTruth.UseInputNormals.ShouldBeTrue();
        settings.GTAOUseInputNormals.ShouldBeTrue();
        settings.GroundTruth.UseVisibilityBitmask.ShouldBeTrue();
        settings.GTAOUseVisibilityBitmask.ShouldBeTrue();
        settings.GroundTruth.VisibilityBitmaskThickness.ShouldBe(0.12f, 0.0001f);
        settings.GTAOVisibilityBitmaskThickness.ShouldBe(0.12f, 0.0001f);
        settings.GroundTruth.MultiBounceEnabled.ShouldBeTrue();
        settings.GTAOMultiBounceEnabled.ShouldBeTrue();
        settings.GroundTruth.SpecularOcclusionEnabled.ShouldBeTrue();
        settings.GTAOSpecularOcclusionEnabled.ShouldBeTrue();
        settings.GroundTruth.Resolution.ShouldBe(GroundTruthAmbientOcclusionSettings.EResolution.Half);
        settings.GTAOResolution.ShouldBe(GroundTruthAmbientOcclusionSettings.EResolution.Half);
        settings.GroundTruth.UseNormalWeightedBlur.ShouldBeTrue();
        settings.GTAOUseNormalWeightedBlur.ShouldBeTrue();
    }

    [TestCase("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs")]
    [TestCase("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.PostProcessing.cs")]
    public void GtaoSchemaDefaults_UseCentralizedRuntimeConstants(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");

        source.ShouldContain("nameof(AmbientOcclusionSettings.Radius),\n            PostProcessParameterKind.Float,\n            AmbientOcclusionSettings.DefaultRadius,");
        source.ShouldContain("nameof(AmbientOcclusionSettings.Power),\n            PostProcessParameterKind.Float,\n            AmbientOcclusionSettings.DefaultPower,");
        source.ShouldContain("nameof(AmbientOcclusionSettings.Bias),\n            PostProcessParameterKind.Float,\n            AmbientOcclusionSettings.DefaultBias,");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.SliceCount)),\n            PostProcessParameterKind.Int,\n            GroundTruthAmbientOcclusionSettings.DefaultSliceCount,");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.StepsPerSlice)),\n            PostProcessParameterKind.Int,\n            GroundTruthAmbientOcclusionSettings.DefaultStepsPerSlice,");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.DenoiseRadius)),\n            PostProcessParameterKind.Int,\n            GroundTruthAmbientOcclusionSettings.DefaultDenoiseRadius,");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.DenoiseSharpness)),\n            PostProcessParameterKind.Float,\n            GroundTruthAmbientOcclusionSettings.DefaultDenoiseSharpness,");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.UseInputNormals)),\n            PostProcessParameterKind.Bool,\n            GroundTruthAmbientOcclusionSettings.DefaultUseInputNormals,");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.UseVisibilityBitmask)),\n            PostProcessParameterKind.Bool,\n            GroundTruthAmbientOcclusionSettings.DefaultUseVisibilityBitmask,");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.VisibilityBitmaskThickness)),\n            PostProcessParameterKind.Float,\n            GroundTruthAmbientOcclusionSettings.DefaultVisibilityBitmaskThickness,");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.MultiBounceEnabled)),\n            PostProcessParameterKind.Bool,\n            GroundTruthAmbientOcclusionSettings.DefaultMultiBounceEnabled,");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.SpecularOcclusionEnabled)),\n            PostProcessParameterKind.Bool,\n            GroundTruthAmbientOcclusionSettings.DefaultSpecularOcclusionEnabled,");
        source.ShouldContain("(int)GroundTruthAmbientOcclusionSettings.DefaultResolution,");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.UseNormalWeightedBlur)),\n            PostProcessParameterKind.Bool,\n            GroundTruthAmbientOcclusionSettings.DefaultUseNormalWeightedBlur,");
    }

    [Test]
    public void GtaoRuntimeFallbacks_AndShaderDefaults_MatchBalancedTuning()
    {
        string settingsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Camera/GroundTruthAmbientOcclusionSettings.cs").Replace("\r\n", "\n");
        settingsSource.ShouldContain("program.Uniform(\"Radius\", PositiveOr(Owner.Radius, AmbientOcclusionSettings.DefaultRadius));");
        settingsSource.ShouldContain("program.Uniform(\"Bias\", PositiveOr(Owner.Bias, AmbientOcclusionSettings.DefaultBias));");
        settingsSource.ShouldContain("program.Uniform(\"Power\", PositiveOr(Owner.Power, AmbientOcclusionSettings.DefaultPower));");
        settingsSource.ShouldContain("program.Uniform(\"SliceCount\", PositiveOr(SliceCount, DefaultSliceCount));");
        settingsSource.ShouldContain("program.Uniform(\"StepsPerSlice\", PositiveOr(StepsPerSlice, DefaultStepsPerSlice));");
        settingsSource.ShouldContain("program.Uniform(\"DenoiseSharpness\", PositiveOr(DenoiseSharpness, DefaultDenoiseSharpness));");
        settingsSource.ShouldContain("program.Uniform(\"VisibilityBitmaskThickness\", PositiveOr(VisibilityBitmaskThickness, DefaultVisibilityBitmaskThickness));");

        string blurPassSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_GTAOPass.cs").Replace("\r\n", "\n");
        blurPassSource.ShouldContain("program.Uniform(\"DenoiseRadius\", Math.Clamp(settings?.GTAODenoiseRadius ?? GroundTruthAmbientOcclusionSettings.DefaultDenoiseRadius, 0, 16));");
        blurPassSource.ShouldContain("program.Uniform(\"DenoiseSharpness\", settings?.GTAODenoiseSharpness is > 0.0f ? settings.GTAODenoiseSharpness : GroundTruthAmbientOcclusionSettings.DefaultDenoiseSharpness);");
        blurPassSource.ShouldContain("SetDynamicFBO(instance, outputFbo, RenderResourceSizePolicy.Internal());");

        AssertShaderContainsDefaults("Build/CommonAssets/Shaders/Scene3D/GTAOGen.fs");
        AssertShaderContainsDefaults("Build/CommonAssets/Shaders/Scene3D/GTAOGenStereo.fs");
        AssertBlurShaderContainsDefaults("Build/CommonAssets/Shaders/Scene3D/GTAOBlur.fs");
        AssertBlurShaderContainsDefaults("Build/CommonAssets/Shaders/Scene3D/GTAOBlurStereo.fs");
    }

    private static void AssertShaderContainsDefaults(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");
        source.ShouldContain("uniform float Radius = 2.2f;");
        source.ShouldContain("uniform float Bias = 0.06f;");
        source.ShouldContain("uniform float Power = 1.35f;");
        source.ShouldContain("uniform int SliceCount = 5;");
        source.ShouldContain("uniform int StepsPerSlice = 10;");
        source.ShouldContain("uniform bool UseVisibilityBitmask = true;");
        source.ShouldContain("uniform float VisibilityBitmaskThickness = 0.12f;");
        source.ShouldContain("float ComputeSampleFalloff(vec3 delta, float dist, vec3 viewDir, float falloffStart, float radiusVS, float thicknessLimit)");
        source.ShouldContain("bitmaskThickness * falloff");
        source.ShouldContain("float occludedSectorWeight = 0.0f;");
        source.ShouldContain("1.0f - occludedSectorWeight / float(VISIBILITY_BITMASK_SECTOR_COUNT)");
        source.ShouldContain("float ComputeScreenEdgeFade(vec2 uv, float radiusPixels)");
        source.ShouldContain("visibility = mix(1.0f, visibility, ComputeScreenEdgeFade(uv, radiusPixels));");
    }

    private static void AssertBlurShaderContainsDefaults(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");
        source.ShouldContain("uniform int DenoiseRadius = 5;");
        source.ShouldContain("uniform float DenoiseSharpness = 10.0f;");
        source.ShouldContain("float ComputeDenoiseEdgeFade(vec2 uv)");
        source.ShouldContain("OutIntensity = mix(1.0f, blurredAO, ComputeDenoiseEdgeFade(uv));");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}
