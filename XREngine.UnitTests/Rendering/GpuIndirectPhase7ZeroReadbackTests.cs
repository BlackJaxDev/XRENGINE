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

            XREngine.Engine.Rendering.Stats.GpuReadbackBytes.ShouldBe(
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

        policySnapshot.ShouldContain("bool meshlet = strategy == EMeshSubmissionStrategy.GpuMeshlet;");
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
    public void MeshletPass_DoesNotRunCpuRenderBeforeGpuMeshlets()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Meshlet/VPRC_RenderMeshesPassMeshlet.cs");

        source.ShouldContain("gpuPass.UseMeshletPipeline = true;");
        source.ShouldContain("activeInstance.MeshRenderCommands.RenderGPU(command.RenderPass, command.MeshSubmissionStrategy);");
        source.ShouldNotContain("RenderCPU(");
    }

    [Test]
    public void FullOverdraw_UsesGpuRenderPath_WhenGpuSubmissionIsActive()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_RenderFullOverdrawPass.cs");

        source.ShouldContain("EMeshSubmissionStrategy overdrawStrategy = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();");
        source.ShouldContain("bool useGpuRenderPath = overdrawStrategy != EMeshSubmissionStrategy.CpuDirect;");
        source.ShouldContain("IsGpuPathCpuFallbackMesh(mesh)");
        source.ShouldContain("commands.RenderGPU(pass, overdrawStrategy);");
        source.ShouldContain("return meshCommand.ForceCpuRendering || material?.RenderOptions?.ExcludeFromGpuIndirect == true;");
    }

    [Test]
    public void SkinnedBounds_ZeroReadbackPath_UsesGpuResidentDirectWriteWithoutWaitForGpu()
    {
        string renderableSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs");
        string bvhCommandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_BuildAccelerationStructure.cs");

        renderableSource.ShouldContain("ShouldUseGpuResidentSkinnedBoundsPath");
        renderableSource.ShouldContain("RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy() == EMeshSubmissionStrategy.GpuIndirectZeroReadback");
        renderableSource.ShouldContain("ApplyGpuResidentSkinnedBoundsDispatchLocked()");
        bvhCommandSource.ShouldContain("RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy() == EMeshSubmissionStrategy.GpuIndirectZeroReadback");

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
