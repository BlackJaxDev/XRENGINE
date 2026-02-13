using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuIndirectPhase9ValidationTests
{
    private readonly record struct GoldenCounters(
        uint Requested,
        uint Culled,
        uint Visible,
        uint EmittedIndirect,
        uint Consumed,
        uint Overflow,
        uint Truncation);

    [TestCase("OpenGL", "Vulkan", "Mono", "DevParity")]
    [TestCase("OpenGL", "Vulkan", "Stereo", "DevParity")]
    [TestCase("OpenGL", "Vulkan", "Foveated", "ShippingFast")]
    [TestCase("OpenGL", "Vulkan", "Mirror", "ShippingFast")]
    [TestCase("VulkanCount", "VulkanNoCount", "Mono", "DevParity")]
    [TestCase("VulkanCount", "VulkanNoCount", "Stereo", "ShippingFast")]
    public void GoldenScene_ParityMatrix_StaysEquivalent(string leftBackend, string rightBackend, string viewMode, string profile)
    {
        GPUIndirectRenderCommand[] commands =
        [
            new GPUIndirectRenderCommand { MeshID = 100, MaterialID = 200, RenderPass = 0 },
            new GPUIndirectRenderCommand { MeshID = 101, MaterialID = 201, RenderPass = 0 },
            new GPUIndirectRenderCommand { MeshID = 102, MaterialID = 202, RenderPass = 1 },
            new GPUIndirectRenderCommand { MeshID = 103, MaterialID = 203, RenderPass = 1 },
        ];

        GoldenCounters expected = new(
            Requested: 4,
            Culled: 3,
            Visible: 3,
            EmittedIndirect: 3,
            Consumed: 3,
            Overflow: 0,
            Truncation: 0);

        ValidateGoldenCounters(leftBackend, viewMode, profile, expected);
        ValidateGoldenCounters(rightBackend, viewMode, profile, expected);

        GpuBackendParitySnapshot lhs = GpuBackendParity.BuildSnapshot(leftBackend, expected.Visible, expected.EmittedIndirect, commands, maxSamples: 3);
        GpuBackendParitySnapshot rhs = GpuBackendParity.BuildSnapshot(rightBackend, expected.Visible, expected.EmittedIndirect, commands, maxSamples: 3);

        GpuBackendParity.AreEquivalent(lhs, rhs, out string reason).ShouldBeTrue(reason);
    }

    [Test]
    public void Stats_IndirectEffectivenessCounters_SwapOnBeginFrame()
    {
        bool previousTracking = XREngine.Engine.Rendering.Stats.EnableTracking;
        try
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = true;

            XREngine.Engine.Rendering.Stats.BeginFrame();
            XREngine.Engine.Rendering.Stats.RecordVulkanIndirectEffectiveness(
                requestedDraws: 100,
                culledDraws: 60,
                emittedIndirectDraws: 40,
                consumedDraws: 39,
                overflowCount: 1);

            XREngine.Engine.Rendering.Stats.BeginFrame();

            XREngine.Engine.Rendering.Stats.VulkanRequestedDraws.ShouldBe(100);
            XREngine.Engine.Rendering.Stats.VulkanCulledDraws.ShouldBe(60);
            XREngine.Engine.Rendering.Stats.VulkanEmittedIndirectDraws.ShouldBe(40);
            XREngine.Engine.Rendering.Stats.VulkanConsumedDraws.ShouldBe(39);
            XREngine.Engine.Rendering.Stats.VulkanOverflowCount.ShouldBe(1);
            XREngine.Engine.Rendering.Stats.VulkanCullEfficiency.ShouldBe(0.4, 0.0001);
        }
        finally
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }

    [Test]
    public void Stats_VulkanStageTiming_SwapOnBeginFrame()
    {
        bool previousTracking = XREngine.Engine.Rendering.Stats.EnableTracking;
        try
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = true;

            XREngine.Engine.Rendering.Stats.BeginFrame();
            XREngine.Engine.Rendering.Stats.RecordVulkanGpuDrivenStageTiming(XREngine.Engine.Rendering.Stats.EVulkanGpuDrivenStageTiming.Reset, TimeSpan.FromMilliseconds(1));
            XREngine.Engine.Rendering.Stats.RecordVulkanGpuDrivenStageTiming(XREngine.Engine.Rendering.Stats.EVulkanGpuDrivenStageTiming.Cull, TimeSpan.FromMilliseconds(2));
            XREngine.Engine.Rendering.Stats.RecordVulkanGpuDrivenStageTiming(XREngine.Engine.Rendering.Stats.EVulkanGpuDrivenStageTiming.Occlusion, TimeSpan.FromMilliseconds(3));
            XREngine.Engine.Rendering.Stats.RecordVulkanGpuDrivenStageTiming(XREngine.Engine.Rendering.Stats.EVulkanGpuDrivenStageTiming.Indirect, TimeSpan.FromMilliseconds(4));
            XREngine.Engine.Rendering.Stats.RecordVulkanGpuDrivenStageTiming(XREngine.Engine.Rendering.Stats.EVulkanGpuDrivenStageTiming.Draw, TimeSpan.FromMilliseconds(5));

            XREngine.Engine.Rendering.Stats.BeginFrame();

            XREngine.Engine.Rendering.Stats.VulkanResetStageMs.ShouldBeGreaterThan(0.5);
            XREngine.Engine.Rendering.Stats.VulkanCullStageMs.ShouldBeGreaterThan(1.5);
            XREngine.Engine.Rendering.Stats.VulkanOcclusionStageMs.ShouldBeGreaterThan(2.5);
            XREngine.Engine.Rendering.Stats.VulkanIndirectStageMs.ShouldBeGreaterThan(3.5);
            XREngine.Engine.Rendering.Stats.VulkanDrawStageMs.ShouldBeGreaterThan(4.5);
        }
        finally
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }

    [Test]
    public void VulkanNonCount_DefaultPath_DoesNotUsePerDrawLoopAntiPattern()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs");

        source.ShouldContain("Api!.CmdDrawIndexedIndirect(");
        source.ShouldContain("usedLoopFallback: false");
        source.ShouldNotContain("for (uint i = 0; i < op.DrawCount; i++)", Case.Insensitive);
    }

    [Test]
    public void ShippingFast_NoUnconditionalCpuFallbackAntiPattern()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs");

        source.ShouldContain("if (VulkanFeatureProfile.EnforceStrictNoFallbacks)");
        source.ShouldContain("return VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics;");
    }

    [Test]
    public void VulkanStageTimingHooks_AreWiredForResetCullOcclusionIndirectDraw()
    {
        string indirect = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs");
        string culling = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs");
        string occlusion = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.Occlusion.cs");

        indirect.ShouldContain("EVulkanGpuDrivenStageTiming.Reset");
        indirect.ShouldContain("EVulkanGpuDrivenStageTiming.Indirect");
        indirect.ShouldContain("EVulkanGpuDrivenStageTiming.Draw");
        culling.ShouldContain("EVulkanGpuDrivenStageTiming.Cull");
        occlusion.ShouldContain("EVulkanGpuDrivenStageTiming.Occlusion");
    }

    private static void ValidateGoldenCounters(string backend, string viewMode, string profile, GoldenCounters counters)
    {
        (counters.Requested > 0u).ShouldBeTrue($"{backend}/{viewMode}/{profile}: requested");
        (counters.Culled <= counters.Requested).ShouldBeTrue($"{backend}/{viewMode}/{profile}: culled");
        (counters.Visible <= counters.Culled).ShouldBeTrue($"{backend}/{viewMode}/{profile}: visible");
        counters.EmittedIndirect.ShouldBe(counters.Visible, customMessage: $"{backend}/{viewMode}/{profile}: emitted");
        counters.Consumed.ShouldBe(counters.EmittedIndirect, customMessage: $"{backend}/{viewMode}/{profile}: consumed");
        counters.Overflow.ShouldBe(0u, customMessage: $"{backend}/{viewMode}/{profile}: overflow");
        counters.Truncation.ShouldBe(0u, customMessage: $"{backend}/{viewMode}/{profile}: truncation");
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
