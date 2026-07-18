using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
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
    public void VulkanFeatureProfile_GpuBvhCulling_IsStrategyDriven()
    {
        EMeshSubmissionStrategy.CpuDirect.UsesGpuBvhCulling().ShouldBeFalse();
        EMeshSubmissionStrategy.GpuIndirectInstrumented.UsesGpuBvhCulling().ShouldBeTrue();
        EMeshSubmissionStrategy.GpuIndirectZeroReadback.UsesGpuBvhCulling().ShouldBeTrue();
        EMeshSubmissionStrategy.GpuMeshletInstrumented.UsesGpuBvhCulling().ShouldBeTrue();
        EMeshSubmissionStrategy.GpuMeshletZeroReadback.UsesGpuBvhCulling().ShouldBeTrue();

        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanFeatureProfile.cs");
        source.ShouldContain("ResolveGpuBvhUsage(EMeshSubmissionStrategy strategy)");
        source.ShouldContain("if (!strategy.UsesGpuBvhCulling())");
        source.ShouldContain("return ProfileAllowsGpuBvh;");
        source.ShouldNotContain("GpuBvhCullingEnvVar");
        source.ShouldNotContain("ResolveVulkanGpuBvhCullingPolicy");
    }

    [TestCase(EMeshSubmissionStrategy.GpuIndirectInstrumented, null, false, false)]
    [TestCase(EMeshSubmissionStrategy.GpuIndirectInstrumented, "", false, false)]
    [TestCase(EMeshSubmissionStrategy.GpuIndirectInstrumented, "0", false, false)]
    [TestCase(EMeshSubmissionStrategy.GpuIndirectInstrumented, "false", false, false)]
    [TestCase(EMeshSubmissionStrategy.GpuIndirectInstrumented, "yes", false, false)]
    [TestCase(EMeshSubmissionStrategy.GpuIndirectInstrumented, "1", false, true)]
    [TestCase(EMeshSubmissionStrategy.GpuIndirectInstrumented, " true ", false, true)]
    [TestCase(EMeshSubmissionStrategy.GpuIndirectInstrumented, null, true, true)]
    [TestCase(EMeshSubmissionStrategy.CpuDirect, "1", true, false)]
    [TestCase(EMeshSubmissionStrategy.GpuIndirectZeroReadback, "1", true, false)]
    [TestCase(EMeshSubmissionStrategy.GpuMeshletInstrumented, "1", true, false)]
    public void CpuIndirectReferenceOverride_RequiresInstrumentedIndirectAndExplicitOptIn(
        EMeshSubmissionStrategy strategy,
        string? environmentValue,
        bool configuredDebugValue,
        bool expected)
    {
        GPURenderPassCollection.ResolveForceCpuIndirectBuild(strategy, environmentValue, configuredDebugValue)
            .ShouldBe(expected);
    }

    [Test]
    [NonParallelizable]
    public void CpuIndirectReferenceOverride_ReadsCachedEnvironmentValue()
    {
        string variable = XREngineEnvironmentVariables.ForceCpuIndirectBuild;
        string? previous = Environment.GetEnvironmentVariable(variable);
        bool previousDebugValue = GPURenderPassCollection.IndirectDebug.ForceCpuIndirectBuild;

        try
        {
            variable.ShouldBe("XRE_FORCE_CPU_INDIRECT_BUILD");
            GPURenderPassCollection.ConfigureIndirectDebug(settings => settings.ForceCpuIndirectBuild = false);
            Environment.SetEnvironmentVariable(variable, " true ");
            EffectiveSettingsEnvOverrides.ReloadForTests();

            GPURenderPassCollection.ShouldForceCpuIndirectBuild(EMeshSubmissionStrategy.GpuIndirectInstrumented)
                .ShouldBeTrue();
            GPURenderPassCollection.ShouldForceCpuIndirectBuild(EMeshSubmissionStrategy.GpuIndirectZeroReadback)
                .ShouldBeFalse();

            Environment.SetEnvironmentVariable(variable, "invalid");
            EffectiveSettingsEnvOverrides.ReloadForTests();
            GPURenderPassCollection.ShouldForceCpuIndirectBuild(EMeshSubmissionStrategy.GpuIndirectInstrumented)
                .ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            EffectiveSettingsEnvOverrides.ReloadForTests();
            GPURenderPassCollection.ConfigureIndirectDebug(settings => settings.ForceCpuIndirectBuild = previousDebugValue);
        }
    }

    [Test]
    public void CpuIndirectReferenceBatches_FollowCpuBuiltMaterialOrderAndCount()
    {
        uint[] materialOrder = [11u, 11u, 22u, 22u, 22u, 0u, 33u];

        var batches = HybridRenderingManager.BuildCpuMaterialBatches(materialOrder, 6u);

        batches.Count.ShouldBe(3);
        batches[0].Offset.ShouldBe(0u);
        batches[0].Count.ShouldBe(2u);
        batches[0].MaterialID.ShouldBe(11u);
        batches[1].Offset.ShouldBe(2u);
        batches[1].Count.ShouldBe(3u);
        batches[1].MaterialID.ShouldBe(22u);
        batches[2].Offset.ShouldBe(5u);
        batches[2].Count.ShouldBe(1u);
        batches[2].MaterialID.ShouldBe(uint.MaxValue);
    }

    [Test]
    public void CpuIndirectReferenceOverride_RebuildsMaterialBatchedIndirectCommands()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string method = SliceMethod(
            source,
            "private void RenderTraditionalBatched",
            "internal static List<DrawBatch> BuildCpuMaterialBatches");

        method.ShouldContain("GPURenderPassCollection.ShouldForceCpuIndirectBuild(renderPasses.MeshSubmissionStrategy)");
        method.ShouldContain("cpuBuiltCount = BuildIndirectCommandsCpu(");
        method.ShouldContain("overrideBatches = BuildCpuMaterialBatches(cpuMaterialOrder, cpuBuiltCount);");
        method.ShouldContain("var activeBatches = CoalesceContiguousBatches(overrideBatches ?? batches);");
    }
    [Test]
    public void BuildAccelerationStructure_UsesGpuBvhForGpuSubmissionStrategies()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_BuildAccelerationStructure.cs");

        source.ShouldContain("EMeshSubmissionStrategy strategy = ResolveEffectiveMeshSubmissionStrategy();");
        source.ShouldContain("viewport?.MeshSubmissionStrategyOverride");
        source.ShouldContain("VulkanFeatureProfile.ResolveGpuBvhUsage(strategy)");
        source.ShouldContain("gpuScene.UseGpuBvh = useGpuBvh;");
        source.ShouldContain("gpuScene.UseInternalBvh = useGpuBvh;");
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
        source.ShouldContain("private bool IsCpuFallbackRequestedForPass()");
        source.ShouldContain("if (!IsCpuFallbackRequestedForPass())");
        source.ShouldContain("LogCpuFallbackSuppressed(\"GPU frustum cull\")");
        source.ShouldContain("LogCpuFallbackSuppressed(\"GPU BVH cull\")");
        source.ShouldContain("LogCpuFallbackSuppressed(\"GPU pass filter\")");
        source.ShouldContain("LogGpuBvhFallback(\"GPU BVH resources are not ready\")");
        source.ShouldContain("using flat GPU frustum culling until it is ready");
    }

    [Test]
    public void Phase3_OcclusionPolicy_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs");

        source.ShouldContain("return VulkanFeatureProfile.ResolveOcclusionCullingMode(RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode);");
        source.ShouldNotContain("CpuQueryAsync && VulkanFeatureProfile.ActiveProfile != EVulkanGpuDrivenProfile.Diagnostics");
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
