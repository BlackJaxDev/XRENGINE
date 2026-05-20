using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuIndirectPhase10HardeningTests
{
    [Test]
    public void Stats_ForbiddenFallbackCounters_SwapAndResetOnBeginFrame()
    {
        bool previousTracking = XREngine.Engine.Rendering.Stats.EnableTracking;
        try
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = true;

            XREngine.Engine.Rendering.Stats.BeginFrame();
            XREngine.Engine.Rendering.Stats.RecordForbiddenGpuFallback(2);
            XREngine.Engine.Rendering.Stats.BeginFrame();

            XREngine.Engine.Rendering.Stats.ForbiddenGpuFallbackEvents.ShouldBe(2);

            XREngine.Engine.Rendering.Stats.BeginFrame();
            XREngine.Engine.Rendering.Stats.ForbiddenGpuFallbackEvents.ShouldBe(0);
        }
        finally
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }

    [Test]
    public void Stats_GpuMeshletCounters_SwapAndResetOnBeginFrame()
    {
        bool previousTracking = XREngine.Engine.Rendering.Stats.EnableTracking;
        try
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = true;

            XREngine.Engine.Rendering.Stats.BeginFrame();
            XREngine.Engine.Rendering.Stats.RecordGpuMeshletStrategyRequested(2);
            XREngine.Engine.Rendering.Stats.RecordGpuMeshletProductionFrame(1);
            XREngine.Engine.Rendering.Stats.RecordGpuMeshletFallback(1);
            XREngine.Engine.Rendering.Stats.RecordGpuMeshletDispatchSkipped(3);
            XREngine.Engine.Rendering.Stats.RecordGpuMeshletTaskStats(100, 10, 5, 2);
            XREngine.Engine.Rendering.Stats.RecordGpuMeshletExpansionOverflow(4);
            XREngine.Engine.Rendering.Stats.RecordGpuMeshletBufferBytesResident(4096);
            XREngine.Engine.Rendering.Stats.RecordGpuMeshletCacheHit(7);
            XREngine.Engine.Rendering.Stats.RecordGpuMeshletCacheMiss(8);
            XREngine.Engine.Rendering.Stats.RecordGpuMeshletCacheStale(9);
            XREngine.Engine.Rendering.Stats.BeginFrame();

            XREngine.Engine.Rendering.Stats.GpuMeshletRequestedFrames.ShouldBe(2);
            XREngine.Engine.Rendering.Stats.GpuMeshletProductionFrames.ShouldBe(1);
            XREngine.Engine.Rendering.Stats.GpuMeshletFallbackFrames.ShouldBe(1);
            XREngine.Engine.Rendering.Stats.GpuMeshletDispatchSkipped.ShouldBe(3);
            XREngine.Engine.Rendering.Stats.GpuMeshletTaskRecordsEmitted.ShouldBe(100);
            XREngine.Engine.Rendering.Stats.GpuMeshletTaskRecordsFrustumCulled.ShouldBe(10);
            XREngine.Engine.Rendering.Stats.GpuMeshletTaskRecordsConeCulled.ShouldBe(5);
            XREngine.Engine.Rendering.Stats.GpuMeshletTaskRecordsHiZCulled.ShouldBe(2);
            XREngine.Engine.Rendering.Stats.GpuMeshletExpansionOverflowCount.ShouldBe(4);
            XREngine.Engine.Rendering.Stats.GpuMeshletBufferBytesResident.ShouldBe(4096);
            XREngine.Engine.Rendering.Stats.GpuMeshletCacheHits.ShouldBe(7);
            XREngine.Engine.Rendering.Stats.GpuMeshletCacheMisses.ShouldBe(8);
            XREngine.Engine.Rendering.Stats.GpuMeshletCacheStale.ShouldBe(9);

            XREngine.Engine.Rendering.Stats.BeginFrame();
            XREngine.Engine.Rendering.Stats.GpuMeshletRequestedFrames.ShouldBe(0);
            XREngine.Engine.Rendering.Stats.GpuMeshletTaskRecordsEmitted.ShouldBe(0);
        }
        finally
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }

    [Test]
    public void ShippingFast_DiagnosticsReadbacks_ArePassGatedInSource()
    {
        string core = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs");
        string init = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ShadersAndInit.cs");
        string indirect = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");

        core.ShouldContain("ShouldCaptureDiagnosticReadbacksForPass");
        init.ShouldContain("MapBuffers skipped flagged diagnostic mappings (diagnostic readbacks disabled)");
        indirect.ShouldContain("if (!ShouldCaptureDiagnosticReadbacksForPass())");
    }

    [Test]
    public void OverflowPolicy_UsesBoundedDoubling_AndGracefulCapacityGrowth()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");

        source.ShouldContain("ComputeBoundedDoublingCapacity");
        source.ShouldContain("scene.EnsureCommandCapacity");
        source.ShouldContain("Overflow growth policy requested capacity increase");
    }

    [Test]
    public void GoldenScene_RequiresZeroForbiddenFallbacks()
    {
        bool previousTracking = XREngine.Engine.Rendering.Stats.EnableTracking;
        try
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = true;
            XREngine.Engine.Rendering.Stats.BeginFrame();

            XREngine.Engine.Rendering.Stats.ForbiddenGpuFallbackEvents.ShouldBe(
                0,
                customMessage: "Golden-scene CI requires zero forbidden fallback attempts in ShippingFast profile.");
        }
        finally
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = ResolveWorkspaceRoot();
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file to exist: {relativePath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XRENGINE.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find workspace root from base directory '{AppContext.BaseDirectory}'.");
    }
}
