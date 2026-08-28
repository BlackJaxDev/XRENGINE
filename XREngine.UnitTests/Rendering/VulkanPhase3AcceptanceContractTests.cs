using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Materials;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Guards the mutation, variant, delayed-diagnostic, scaling, and allocation
/// contracts required to promote workstream 03.
/// </summary>
[TestFixture]
public sealed class VulkanPhase3AcceptanceContractTests
{
    [Test]
    public void CapacityControls_SupportMultiplierAndExactFixedFloor()
    {
        GpuDrivenValidationCapacity.ResolveFloor(null).ShouldBe(0u);
        GpuDrivenValidationCapacity.ResolveFloor("65536").ShouldBe(65536u);
        GpuDrivenValidationCapacity.Apply(17u, 4u, 4096u).ShouldBe(4096u);
        GpuDrivenValidationCapacity.Apply(2048u, 4u, 4096u).ShouldBe(8192u);
        Should.Throw<InvalidOperationException>(
            () => GpuDrivenValidationCapacity.ResolveFloor("4294967295"));

        string environment = Read(
            "XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string profile = Read("XREngine.Runtime.Bootstrap/Engine/Engine.ProfileCapture.cs");
        string runner = Read("Tools/Benchmarks/Invoke-VulkanPerf.ps1");
        environment.ShouldContain("XRE_GPU_DRIVEN_VALIDATION_CAPACITY_FLOOR");
        profile.ShouldContain("\"gpu_driven_validation_capacity_floor\"");
        runner.ShouldContain("XRE_GPU_DRIVEN_VALIDATION_CAPACITY_FLOOR");
    }

    [Test]
    public void MaterialEditsAndStreamingPublication_AreRowScopedAndFrameSlotSafe()
    {
        using GPUMaterialTable table = new(initialCapacity: 8u, initialHandleCapacity: 8u);
        table.AddOrUpdate(
            2u,
            new GPUMaterialEntry { Flags = 1u },
            new GPUMaterialTextureReferences(
                GPUMaterialTextureReference.FromVulkanDescriptorIndex(5u),
                GPUMaterialTextureReference.FromVulkanDescriptorIndex(6u),
                GPUMaterialTextureReference.None));
        table.AddOrUpdate(
            2u,
            new GPUMaterialEntry { Flags = 2u },
            new GPUMaterialTextureReferences(
                GPUMaterialTextureReference.FromVulkanDescriptorIndex(7u),
                GPUMaterialTextureReference.FromVulkanDescriptorIndex(8u),
                GPUMaterialTextureReference.None));

        table.MaterialDirtyRange.FirstIndex.ShouldBe(2u);
        table.MaterialDirtyRange.IndexCount.ShouldBe(1u);
        ReadUInt(table.Buffer, 2u, 0u).ShouldBe(7u);
        ReadUInt(table.Buffer, 2u, 1u).ShouldBe(8u);
        ReadUInt(table.Buffer, 2u, 3u).ShouldBe(2u);
        table.ActiveTextureHandles.ShouldBeEmpty();

        string material = Read(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string state = Read(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        string pipeline = Read(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");

        material.ShouldContain("DescriptorSlotRequiresPublication(");
        state.ShouldContain("CanUpdateCompletedDescriptorFrameSlot(");
        material.ShouldContain(
            "state.SlotResourceFingerprints[resolvedFrame] = resourceFingerprint;");
        state.ShouldContain("HasTimelineValueCompleted(");
        pipeline.ShouldContain("RenderResourceChangeKind.CompatibleContentPublication");
    }

    [Test]
    public void RequiredVariants_AreCoveredOrPromotionBlocking()
    {
        string manager = Read("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string pass = Read(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");
        string viewPolicy = Read(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ViewBatchClassification.cs");
        string shader = Read(
            "Build/CommonAssets/Shaders/Compute/Indirect/GPURenderMaterialScatter.comp");
        string scene = Read(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Soa.cs");
        string evaluator = Read(
            "XREngine.Benchmarks/VulkanPerformance/VulkanPerformanceEvaluator.cs");

        manager.ShouldContain("DepthNormalPrePassVariant");
        manager.ShouldContain("RuntimeEngine.Rendering.State.OverrideMaterial");
        manager.ShouldContain("ReportDeclaredUnsupportedCompactPass(");
        pass.ShouldContain(
            "ResolveEffectiveGpuMaterial(material, overrideMaterial, useDepthNormalMaterialVariants)");
        scene.ShouldContain("return EGpuMaterialStateClass.Shadow;");
        shader.ShouldContain("RejectExactTransparentMultiview");
        shader.ShouldContain("domain != DOMAIN_EXACT");
        viewPolicy.ShouldContain("RecordUnsupportedCompactPass(renderPass)");
        evaluator.ShouldContain("\"UnsupportedCompactVariant\"");
    }

    [Test]
    public void DelayedDiagnosticsAndVisibilityBypass_KeepCurrentSubmissionGpuOwned()
    {
        string readback = Read(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.GpuStatsReadback.cs");
        string pass = Read(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");

        readback.ShouldContain("Api!.GetFenceStatus(device, slot.Fence)");
        readback.ShouldContain("RecordDelayedDiagnosticReadback(slot.ByteCount)");
        readback.ShouldNotContain("WaitForFences");
        pass.ShouldContain("VulkanDelayedCounterDiagnosticsEnabled");
        pass.ShouldContain("if (!captureDiagnostics)");

        typeof(IGpuCompactVisibilityInput).GetProperties()
            .Select(static property => property.Name)
            .ShouldBe([
                nameof(IGpuCompactVisibilityInput.CommandIds),
                nameof(IGpuCompactVisibilityInput.CommandCount),
                nameof(IGpuCompactVisibilityInput.Capacity),
                nameof(IGpuCompactVisibilityInput.ResourceGeneration),
                nameof(IGpuCompactVisibilityInput.IsConservativeBypass),
            ]);
    }

    [Test]
    public void CompactSubmissionAllocations_AreMeasuredAndPromotionBlocking()
    {
        string manager = Read("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string stats = Read(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.GpuDriven.cs");
        string materialBindings = Read(
            "XREngine.Runtime.Rendering/Rendering/Materials/MaterialBindingLayout.cs");
        string profile = Read("XREngine.Runtime.Bootstrap/Engine/Engine.ProfileCapture.cs");
        string measure = Read("Tools/Measure-GameLoopRenderPipeline.ps1");
        string evaluator = Read(
            "XREngine.Benchmarks/VulkanPerformance/VulkanPerformanceEvaluator.cs");
        string runner = Read("Tools/Benchmarks/Invoke-VulkanPerf.ps1");

        manager.ShouldContain("GC.GetAllocatedBytesForCurrentThread()");
        manager.ShouldContain("RecordSubmissionManagedAllocation(");
        stats.ShouldContain("SubmissionManagedAllocatedBytes");
        stats.ShouldContain("SubmissionBackendManagedAllocatedBytes");
        stats.ShouldContain("SubmissionOwnedManagedAllocatedBytes");
        stats.ShouldNotContain("_materialBindingRung = rung.ToString();");
        materialBindings.ShouldContain("public readonly record struct MaterialBindingResolverResult(");
        profile.ShouldContain("\"gpu_driven_submission_managed_allocated_bytes\"");
        profile.ShouldContain("\"gpu_driven_submission_backend_managed_allocated_bytes\"");
        profile.ShouldContain("\"gpu_driven_submission_owned_managed_allocated_bytes\"");
        measure.ShouldContain("GpuDrivenSubmissionManagedAllocatedBytesTotal");
        measure.ShouldContain("GpuDrivenSubmissionBackendManagedAllocatedBytesTotal");
        measure.ShouldContain("GpuDrivenSubmissionOwnedManagedAllocatedBytesTotal");
        evaluator.ShouldContain("\"ZeroReadbackSubmissionAllocation\"");
        runner.ShouldNotContain("FailOnSteadyStateCommandBufferAllocations = $true");
        runner.ShouldNotContain("MaxSteadyStateRecordCommandBufferAllocatedBytes = 0");
    }

    [Test]
    public void AcceptanceComparator_EnforcesScalingAndCrossoverContracts()
    {
        string comparator = Read(
            "Tools/Benchmarks/Compare-VulkanPhase3Acceptance.ps1");
        string measure = Read("Tools/Measure-GameLoopRenderPipeline.ps1");
        string runner = Read("Tools/Benchmarks/Invoke-VulkanPerf.ps1");
        string profile = Read("XREngine.Runtime.Bootstrap/Engine/Engine.ProfileCapture.cs");
        string settings1X = Read(
            "XREngine.Benchmarks/VulkanPerformance/Cohorts/phase3-active-1x.jsonc");
        string settings4X = Read(
            "XREngine.Benchmarks/VulkanPerformance/Cohorts/phase3-active-4x.jsonc");
        string settings16X = Read(
            "XREngine.Benchmarks/VulkanPerformance/Cohorts/phase3-active-16x.jsonc");

        comparator.ShouldContain("phase3-capacity-1x-active-fixed");
        comparator.ShouldContain("phase3-capacity-16x-active-fixed");
        comparator.ShouldContain("phase3-active-1x-capacity-fixed");
        comparator.ShouldContain("phase3-active-16x-capacity-fixed");
        comparator.ShouldContain("phase3-high-count-zero-readback");
        comparator.ShouldContain("phase3-high-count-cpu-direct");
        comparator.ShouldContain("phase3-high-count-full-scan");
        comparator.ShouldContain("render_outside_vulkan_frame_ms");
        comparator.ShouldContain("vulkan_cpu_secondary_recording_ms");
        comparator.ShouldContain("VarianceThresholdPercent = 7.5");
        comparator.ShouldContain("LowCountMinimumAllowanceMilliseconds = 0.25");
        comparator.ShouldContain("$zeroP95 -le (0.95 * $cpuP95)");
        comparator.ShouldContain("$zeroP95 -le (0.95 * $scanP95)");
        comparator.ShouldContain("GpuSceneCommandCountP50");
        comparator.ShouldContain("$zeroCommands -ge 4096.0");
        comparator.ShouldContain("$cpuCommands -ge 4096.0");
        comparator.ShouldContain("$scanCommands -ge 4096.0");
        comparator.ShouldContain("$matchedSceneSettings");
        comparator.ShouldContain("ScalingActiveHandoff");
        comparator.ShouldContain("VulkanEligiblePrimaryCommandBufferReuseDecisionsTotal");
        measure.ShouldContain("MinSteadyStateGpuSceneCommandCount");
        measure.ShouldContain("GPU-scene command topology not ready");
        measure.ShouldContain("UseEligiblePrimaryReuseRatio");
        measure.ShouldContain("VulkanEligiblePrimaryCommandBufferReuseRatio");
        runner.ShouldContain("minimumGpuSceneCommandCount");
        runner.ShouldContain("UnitTestingWorldSettingsPath = $settingsPath");
        runner.ShouldContain("UseEligiblePrimaryReuseRatio = $true");
        runner.ShouldContain("Persist-CohortFrameStreams");
        runner.ShouldContain("GateScope =");
        profile.ShouldContain("\"gpu_scene_command_count\"");
        settings1X.ShouldContain("\"UnitBoxCount\": 256");
        settings1X.ShouldContain("\"Locomotion\": false");
        settings1X.ShouldContain("\"DirLightCastsShadows\": false");
        settings4X.ShouldContain("\"UnitBoxCount\": 1024");
        settings16X.ShouldContain("\"UnitBoxCount\": 4096");
        settings16X.ShouldContain("\"UnitBoxMaterialCount\": 512");
    }

    private static uint ReadUInt(XRDataBuffer buffer, uint row, uint wordIndex)
    {
        uint offset = (row * buffer.ElementSize) + (wordIndex * sizeof(uint));
        uint? value = buffer.Get<uint>(offset);
        value.HasValue.ShouldBeTrue();
        return value.Value;
    }

    private static string Read(string relativePath)
        => SourceContractWorkspace
            .ReadFile(relativePath)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
}
