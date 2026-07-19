using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Rendering;

/// <summary>Automated coverage for rollout-matrix completeness and selector calibration.</summary>
[TestFixture]
public sealed class GpuBvhCalibrationAndLifecycleTests
{
    [Test]
    public void WorkloadMatrix_CoversEveryRequiredCalibrationDimension()
    {
        GpuBvhWorkload[] matrix = CreateMatrix();

        matrix.Select(workload => workload.CommandCount).Distinct().Order().ShouldBe(new uint[] { 1_000, 10_000, 100_000, 1_000_000 });
        matrix.Select(workload => workload.Distribution).Distinct().Count().ShouldBe(6);
        matrix.Select(workload => workload.DirtyRatio).Distinct().Order().ShouldBe(new[] { 0f, 0.001f, 0.01f, 0.1f, 1f });
        matrix.Select(workload => workload.VisibleRatio).Distinct().Order().ShouldBe(new[] { 0f, 0.1f, 0.5f, 1f });
        matrix.Select(workload => workload.ViewCount).Distinct().Order().ShouldBe(new uint[] { 1, 2, 3 });
        matrix.Select(workload => workload.LeafCapacity).Distinct().Order().ShouldBe(new uint[] { 1, 2, 4, 8, 16 });
        matrix.Select(workload => workload.Backend).Distinct().Count().ShouldBe(2);
        matrix.Length.ShouldBe(14_400);
    }

    [Test]
    public void Calibrator_RequiresTwoConsecutiveWinsAndSeparatesBackendViewAndVisibility()
    {
        GpuBvhSelectorBucket vulkanStereoLow = GpuBvhSelectorBucket.From(GpuBvhCullingBackend.Vulkan, 2, 0.1f);
        GpuBvhSelectorBucket openGlMonoHigh = GpuBvhSelectorBucket.From(GpuBvhCullingBackend.OpenGl, 1, 1.0f);
        GpuBvhSelectorSample[] samples =
        [
            new(vulkanStereoLow, 1_000, 100, 80),
            new(vulkanStereoLow, 10_000, 1_000, 500),
            new(vulkanStereoLow, 100_000, 10_000, 2_000),
            new(openGlMonoHigh, 1_000, 100, 150),
            new(openGlMonoHigh, 10_000, 1_000, 900),
            new(openGlMonoHigh, 100_000, 10_000, 5_000),
            new(openGlMonoHigh, 1_000_000, 100_000, 30_000),
        ];

        GpuBvhSelectorCalibration calibration = GpuBvhSelectorCalibrator.Calibrate(samples);

        calibration.GetCommandThreshold(vulkanStereoLow).ShouldBe(1_000u);
        calibration.GetCommandThreshold(openGlMonoHigh).ShouldBe(10_000u);
        calibration.ShouldUseBvh(vulkanStereoLow, 999u).ShouldBeFalse();
        calibration.ShouldUseBvh(vulkanStereoLow, 1_000u).ShouldBeTrue();
        calibration.GetCommandThreshold(GpuBvhSelectorBucket.From(GpuBvhCullingBackend.Vulkan, 1, 0.5f))
            .ShouldBe(GpuBvhSelectorCalibration.UncalibratedCommandThreshold);
    }

    [Test]
    public void Calibrator_UsesMedianReplicatesAndRejectsInvalidSamples()
    {
        GpuBvhSelectorBucket bucket = GpuBvhSelectorBucket.From(GpuBvhCullingBackend.Vulkan, 1, 0.5f);
        GpuBvhSelectorSample[] samples =
        [
            new(bucket, 1_000, 100, 90),
            new(bucket, 1_000, 100, 900), // noisy outlier
            new(bucket, 1_000, 100, 80),
            new(bucket, 10_000, 1_000, 700),
            new(bucket, 10_000, 1_000, 800),
            new(bucket, 10_000, double.NaN, 1),
        ];

        GpuBvhSelectorCalibrator.Calibrate(samples).GetCommandThreshold(bucket).ShouldBe(1_000u);
    }

