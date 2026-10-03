using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

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

        settings.GroundTruth.SliceCount.ShouldBe(3);
        settings.GTAOSliceCount.ShouldBe(3);
        settings.GroundTruth.StepsPerSlice.ShouldBe(4);
        settings.GTAOStepsPerSlice.ShouldBe(4);
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

    [Test]
    public void AmbientOcclusionSettings_BindingGenerationTracksOnlyValueChanges()
    {
        AmbientOcclusionSettings settings = new();
        ulong initialGeneration = settings.BindingGeneration;

        settings.Radius = settings.Radius;
        settings.GroundTruth.SliceCount =
            settings.GroundTruth.SliceCount;
        settings.BindingGeneration.ShouldBe(initialGeneration);

        settings.Radius += 0.1f;
        ulong radiusGeneration = settings.BindingGeneration;
        radiusGeneration.ShouldBeGreaterThan(initialGeneration);

        settings.GroundTruth.SliceCount++;
        settings.BindingGeneration.ShouldBeGreaterThan(radiusGeneration);
    }

    [Test]
    public void GtaoGenerationPass_UsesViewOwnedTypedBindingPublisher()
    {
        string source = ReadWorkspaceFile(
                "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_GTAOPass.cs")
            .Replace("\r\n", "\n");

        source.ShouldContain("public ERenderBindingFrequency Frequency");
        source.ShouldContain("publication == EGtaoBindingPublication.Generate");
        source.ShouldContain("? ERenderBindingFrequency.View");
        source.ShouldContain(": ERenderBindingFrequency.Pass;");
        source.ShouldContain(
            "genFbo.FullScreenMesh.BindingPublishers.Add(");
        source.ShouldContain("EGtaoBindingPublication.Generate));");
        source.ShouldNotContain(
            "genFbo.SettingUniforms += GTAOGen_SetUniforms;");
    }

    [TestCase(typeof(DefaultRenderPipeline))]
    [TestCase(typeof(AdvancedRenderPipeline))]
    public void GtaoSchemaDefaults_UseCentralizedRuntimeConstants(Type pipelineType)
    {
        var pipeline = (RenderPipeline)Activator.CreateInstance(pipelineType)!;
        pipeline.PostProcessSchema.TryGetStage(CommonPostProcessStages.AmbientOcclusionStageKey, out var stage).ShouldBeTrue();
        stage.ShouldNotBeNull();

        AssertDefault(nameof(AmbientOcclusionSettings.Radius), PostProcessParameterKind.Float, AmbientOcclusionSettings.DefaultRadius);
        AssertDefault(nameof(AmbientOcclusionSettings.Power), PostProcessParameterKind.Float, AmbientOcclusionSettings.DefaultPower);
        AssertDefault(nameof(AmbientOcclusionSettings.Bias), PostProcessParameterKind.Float, AmbientOcclusionSettings.DefaultBias);
        AssertGroundTruthDefault(nameof(GroundTruthAmbientOcclusionSettings.SliceCount), PostProcessParameterKind.Int, GroundTruthAmbientOcclusionSettings.DefaultSliceCount);
        AssertGroundTruthDefault(nameof(GroundTruthAmbientOcclusionSettings.StepsPerSlice), PostProcessParameterKind.Int, GroundTruthAmbientOcclusionSettings.DefaultStepsPerSlice);
        AssertGroundTruthDefault(nameof(GroundTruthAmbientOcclusionSettings.DenoiseRadius), PostProcessParameterKind.Int, GroundTruthAmbientOcclusionSettings.DefaultDenoiseRadius);
        AssertGroundTruthDefault(nameof(GroundTruthAmbientOcclusionSettings.DenoiseSharpness), PostProcessParameterKind.Float, GroundTruthAmbientOcclusionSettings.DefaultDenoiseSharpness);
        AssertGroundTruthDefault(nameof(GroundTruthAmbientOcclusionSettings.UseInputNormals), PostProcessParameterKind.Bool, GroundTruthAmbientOcclusionSettings.DefaultUseInputNormals);
        AssertGroundTruthDefault(nameof(GroundTruthAmbientOcclusionSettings.UseVisibilityBitmask), PostProcessParameterKind.Bool, GroundTruthAmbientOcclusionSettings.DefaultUseVisibilityBitmask);
        AssertGroundTruthDefault(nameof(GroundTruthAmbientOcclusionSettings.VisibilityBitmaskThickness), PostProcessParameterKind.Float, GroundTruthAmbientOcclusionSettings.DefaultVisibilityBitmaskThickness);
        AssertGroundTruthDefault(nameof(GroundTruthAmbientOcclusionSettings.MultiBounceEnabled), PostProcessParameterKind.Bool, GroundTruthAmbientOcclusionSettings.DefaultMultiBounceEnabled);
        AssertGroundTruthDefault(nameof(GroundTruthAmbientOcclusionSettings.SpecularOcclusionEnabled), PostProcessParameterKind.Bool, GroundTruthAmbientOcclusionSettings.DefaultSpecularOcclusionEnabled);
        AssertGroundTruthDefault(nameof(GroundTruthAmbientOcclusionSettings.Resolution), PostProcessParameterKind.Int, (int)GroundTruthAmbientOcclusionSettings.DefaultResolution);
        AssertGroundTruthDefault(nameof(GroundTruthAmbientOcclusionSettings.UseNormalWeightedBlur), PostProcessParameterKind.Bool, GroundTruthAmbientOcclusionSettings.DefaultUseNormalWeightedBlur);

        void AssertGroundTruthDefault(string name, PostProcessParameterKind kind, object expected)
            => AssertDefault($"{nameof(AmbientOcclusionSettings.GroundTruth)}.{name}", kind, expected);

        void AssertDefault(string name, PostProcessParameterKind kind, object expected)
        {
            var parameter = stage!.Parameters.Single(parameter => parameter.Name == name);
            parameter.Kind.ShouldBe(kind);
            parameter.DefaultValue.ShouldBe(expected);
        }
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
        blurPassSource.ShouldContain("forceRebuild = !HasDeclaredFrameBuffers(instance);");

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
        source.ShouldContain("uniform int SliceCount = 3;");
        source.ShouldContain("uniform int StepsPerSlice = 4;");
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
        return File.ReadAllText(fullPath).Replace("\r\n", "\n", StringComparison.Ordinal);
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
