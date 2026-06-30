using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuIndirectPhase7ZeroReadbackTests
{
    [Test]
    public void ZeroReadback_ShippingMode_ZeroGpuReadbackBytes_FullFrame()
    {
        bool previousTracking = XREngine.Engine.Rendering.Stats.EnableTracking;
        try
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = true;
            GPURenderPassCollection.ConfigureIndirectDebug(d =>
            {
                d.DisableCpuReadbackCount = true;
                d.EnableCpuBatching = false;
                d.ForceCpuFallbackCount = false;
                d.ForceCpuIndirectBuild = false;
            });

            XREngine.Engine.Rendering.Stats.BeginFrame();
            XREngine.Engine.Rendering.Stats.BeginFrame();

            XREngine.Engine.Rendering.Stats.GpuReadback.GpuReadbackBytes.ShouldBe(
                0L,
                customMessage: "A representative zero-readback frame must finish without recording GPU->CPU bytes.");
        }
        finally
        {
            XREngine.Engine.Rendering.Stats.EnableTracking = previousTracking;
        }

        string renderPassSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");
        renderPassSource.ShouldContain("BuildIndirectCommandBuffer(scene);");
        renderPassSource.ShouldContain("EnableZeroReadbackMaterialScatter");
        renderPassSource.ShouldContain("DispatchMaterialScatter(scene);");
        renderPassSource.ShouldContain("#if XRE_DEBUG_BATCH_RANGE_READBACK");
    }

    [Test]
    public void LegacyBatchRangeReadback_IsCompileFlaggedOutOfShippingPath()
    {
        string initSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ShadersAndInit.cs");
        string passSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");

        initSource.ShouldContain("#if XRE_DEBUG_BATCH_RANGE_READBACK\n            _buildGpuBatchesComputeShader");
        passSource.ShouldContain("#if XRE_DEBUG_BATCH_RANGE_READBACK\n        private void DispatchBuildGpuBatches");
        passSource.ShouldContain("#if XRE_DEBUG_BATCH_RANGE_READBACK\n        private List<HybridRenderingManager.DrawBatch>? ReadGpuBatchRanges()");

        string shippingFallback = Slice(passSource, "#else\n            // Shipping/default builds", "#endif", StringComparison.Ordinal);
        shippingFallback.ShouldNotContain("ReadGpuBatchRanges");
        shippingFallback.ShouldNotContain("DispatchBuildGpuBatches");
    }

    [Test]
    public void GpuDrivenStrategies_UseMaterialScatterForVisualDiagnosticsAndMeshletParity()
    {
        string coreSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs");
        string passSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");

        string policySnapshot = Slice(
            coreSource,
            "private void CapturePassPolicySnapshot()",
            "AssertZeroReadbackProductionInvariantsForPass(strategy);",
            StringComparison.Ordinal);

        policySnapshot.ShouldContain("bool meshlet = strategy.IsAnyMeshletStrategy();");
        policySnapshot.ShouldContain("_passZeroReadbackMaterialDrawPath = EZeroReadbackMaterialDrawPath.MaterialTable;");
        policySnapshot.ShouldContain("_passEnableZeroReadbackMaterialScatter = zeroReadback || instrumented || meshlet;");
        policySnapshot.ShouldContain("_passDisableCpuReadbackCount = !instrumented;");

        string materialTableRequirement = Slice(
            passSource,
            "private bool PrepareMaterialTableAndValidateResidency",
            "if (!VulkanFeatureProfile.EnableBindlessMaterialTable",
            StringComparison.Ordinal);

        materialTableRequirement.ShouldContain("bool materialTableRequired = EnableZeroReadbackMaterialScatter &&");
    }

    [Test]
    public void GpuOcclusionTelemetry_UsesActualPassStrategyAndSocFilter()
    {
        string occlusionSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs");
        string commandCollectionSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");
        string telemetrySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Occlusion/OcclusionTelemetry.cs");
        string panelSource = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.OcclusionPanel.cs");

        string applyOcclusion = Slice(
            occlusionSource,
            "private void ApplyOcclusionCulling",
            "private void LogOcclusionModeActivation",
            StringComparison.Ordinal);

        applyOcclusion.ShouldContain("mode, MeshSubmissionStrategy);");
        applyOcclusion.ShouldContain("ApplyCpuSoftwareOcclusionToGpuCulledCommands(scene, candidates);");

        string renderGpu = Slice(
            commandCollectionSource,
            "public void RenderGPU(int renderPass, EMeshSubmissionStrategy meshSubmissionStrategy)",
            "public bool HasRenderingCommands",
            StringComparison.Ordinal);

        renderGpu.ShouldContain("meshSubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectInstrumented");
        renderGpu.ShouldContain("PrepareCpuSoftwareOcclusion(renderPass, xrCamera);");

        telemetrySource.ShouldContain("GpuDepthSourceHistory");
        telemetrySource.ShouldContain("RecordGpuDepthSource(bool history)");
        panelSource.ShouldContain("Depth Source      : history=");
    }

    [Test]
    public void CpuQueryAsyncOcclusion_KeepsResolvedOccludedCommandsVisibleDuringCameraMotion()
    {
        string occlusionSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs");

        string cpuQueryAsync = Slice(
            occlusionSource,
            "private void ApplyCpuQueryAsyncOcclusion",
            "private static bool HasSignificantCameraChange",
            StringComparison.Ordinal);

        cpuQueryAsync.ShouldContain("out cameraMoved");
        cpuQueryAsync.ShouldContain("SubmitCpuOcclusionQueryBatch(scene, camera, candidates, cameraMoved);");
        cpuQueryAsync.ShouldContain("ApplyTemporalCpuOcclusionFilter(candidates, cameraMoved, ref temporalOverrides, ref falsePositiveRecoveries);");

        string submit = Slice(
            occlusionSource,
            "private void SubmitCpuOcclusionQueryBatch",
            "private uint ApplyTemporalCpuOcclusionFilter",
            StringComparison.Ordinal);

        submit.ShouldContain("int retestPeriod = cameraMoved ? 1 : Math.Max(1, TemporalOcclusionHysteresisFrames * 3);");

        string filter = Slice(
            occlusionSource,
            "private uint ApplyTemporalCpuOcclusionFilter",
            "private void ResolveCpuOcclusionQueryResults",
            StringComparison.Ordinal);

        filter.ShouldContain("if (cameraMoved)");
        filter.ShouldContain("temporalOverrides++;");
        filter.ShouldContain("else if (state.ConsecutiveOccludedFrames >= TemporalOcclusionHysteresisFrames)");
    }

    [Test]
    public void MeshletPass_DoesNotRunCpuRenderBeforeGpuMeshlets()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Meshlet/VPRC_RenderMeshesPassMeshlet.cs");

        source.ShouldContain("gpuPass.UseMeshletPipeline = true;");
        source.ShouldContain("activeInstance.MeshRenderCommands.RenderGPU(command.RenderPass, command.MeshSubmissionStrategy);");
        source.ShouldContain("ShouldUseOpenGLMeshletProgramWarmupFallback");
        source.ShouldContain("RenderCPUMeshOnly(command.RenderPass);");
        source.ShouldNotContain("RenderCPU(");
    }

    [Test]
    public void RenderGPU_MeshletStrategy_SetsMeshletPipelineIntentForDirectCallers()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");
        string renderGpu = Slice(
            source,
            "public void RenderGPU(int renderPass, EMeshSubmissionStrategy meshSubmissionStrategy)",
            "public bool HasRenderingCommands",
            StringComparison.Ordinal);

        renderGpu.ShouldContain("bool meshletStrategy = meshSubmissionStrategy.IsAnyMeshletStrategy();");
        renderGpu.ShouldContain("bool previousUseMeshletPipeline = gpuPass.UseMeshletPipeline;");
        renderGpu.ShouldContain("gpuPass.UseMeshletPipeline = true;");
        renderGpu.ShouldContain("scene is GPUScene gpuScene");
        renderGpu.ShouldContain("gpuScene.EnsureRuntimeMeshletPayloadsForMeshletDispatch();");
        renderGpu.ShouldContain("finally");
        renderGpu.ShouldContain("gpuPass.UseMeshletPipeline = previousUseMeshletPipeline;");
    }

    [Test]
    public void ForwardDepthNormalPrepass_UsesResolvedStrategyAndMirrorsGpuPrefilter()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardDepthNormalPrePass.cs");

        source.ShouldContain("EMeshSubmissionStrategy strategy = _gpuDispatch");
        source.ShouldContain("EMeshSubmissionStrategy prepassStrategy = ResolveDepthNormalSubmissionStrategy(strategy);");
        source.ShouldContain("private static EMeshSubmissionStrategy ResolveDepthNormalSubmissionStrategy");
        source.ShouldContain("strategy == EMeshSubmissionStrategy.GpuMeshletInstrumented");
        source.ShouldContain("bool useGpuRenderPath = prepassStrategy != EMeshSubmissionStrategy.CpuDirect;");
        source.ShouldContain("if (useGpuRenderPath)");
        source.ShouldContain("commands.RenderCPUNonMeshAndExcluded(pass);");
        source.ShouldContain("commands.RenderGPU(pass, prepassStrategy);");
        source.ShouldContain("commands.RenderCPU(pass, false, camera);");
        source.ShouldNotContain("allowExcludedGpuFallbackMeshes: false");
    }

    [Test]
    public void FullOverdraw_UsesGpuRenderPath_WhenGpuSubmissionIsActive()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_RenderFullOverdrawPass.cs");

        source.ShouldContain("EMeshSubmissionStrategy overdrawStrategy = ResolveOverrideSubmissionStrategy(");
        source.ShouldContain("private static EMeshSubmissionStrategy ResolveOverrideSubmissionStrategy");
        source.ShouldContain("bool useGpuRenderPath = overdrawStrategy != EMeshSubmissionStrategy.CpuDirect;");
        source.ShouldContain("IsGpuPathCpuFallbackMesh(mesh)");
        source.ShouldContain("commands.RenderGPU(pass, overdrawStrategy);");
        source.ShouldContain("return meshCommand.ForceCpuRendering || material?.RenderOptions?.ExcludeFromGpuIndirect == true;");
    }

    [Test]
    public void ForwardPlusLightCulling_ExecutesInsideDeclaredRenderGraphPass()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardPlusLightCullingPass.cs");

        string execute = Slice(
            source,
            "protected override void Execute()",
            "private static List<ForwardPlusLocalLight> BuildLocalLights",
            StringComparison.Ordinal);

        execute.ShouldContain("ResolvePassIndex(nameof(VPRC_ForwardPlusLightCullingPass))");
        execute.ShouldContain("RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)");

        string describe = Slice(
            source,
            "internal override void DescribeRenderPass",
            "private int ResolvePassIndex",
            StringComparison.Ordinal);

        describe.ShouldContain("context.GetOrCreateSyntheticPass(nameof(VPRC_ForwardPlusLightCullingPass), ERenderGraphPassStage.Compute)");
    }

    [Test]
    public void SkinnedBounds_ZeroReadbackPath_UsesGpuResidentDirectWriteWithoutWaitForGpu()
    {
        string renderableSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs");
        string bvhCommandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_BuildAccelerationStructure.cs");

        renderableSource.ShouldContain("ShouldUseGpuResidentSkinnedBoundsPath");
        renderableSource.ShouldContain("RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy().IsGpuZeroReadbackStrategy()");
        renderableSource.ShouldContain("ApplyGpuResidentSkinnedBoundsDispatchLocked()");
        bvhCommandSource.ShouldContain("RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy().IsGpuZeroReadbackStrategy()");

        string directPath = Slice(
            renderableSource,
            "private bool ApplyGpuResidentSkinnedBoundsDispatchLocked()",
            "private static bool TryComputeSkinnedBoundsOnCpu",
            StringComparison.Ordinal);

        directPath.ShouldContain("DispatchPathADirectWrite");
        directPath.ShouldNotContain("WaitForGpu");
        directPath.ShouldNotContain("TryComputeSkinnedBoundsOnGpu");
    }

    private static string Slice(string source, string startToken, string endToken, StringComparison comparison)
    {
        string normalized = source.Replace("\r\n", "\n");
        int start = normalized.IndexOf(startToken, comparison);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find start token '{startToken}'.");

        int end = normalized.IndexOf(endToken, start + startToken.Length, comparison);
        end.ShouldBeGreaterThan(start, $"Could not find end token '{endToken}' after '{startToken}'.");

        return normalized[start..end];
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = ResolveWorkspaceRoot();
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file to exist: {relativePath}");
        return File.ReadAllText(fullPath).Replace("\r\n", "\n");
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