    [Test]
    public void CleanStaticAndGpuDeformedPaths_HaveExplicitZeroWorkAndRefitContracts()
    {
        string bvh = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Bvh.cs");
        string bounds = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs");
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandBuffers.cs");

        bvh.ShouldContain("_gpuBvhTree.RecordCleanFrameSkipped()");
        bvh.ShouldContain("aabbRevision != _lastAppliedCommandAabbRevision");
        commandBuffers.ShouldContain("if (!commandSnapshotDirty)");
        commandBuffers.ShouldContain("return;");

        bounds.ShouldContain("WriteCommandAabbSentinel(commandIndex)");
        bounds.ShouldContain("uploadImmediately: true");
        bounds.ShouldContain("Interlocked.Increment(ref _commandAabbRevision)");
        bounds.ShouldContain("_bvhRefitPending = true");
        bounds.ShouldNotContain("GetDataRawAtIndex<CommandWorldAabb>(commandIndex);\n            if (uploadImmediately");
    }

    [Test]
    public void ExtendedCleanStaticSequence_RecordsNoBuildRefitClearOrAllocationWork()
    {
        using GpuBvhTree tree = new();
        const int StableFrameCount = 600;

        for (int frame = 0; frame < StableFrameCount; ++frame)
            tree.RecordCleanFrameSkipped();

        GpuBvhDiagnostics diagnostics = tree.Diagnostics;
        diagnostics.SkippedCleanFrameCount.ShouldBe((ulong)StableFrameCount);
        diagnostics.BuildCount.ShouldBe(0UL);
        diagnostics.RefitCount.ShouldBe(0UL);
        diagnostics.ClearCount.ShouldBe(0UL);
        diagnostics.BufferReallocationCount.ShouldBe(0UL);
        diagnostics.LogicalPrimitiveCount.ShouldBe(0u);
        diagnostics.RetainedBytes.ShouldBe(0UL);
    }

    private static GpuBvhWorkload[] CreateMatrix()
    {
        uint[] counts = [1_000, 10_000, 100_000, 1_000_000];
        GpuBvhBoundsDistribution[] distributions = Enum.GetValues<GpuBvhBoundsDistribution>();
        float[] dirtyRatios = [0f, 0.001f, 0.01f, 0.1f, 1f];
        float[] visibleRatios = [0f, 0.1f, 0.5f, 1f];
        uint[] views = [1, 2, 3];
        uint[] leafCapacities = [1, 2, 4, 8, 16];
        GpuBvhCullingBackend[] backends = Enum.GetValues<GpuBvhCullingBackend>();

        List<GpuBvhWorkload> result = new(14_400);
        foreach (uint commandCount in counts)
        foreach (GpuBvhBoundsDistribution distribution in distributions)
        foreach (float dirtyRatio in dirtyRatios)
        foreach (float visibleRatio in visibleRatios)
        foreach (uint viewCount in views)
        foreach (uint leafCapacity in leafCapacities)
        foreach (GpuBvhCullingBackend backend in backends)
        {
            result.Add(new(
                commandCount,
                distribution,
                dirtyRatio,
                visibleRatio,
                viewCount,
                leafCapacity,
                backend));
        }

        return result.ToArray();
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "XRENGINE.slnx")))
            root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("Could not locate workspace root.");
        return File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");
    }

    private enum GpuBvhBoundsDistribution
    {
        Uniform,
        Clustered,
        IdenticalCenter,
        LongThin,
        GiantPlusManySmall,
        RapidlyExpanding,
    }

    private readonly record struct GpuBvhWorkload(
        uint CommandCount,
        GpuBvhBoundsDistribution Distribution,
        float DirtyRatio,
        float VisibleRatio,
        uint ViewCount,
        uint LeafCapacity,
        GpuBvhCullingBackend Backend);
}
