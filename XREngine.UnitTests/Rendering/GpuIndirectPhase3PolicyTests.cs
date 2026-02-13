using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuIndirectPhase3PolicyTests
{
    [Test]
    public void VulkanFeatureProfile_AutoProfile_ResolvesByBuildConfiguration()
    {
        VulkanFeatureProfile.ResolveRuntimeProfile(EVulkanGpuDrivenProfile.Auto, EBuildConfiguration.Debug)
            .ShouldBe(EVulkanGpuDrivenProfile.DevParity);

        VulkanFeatureProfile.ResolveRuntimeProfile(EVulkanGpuDrivenProfile.Auto, EBuildConfiguration.Development)
            .ShouldBe(EVulkanGpuDrivenProfile.ShippingFast);

        VulkanFeatureProfile.ResolveRuntimeProfile(EVulkanGpuDrivenProfile.Auto, EBuildConfiguration.Release)
            .ShouldBe(EVulkanGpuDrivenProfile.ShippingFast);
    }

    [Test]
    public void VulkanFeatureProfile_ExplicitProfile_IsPreserved()
    {
        VulkanFeatureProfile.ResolveRuntimeProfile(EVulkanGpuDrivenProfile.ShippingFast, EBuildConfiguration.Debug)
            .ShouldBe(EVulkanGpuDrivenProfile.ShippingFast);

        VulkanFeatureProfile.ResolveRuntimeProfile(EVulkanGpuDrivenProfile.DevParity, EBuildConfiguration.Release)
            .ShouldBe(EVulkanGpuDrivenProfile.DevParity);

        VulkanFeatureProfile.ResolveRuntimeProfile(EVulkanGpuDrivenProfile.Diagnostics, EBuildConfiguration.Development)
            .ShouldBe(EVulkanGpuDrivenProfile.Diagnostics);
    }

    [Test]
    public void Phase3_CullingPolicy_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs");

        source.ShouldContain("private bool ShouldUsePassthroughCulling()");
        source.ShouldContain("VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics");
        source.ShouldContain("private bool ShouldAllowCpuFallback(bool debugLoggingEnabled)");
        source.ShouldContain("return VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics;");
        source.ShouldContain("LogCpuFallbackSuppressed(\"GPU frustum cull\")");
        source.ShouldContain("LogCpuFallbackSuppressed(\"GPU BVH cull\")");
        source.ShouldContain("LogCpuFallbackSuppressed(\"GPU pass filter\")");
    }

    [Test]
    public void Phase3_OcclusionPolicy_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.Occlusion.cs");

        source.ShouldContain("if (mode == EOcclusionCullingMode.CpuQueryAsync && VulkanFeatureProfile.ActiveProfile != EVulkanGpuDrivenProfile.Diagnostics)");
        source.ShouldContain("return EOcclusionCullingMode.GpuHiZ;");
        source.ShouldContain("private bool ShouldInvalidateGpuHiZTemporalState(GPUScene scene, XRCamera camera)");
        source.ShouldContain("shared.LastBuiltFrameId = ulong.MaxValue;");
    }

    [Test]
    public void Phase3_MeshPassSafetyNet_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Traditional/VPRC_RenderMeshesPassTraditional.cs");

        source.ShouldContain("VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics");
        source.ShouldContain("Engine.Rendering.Stats.RecordGpuCpuFallback(1, 0);");
        source.ShouldContain("CPU mesh safety-net suppressed for profile");
    }

    [Test]
    public void Phase3_ViewContractValidation_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.ViewSet.cs");

        source.ShouldContain("ValidateViewSetContractOrThrow();");
        source.ShouldContain("private void ValidateViewSetContractOrThrow()");
        source.ShouldContain("if (_indirectSourceViewId >= _activeViewCount)");
        source.ShouldContain("if (descriptor.RenderPassMaskLo == 0u && descriptor.RenderPassMaskHi == 0u)");
    }

    [Test]
    public void Phase3_RenderCommandViewLayoutValidation_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Commands/RenderCommandCollection.cs");

        source.ShouldContain("ValidateViewDescriptorLayout(descriptors.Slice(0, (int)cursor), gpuPass.CommandCapacity);");
        source.ShouldContain("private static void ValidateViewDescriptorLayout(ReadOnlySpan<GPUViewDescriptor> descriptors, uint commandCapacity)");
        source.ShouldContain("if (requestedSourceView != gpuPass.IndirectSourceViewId)");
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
