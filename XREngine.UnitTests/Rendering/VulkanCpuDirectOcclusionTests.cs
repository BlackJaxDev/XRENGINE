using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCpuDirectOcclusionTests
{
    [Test]
    public void CpuQueryAsync_VulkanCpuDirect_UsesRequestedModeAndRecordsQueryFrameOps()
    {
        string coordinator = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs");
        string cpuDirect = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");
        string gpuOcclusion = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs");
        string frameOps = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string recorder = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string query = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Queries/VkRenderQuery.cs");
        string renderGraph = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");

        coordinator.ShouldContain("using XREngine.Rendering.Vulkan;");
        coordinator.ShouldContain("AbstractRenderer.Current is OpenGLRenderer or VulkanRenderer");
        coordinator.ShouldContain("vk.EnqueueOcclusionQueryBegin(query, EQueryTarget.AnySamplesPassedConservative)");
        coordinator.ShouldContain("vk.EnqueueOcclusionQueryEnd(query)");
        coordinator.ShouldContain("VulkanQueryResolveMinLatencyFrames");
        coordinator.ShouldContain("ShouldDelayPendingQueryPoll(queryState, frameId)");
        coordinator.ShouldContain("AbstractRenderer.Current is not VulkanRenderer");

        cpuDirect.ShouldContain("EOcclusionCullingMode occlusionMode = RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode;");
        cpuDirect.ShouldContain("occlusionMode == EOcclusionCullingMode.CpuQueryAsync");
        cpuDirect.ShouldContain("ShouldSuppressOcclusionForCurrentPass(suppressCpuOcclusionForPass, out bool isShadowPass, out bool isDepthNormalPrePass)");
        cpuDirect.ShouldContain("suppressCpuOcclusionForPass = false");
        cpuDirect.ShouldContain("!suppressOcclusion &&");
        cpuDirect.ShouldContain("depthNormalPrePass: isDepthNormalPrePass");
        cpuDirect.ShouldNotContain("UseDepthNormalMaterialVariants");
        cpuDirect.ShouldNotContain("ResolveCpuDirectOcclusionMode");
        cpuDirect.ShouldNotContain("XREngineEnvironmentVariables.VulkanCpuQueryOcclusion");
        cpuDirect.ShouldNotContain("return EOcclusionCullingMode.Disabled;");

        string depthPrepass = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardDepthNormalPrePass.cs");
        depthPrepass.ShouldContain("commands.RenderCPU(pass, false, camera, suppressCpuOcclusionForPass: true);");

        gpuOcclusion.ShouldContain("return VulkanFeatureProfile.ResolveOcclusionCullingMode(RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode);");
        gpuOcclusion.ShouldNotContain("_loggedCpuQueryModeSuppressedByProfile");
        gpuOcclusion.ShouldNotContain("CpuQueryAsync && VulkanFeatureProfile.ActiveProfile != EVulkanGpuDrivenProfile.Diagnostics");
        gpuOcclusion.ShouldNotContain("Occlusion mode {0} suppressed");

        frameOps.ShouldContain("internal sealed record QueryOp(");
        frameOps.ShouldContain("internal bool EnqueueOcclusionQueryBegin(XRRenderQuery query, EQueryTarget target)");
        frameOps.ShouldContain("internal bool EnqueueOcclusionQueryEnd(XRRenderQuery query)");
        frameOps.ShouldContain("EnqueueFrameOp(new QueryOp(");

        recorder.ShouldContain("bool hasQueryFrameOps = HasQueryFrameOps(ops)");
        recorder.ShouldContain("PrepareQueryFrameOpsForCommandBufferReuse");
        recorder.ShouldNotContain("(!VulkanPrimaryCommandBufferReuseEnabled || hasQueryFrameOps)");
        recorder.ShouldContain("private static bool HasQueryFrameOps(FrameOp[] ops)");
        recorder.ShouldContain("case QueryOp queryOp:");
        recorder.ShouldContain("queryOp.Query.BeginQuery(commandBuffer, queryOp.QueryTarget)");
        recorder.ShouldContain("queryOp.Query.EndQuery(commandBuffer);");

        query.ShouldContain("CmdResetQueryPool(commandBuffer, _queryPool, 0, _activeQueryCount)");
        query.ShouldContain("PrepareForCommandBufferReuse(EQueryTarget target)");
        query.ShouldContain("ResetQueryPoolForCommandBufferReuse");
        query.ShouldContain("QueryCount = 2");
        query.ShouldContain("_activeQueryCount = Math.Clamp(queryCount, 1u, 2u)");
        query.ShouldContain("DestroyQueryPool();");

        renderGraph.ShouldContain("op is MeshDrawOp or QueryOp or BlitOp");
        renderGraph.ShouldContain("QueryOp q => q.Target is null");
    }

    [Test]
    public void CpuSoftwareOcclusion_FilteredDebugPath_UsesSameSocCullerAsPrimaryCpuPath()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");
        string culler = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Occlusion/CpuSoftwareOcclusionCuller.cs");
        string telemetry = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Occlusion/OcclusionTelemetry.cs");
        string panel = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.OcclusionPanel.cs");

        string filtered = Slice(
            source,
            "public void RenderCPUFiltered(int renderPass, Predicate<RenderCommand> filter, bool respectCpuQueryOcclusion)",
            "private static void RenderWithGpuScope",
            StringComparison.Ordinal);

        filtered.ShouldContain("!useCpuQueryOcclusion");
        filtered.ShouldContain("PrepareCpuSoftwareOcclusion(renderPass, camera)");
        filtered.ShouldContain("CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(socMesh)");
        filtered.ShouldContain("s_cpuSoftwareOcclusionCuller.TestVisible(cmd.StableQueryKey, socBounds)");

        source.ShouldContain("LogSponzaCpuDiag(\"skip-cpu-soc\"");
        source.ShouldContain("OcclusionTelemetry.RecordCpuCulledOne();");
        culler.ShouldContain("OcclusionTelemetry.RecordCpuSocSelfOccluderSkipped();");
        telemetry.ShouldContain("CpuSocSelfOccluderSkipped");
        panel.ShouldContain("Self-Occluder Skips");
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
