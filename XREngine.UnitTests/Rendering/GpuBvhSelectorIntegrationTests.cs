using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuBvhSelectorIntegrationTests
{
    [Test]
    public void CaptureOverride_IsExplicitAndDoesNotChangeTheDefaultCalibration()
    {
        string environment = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string collection = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.CullingAndSoA.cs");

        environment.ShouldContain("XRE_FORCE_GPU_BVH_CULLING");
        environment.ShouldContain("XRE_FORCE_GPU_BVH_REBUILD_EVERY_FRAME");
        collection.ShouldContain("private static readonly bool ForceGpuBvhCulling");
        collection.ShouldContain("if (!ForceGpuBvhCulling && !_gpuBvhSelectorCalibration.ShouldUseBvh");
        new GpuBvhSelectorCalibration().FallbackCommandThreshold
            .ShouldBe(GpuBvhSelectorCalibration.UncalibratedCommandThreshold);
    }

    [Test]
    public void RuntimeSelector_UsesBackendViewVisibilityCalibrationWithFallback()
    {
        var calibration = new GpuBvhSelectorCalibration();
        GpuBvhSelectorBucket bucket = GpuBvhSelectorBucket.From(
            GpuBvhCullingBackend.OpenGl,
            activeViewCount: 2u,
            estimatedVisibleRatio: 0.5f);

        calibration.GetCommandThreshold(bucket).ShouldBe(GpuBvhSelectorCalibration.UncalibratedCommandThreshold);
        calibration.ShouldUseBvh(bucket, 511u).ShouldBeFalse();
        calibration.ShouldUseBvh(bucket, 512u).ShouldBeFalse();

        string culling = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.CullingAndSoA.cs");
        string diagnostics = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");

        culling.ShouldContain("GpuBvhSelectorBucket.From(");
        culling.ShouldContain("_activeViewCount == 0u ? 1u : _activeViewCount");
        culling.ShouldContain("_gpuBvhEstimatedVisibleRatio");
        culling.ShouldContain("_gpuBvhSelectorCalibration.ShouldUseBvh(bucket, commandCount)");
        diagnostics.ShouldContain("_gpuBvhEstimatedVisibleRatio = Math.Clamp((float)stats.Culled / stats.Input");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "XRENGINE.slnx")))
            root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("Could not locate workspace root.");
        return File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");
    }
}
