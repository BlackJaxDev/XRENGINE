using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AmbientOcclusionVisibilityBitmaskTests
{
    [Test]
    public void GtaoVisibilityBitmask_Defaults_AndCompatibilityProperties_StayAligned()
    {
        AmbientOcclusionSettings settings = new();

        settings.GroundTruth.UseVisibilityBitmask.ShouldBeTrue();
        settings.GTAOUseVisibilityBitmask.ShouldBeTrue();
        settings.GroundTruth.VisibilityBitmaskThickness.ShouldBe(1.5002f, 0.0001f);
        settings.GTAOVisibilityBitmaskThickness.ShouldBe(1.5002f, 0.0001f);

        settings.GTAOUseVisibilityBitmask = true;
        settings.GroundTruth.UseVisibilityBitmask.ShouldBeTrue();

        settings.GTAOVisibilityBitmaskThickness = 0.22f;
        settings.GroundTruth.VisibilityBitmaskThickness.ShouldBe(0.22f, 0.0001f);
    }

    [TestCase("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs")]
    [TestCase("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.PostProcessing.cs")]
    public void GtaoVisibilityBitmask_IsExposedInPostProcessSchema(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");

        source.ShouldContain("bool UsesGtaoVisibilityBitmask(object o) => IsGTAO(o) && ((AmbientOcclusionSettings)o).GroundTruth.UseVisibilityBitmask;");
        source.ShouldContain("bool UsesClassicGtaoHorizon(object o) => IsGTAO(o) && !((AmbientOcclusionSettings)o).GroundTruth.UseVisibilityBitmask;");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.UseVisibilityBitmask)");
        source.ShouldContain("displayName: \"Use Visibility Bitmask\"");
        source.ShouldContain("nameof(GroundTruthAmbientOcclusionSettings.VisibilityBitmaskThickness)");
        source.ShouldContain("displayName: \"Visibility Bitmask Thickness\"");
        source.ShouldContain("visibilityCondition: UsesGtaoVisibilityBitmask");
        source.ShouldContain("visibilityCondition: UsesClassicGtaoHorizon");
    }

    [Test]
    public void GtaoSettings_AndShaders_DeclareVisibilityBitmaskUniforms()
    {
        string settingsSource = ReadWorkspaceFile("XRENGINE/Rendering/Camera/GroundTruthAmbientOcclusionSettings.cs").Replace("\r\n", "\n");
        settingsSource.ShouldContain("program.Uniform(\"UseVisibilityBitmask\", UseVisibilityBitmask);");
        settingsSource.ShouldContain("program.Uniform(\"VisibilityBitmaskThickness\", PositiveOr(VisibilityBitmaskThickness, DefaultVisibilityBitmaskThickness));");

        AssertShaderContainsVisibilityBitmaskPath("Build/CommonAssets/Shaders/Scene3D/GTAOGen.fs");
        AssertShaderContainsVisibilityBitmaskPath("Build/CommonAssets/Shaders/Scene3D/GTAOGenStereo.fs");
    }

    private static void AssertShaderContainsVisibilityBitmaskPath(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");
        source.ShouldContain("uniform bool UseVisibilityBitmask = true;");
        source.ShouldContain("uniform float VisibilityBitmaskThickness = 1.5002f;");
        source.ShouldContain("const uint VISIBILITY_BITMASK_SECTOR_COUNT = 32u;");
        source.ShouldContain("uint UpdateSectors(float minHorizon, float maxHorizon, uint globalOccludedBitfield)");
        source.ShouldContain("uint AccumulateVisibilitySectors(vec3 deltaPos, vec3 viewDir, float normalAngle, float samplingDirection, float thickness, uint occludedSectors)");
        source.ShouldContain("float visibilityBitmaskNormalAngle = -gamma;");
        source.ShouldContain("bitCount(occludedSectors)");
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