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
    public void VulkanFeatureProfile_AllRuntimeProfilesAllowGpuRenderDispatch()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanFeatureProfile.cs");

        source.ShouldContain("private static bool ProfileAllowsGpuRenderDispatch");
        source.ShouldContain("EVulkanGpuDrivenProfile.ShippingFast => true");
        source.ShouldContain("EVulkanGpuDrivenProfile.DevParity => true");
        source.ShouldContain("EVulkanGpuDrivenProfile.Diagnostics => true");
        source.ShouldNotContain("ProfileAllowsGpuRenderDispatch\n        => false");
    }

    [Test]
    public void VulkanFeatureProfile_GpuBvhCulling_IsExplicitVulkanOptIn()
    {
        VulkanFeatureProfile.ResolveVulkanGpuBvhCullingPolicy(null).ShouldBeFalse();
        VulkanFeatureProfile.ResolveVulkanGpuBvhCullingPolicy("").ShouldBeFalse();
        VulkanFeatureProfile.ResolveVulkanGpuBvhCullingPolicy("0").ShouldBeFalse();
        VulkanFeatureProfile.ResolveVulkanGpuBvhCullingPolicy("disabled").ShouldBeFalse();
        VulkanFeatureProfile.ResolveVulkanGpuBvhCullingPolicy("1").ShouldBeTrue();
        VulkanFeatureProfile.ResolveVulkanGpuBvhCullingPolicy("enabled").ShouldBeTrue();

        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanFeatureProfile.cs");
        source.ShouldContain("GpuBvhCullingEnvVar = XREngineEnvironmentVariables.VulkanGpuBvhCulling");
        source.ShouldContain("private static readonly bool VulkanGpuBvhCullingEnabled");
        source.ShouldContain("ResolveVulkanGpuBvhCullingPolicy(Environment.GetEnvironmentVariable(GpuBvhCullingEnvVar))");
        source.ShouldContain("return ProfileAllowsGpuBvh && VulkanGpuBvhCullingEnabled;");
    }

    [Test]
    public void BuildAccelerationStructure_RespectsVulkanGpuBvhProfile()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_BuildAccelerationStructure.cs");

        source.ShouldContain("VulkanFeatureProfile.ResolveGpuBvhPreference(RuntimeEngine.EffectiveSettings.UseGpuBvh)");
        source.ShouldContain("gpuScene.UseGpuBvh = useGpuBvh;");
        source.ShouldContain("gpuScene.UseInternalBvh = useGpuBvh && EnableInternalSceneBvh;");
        source.ShouldContain("if (!useGpuBvh)");
        source.ShouldContain("PublishEmpty();");
    }

    [Test]
    public void ZeroReadbackMaterialScatter_PreparedOnlyAfterDispatchSucceeds()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");

        source.ShouldContain("bool materialScatterDispatched = DispatchMaterialScatter(scene);");
        source.ShouldContain("_zeroReadbackMaterialScatterPreparedThisFrame = materialScatterDispatched &&");
        source.ShouldContain("private bool DispatchMaterialScatter(GPUScene scene)");
        source.ShouldContain("if (!ResetMaterialScatterBuffersOnGpu())");
        source.ShouldContain("return false;");
        source.ShouldContain("return true;");
        source.ShouldContain("private static bool RequiresActiveMaterialBucketList");
    }

    [Test]
    public void VulkanMeshRenderer_ExternalTriangleIndexBufferSurvivesPreparation()
    {
        string meshRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string buffersSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs");

        meshRendererSource.ShouldContain("private bool _triangleIndexBufferExternallyProvided;");
        buffersSource.ShouldContain("else if (_triangleIndexBufferExternallyProvided)");
        buffersSource.ShouldContain("_triangleIndexBuffer?.TryEnsureReadyForRendering(allowSynchronousBufferUpload);");
        buffersSource.ShouldContain("_triangleIndexBufferExternallyProvided = buffer is not null;");
        buffersSource.ShouldContain("_triangleIndexBufferExternallyProvided = false;");
    }

    [Test]
    public void Phase3_CullingPolicy_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.CullingAndSoA.cs");

        source.ShouldContain("private bool ShouldUsePassthroughCulling()");
        source.ShouldContain("VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics");
        source.ShouldContain("private bool ShouldAllowCpuFallback()");
        source.ShouldContain("return VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics;");
        source.ShouldContain("LogCpuFallbackSuppressed(\"GPU frustum cull\")");
        source.ShouldContain("LogCpuFallbackSuppressed(\"GPU BVH cull\")");
        source.ShouldContain("LogCpuFallbackSuppressed(\"GPU pass filter\")");
    }

    [Test]
    public void Phase3_OcclusionPolicy_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs");

        source.ShouldContain("if (mode == EOcclusionCullingMode.CpuQueryAsync && VulkanFeatureProfile.ActiveProfile != EVulkanGpuDrivenProfile.Diagnostics)");
        source.ShouldContain("return EOcclusionCullingMode.GpuHiZ;");
        source.ShouldContain("case EOcclusionCullingMode.CpuSoftwareOcclusion:");
        source.ShouldContain("private bool ShouldInvalidateGpuHiZTemporalState(GPUScene scene, XRCamera camera)");
        source.ShouldContain("shared.LastBuiltFrameId = ulong.MaxValue;");
        source.ShouldContain("private static bool TryResolveGpuHiZHistoryDepthInput");
        source.ShouldContain("DefaultRenderPipeline.HistoryDepthViewTextureName");
        source.ShouldContain("temporalData.PrevViewProjection");
        source.ShouldContain("Fall back to temporal history only when no current-frame depth view is available.");
        source.ShouldContain("private bool ShouldBypassCurrentDepthGpuHiZRefine(in GpuHiZDepthInput depthInput)");
        source.ShouldContain("IForwardDepthNormalPrePassSettings { ForwardDepthPrePassEnabled: true }");
        source.ShouldContain("RenderPass == (int)EDefaultRenderPass.OpaqueForward");
        source.ShouldContain("RenderPass == (int)EDefaultRenderPass.MaskedForward");
        source.ShouldContain("EGpuHiZSkipReason.MissingShaders");
        source.ShouldContain("EGpuHiZSkipReason.NoDepthTexture");
        source.ShouldContain("scene.BoundsBuffer.BindTo(_hiZOcclusionProgram, 5);");
        source.ShouldContain("OcclusionTelemetry.RecordGpuDepthSource(depthInput.History);");
    }

    [Test]
    public void Phase3_MeshPassSafetyNet_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Traditional/VPRC_RenderMeshesPassTraditional.cs");

        source.ShouldContain("VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics");
        source.ShouldContain("Engine.Rendering.Stats.GpuFallback.RecordGpuCpuFallback(1, 0);");
        source.ShouldContain("CPU mesh safety-net suppressed for policy");
    }

    [Test]
    public void Phase3_ViewContractValidation_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ViewSet.cs");

        source.ShouldContain("ValidateViewSetContractOrThrow();");
        source.ShouldContain("private void ValidateViewSetContractOrThrow()");
        source.ShouldContain("if (_indirectSourceViewId >= _activeViewCount)");
        source.ShouldContain("if (descriptor.RenderPassMaskLo == 0u && descriptor.RenderPassMaskHi == 0u)");
    }

    [Test]
    public void Phase3_RenderCommandViewLayoutValidation_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");

        source.ShouldContain("ValidateViewDescriptorLayout(descriptors.Slice(0, (int)cursor), gpuPass.CommandCapacity);");
        source.ShouldContain("private static void ValidateViewDescriptorLayout(ReadOnlySpan<GPUViewDescriptor> descriptors, uint commandCapacity)");
        source.ShouldContain("if (requestedSourceView != gpuPass.IndirectSourceViewId)");
    }

    [Test]
    public void ZeroReadbackCombinedProgram_UsesReadyUseCacheBeforeRebuildingDescriptor()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string ensureMethod = SliceMethod(
            source,
            "private XRRenderProgram? EnsureCombinedProgram",
            "private bool TryGetReadyCombinedProgramFromUseCache");

        ensureMethod.ShouldContain("TryGetReadyCombinedProgramFromUseCache(useKey, shaderStateRevision, material, out XRRenderProgram? cachedProgram)");
        ensureMethod.IndexOf("TryGetReadyCombinedProgramFromUseCache", StringComparison.Ordinal)
            .ShouldBeLessThan(ensureMethod.IndexOf("new List<XRShader>", StringComparison.Ordinal));

        string cacheMethod = SliceMethod(
            source,
            "private bool TryGetReadyCombinedProgramFromUseCache",
            "private bool EnsureZeroReadbackMaterialSlotProgramsReady");

        cacheMethod.ShouldContain("_materialProgramUseDescriptors.TryGetValue(useKey, out XRRenderProgramDescriptor descriptor)");
        cacheMethod.ShouldContain("descriptor.RenderSettingsVersion != RuntimeEngine.Rendering.Settings.ShaderConfigVersion");
        cacheMethod.ShouldContain("descriptor.MaterialVariantHash != material.ActiveUberVariant.VariantHash");
        cacheMethod.ShouldContain("_materialPrograms.TryGetValue(descriptor, out MaterialProgramCache cache)");
        cacheMethod.ShouldContain("IsProgramReadyForCurrentRenderer(cache.Program)");
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

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Missing start marker '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Missing end marker '{endMarker}' after '{startMarker}'.");
        return source[start..end];
    }
}
