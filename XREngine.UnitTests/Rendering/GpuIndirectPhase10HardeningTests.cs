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
    public void ShippingFast_DiagnosticsReadbacks_ArePassGatedInSource()
    {
        string core = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.Core.cs");
        string init = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.ShadersAndInit.cs");
        string indirect = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs");

        core.ShouldContain("ShouldCaptureDiagnosticReadbacksForPass");
        init.ShouldContain("MapBuffers skipped (diagnostic readbacks disabled)");
        indirect.ShouldContain("if (!ShouldCaptureDiagnosticReadbacksForPass())");
    }

    [Test]
    public void OverflowPolicy_UsesBoundedDoubling_AndGracefulCapacityGrowth()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs");

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
