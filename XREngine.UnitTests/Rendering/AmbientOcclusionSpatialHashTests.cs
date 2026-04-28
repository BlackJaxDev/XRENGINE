using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AmbientOcclusionSpatialHashTests
{
    [Test]
    public void SpatialHashMaxDistance_IsCompatibilityAliasForSharedRadiusControl()
    {
        AmbientOcclusionSettings settings = new();

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
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/AO/VPRC_SpatialHashAOPass.cs").Replace("\r\n", "\n");

        source.ShouldContain("Radius = settings?.Radius > 0.0f ? settings.Radius : AmbientOcclusionSettings.DefaultRadius,");
        source.ShouldNotContain("ClearSpatialHashData");
        source.ShouldNotContain("cameraMovedSinceLastFrame");
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