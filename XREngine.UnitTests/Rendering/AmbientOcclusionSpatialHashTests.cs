using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AmbientOcclusionSpatialHashTests
{
    [Test]
    public void AmbientOcclusionSettings_DefaultToGtaoButUseSpatialHashTuningWhenSelected()
    {
        AmbientOcclusionSettings settings = new();

        settings.Type.ShouldBe(AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion);
        settings.Radius.ShouldBe(AmbientOcclusionSettings.DefaultRadius, 0.0001f);
        settings.Power.ShouldBe(AmbientOcclusionSettings.DefaultPower, 0.0001f);
        settings.Bias.ShouldBe(AmbientOcclusionSettings.DefaultBias, 0.0001f);

        settings.Type = AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion;

        settings.Radius.ShouldBe(AmbientOcclusionSettings.SpatialHashDefaultRadius, 0.0001f);
        settings.Power.ShouldBe(AmbientOcclusionSettings.SpatialHashDefaultPower, 0.0001f);
        settings.Bias.ShouldBe(AmbientOcclusionSettings.SpatialHashDefaultBias, 0.0001f);
        settings.SpatialHash.SamplesPerPixel.ShouldBe(SpatialHashAmbientOcclusionSettings.DefaultSamplesPerPixel, 0.0001f);
        settings.SpatialHash.CellSize.ShouldBe(SpatialHashAmbientOcclusionSettings.DefaultCellSize, 0.0001f);
        settings.SpatialHash.Steps.ShouldBe(SpatialHashAmbientOcclusionSettings.DefaultSteps);
        settings.SpatialHash.Thickness.ShouldBe(SpatialHashAmbientOcclusionSettings.DefaultThickness, 0.0001f);
        settings.SpatialHash.JitterScale.ShouldBe(SpatialHashAmbientOcclusionSettings.DefaultJitterScale, 0.0001f);
        settings.SpatialHash.TemporalReuseEnabled.ShouldBe(SpatialHashAmbientOcclusionSettings.DefaultTemporalReuseEnabled);
        settings.SpatialHash.TemporalBlendFactor.ShouldBe(SpatialHashAmbientOcclusionSettings.DefaultTemporalBlendFactor, 0.0001f);
        settings.SpatialHash.TemporalClamp.ShouldBe(SpatialHashAmbientOcclusionSettings.DefaultTemporalClamp, 0.0001f);
        settings.SpatialHash.TemporalDepthRejectThreshold.ShouldBe(SpatialHashAmbientOcclusionSettings.DefaultTemporalDepthRejectThreshold, 0.0001f);
        settings.SpatialHash.TemporalMotionRejectionScale.ShouldBe(SpatialHashAmbientOcclusionSettings.DefaultTemporalMotionRejectionScale, 0.0001f);
    }

    [Test]
    public void PostProcessStageState_ChangingAoTypePublishesSpatialHashSharedDefaults()
    {
        PostProcessStageDescriptor descriptor = new(
            "ao",
            "Ambient Occlusion",
            [
                new(
                    nameof(AmbientOcclusionSettings.Type),
                    "Method",
                    PostProcessParameterKind.Int,
                    false,
                    null,
                    (int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null),
                new(
                    nameof(AmbientOcclusionSettings.Radius),
                    "Radius",
                    PostProcessParameterKind.Float,
                    false,
                    null,
                    AmbientOcclusionSettings.DefaultRadius,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null),
                new(
                    nameof(AmbientOcclusionSettings.Power),
                    "Contrast",
                    PostProcessParameterKind.Float,
                    false,
                    null,
                    AmbientOcclusionSettings.DefaultPower,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null),
                new(
                    nameof(AmbientOcclusionSettings.Bias),
                    "Bias",
                    PostProcessParameterKind.Float,
                    false,
                    null,
                    AmbientOcclusionSettings.DefaultBias,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null),
            ],
            typeof(AmbientOcclusionSettings),
            static () => new AmbientOcclusionSettings());
        PostProcessStageState state = new();
        state.AttachDescriptor(descriptor);

        state.SetValue(nameof(AmbientOcclusionSettings.Type), (int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion);

        state.GetValue<float>(nameof(AmbientOcclusionSettings.Radius)).ShouldBe(AmbientOcclusionSettings.SpatialHashDefaultRadius, 0.0001f);
        state.GetValue<float>(nameof(AmbientOcclusionSettings.Power)).ShouldBe(AmbientOcclusionSettings.SpatialHashDefaultPower, 0.0001f);
        state.GetValue<float>(nameof(AmbientOcclusionSettings.Bias)).ShouldBe(AmbientOcclusionSettings.SpatialHashDefaultBias, 0.0001f);
    }

    [Test]
    public void SpatialHashMaxDistance_IsCompatibilityAliasForSharedRadiusControl()
    {
        AmbientOcclusionSettings settings = new();
        settings.Type = AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion;

        settings.SpatialHash.MaxDistance.ShouldBe(settings.Radius);

        settings.SpatialHash.MaxDistance = 1.5f;
        settings.Radius.ShouldBe(1.5f);
        settings.SpatialHashMaxDistance.ShouldBe(1.5f);

        settings.SpatialHashMaxDistance = 2.0f;
        settings.Radius.ShouldBe(2.0f);
        settings.SpatialHash.MaxDistance.ShouldBe(2.0f);
    }

    [Test]
    public void SpatialHashComputePass_UsesSharedRadiusAndKeepsHashHistoryAcrossCameraMotion()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_SpatialHashAOPass.cs").Replace("\r\n", "\n");

        source.ShouldContain("Radius = settings?.Radius > 0.0f ? settings.Radius : AmbientOcclusionSettings.SpatialHashDefaultRadius,");
        source.ShouldNotContain("ClearSpatialHashData");
        source.ShouldNotContain("cameraMovedSinceLastFrame");
    }

    [Test]
    public void SpatialHashComputePass_OnlyPublishesStorageMetadataWhenSpatialHashIsSelected()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_SpatialHashAOPass.cs").Replace("\r\n", "\n");

        source.ShouldContain("protected override bool ShouldExecuteThisFrame()");
        source.ShouldContain("IsSpatialHashAmbientOcclusionSelected(RuntimeEngine.Rendering.State.CurrentRenderingPipeline)");
        source.ShouldContain("AmbientOcclusionSettings.NormalizeType(settings.Type) == AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion");

        int guardIndex = source.IndexOf("if (!IsSpatialHashAmbientOcclusionSelected(RuntimeEngine.Rendering.State.CurrentRenderingPipeline))", StringComparison.Ordinal);
        int storageIndex = source.IndexOf("builder.ReadWriteTexture(MakeTextureResource(IntensityTextureName));", StringComparison.Ordinal);

        guardIndex.ShouldBeGreaterThanOrEqualTo(0);
        storageIndex.ShouldBeGreaterThan(guardIndex);
    }

    [TestCase("Build/CommonAssets/Shaders/Compute/AO/SpatialHashAO.comp")]
    [TestCase("Build/CommonAssets/Shaders/Compute/AO/SpatialHashAOStereo.comp")]
    public void SpatialHashComputeShaders_AccumulateWeightedOcclusionInsteadOfBinaryHits(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");

        source.ShouldContain("const uint OcclusionScale = 1024u;");
        source.ShouldContain("float distanceWeight = clamp(1.0 - marchDist / max(maxDistance, 1e-4), 0.0, 1.0);");
        source.ShouldContain("uint encodedOcclusion = uint(clamp(occlusionSample, 0.0, 1.0) * float(OcclusionScale) + 0.5);");
        source.ShouldNotContain("uint hit = uint(RayMarchOcclusion");
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
