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
        string queryFrameOp = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/VulkanRenderer.QueryOp.cs");
        string recorder = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string commandChains = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
        string openXr = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string query = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Queries/VkRenderQuery.cs");
        string resourceLifetime = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string renderGraph = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");
        string runtimeEngine = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");

        coordinator.ShouldContain("using XREngine.Rendering.Vulkan;");
        coordinator.ShouldContain("AbstractRenderer.Current is OpenGLRenderer or VulkanRenderer");
        coordinator.ShouldContain("vk.EnqueueOcclusionQueryBegin(query, EQueryTarget.AnySamplesPassedConservative)");
        coordinator.ShouldContain("vk.EnqueueOcclusionQueryEnd(query)");
        coordinator.ShouldContain("VulkanQueryResolveMinLatencyFrames");
        coordinator.ShouldContain("ShouldDelayPendingQueryPoll(queryState, frameId)");
        coordinator.ShouldContain("NormalizeBackendQueryResult(");
        coordinator.ShouldContain("ECpuOcclusionForceVisibleReason.UntrustedBackendNegativeResult");
        coordinator.ShouldContain("CpuOcclusion.VulkanNegativeResultQuarantined");
        coordinator.ShouldContain("AbstractRenderer.Current is not VulkanRenderer");
        coordinator.ShouldContain("ulong frameId = ++state.FrameEpoch;");
        coordinator.ShouldContain("EvictStaleOwnershipStates(globalFrameId)");
        coordinator.ShouldContain("ulong frameId = state.FrameEpoch;");

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
        runtimeEngine.ShouldContain("commandState?.RenderingCamera as XRCamera");
        runtimeEngine.ShouldContain("public static XRCamera.EDepthMode GetDepthMode() => RenderingCamera?.DepthMode");

        string visibleDemotionPath = Slice(
            cpuDirect,
            "// Query the conservative AABB immediately before the contributing draw.",
            "if (!useCpuQueryOcclusion && useCpuSocOcclusion",
            StringComparison.Ordinal);
        visibleDemotionPath.ShouldContain("TryScheduleVisibleProxyProbe(");
        visibleDemotionPath.ShouldContain("CpuOcclusionProxyRenderer.Draw(visibleProbeBounds, camera!);");
        visibleDemotionPath.ShouldContain("s_cpuOcclusionCoordinator.BeginQuery(renderPass, camera, queryKey, occlusionOwnership);");
        visibleDemotionPath.ShouldContain("s_cpuOcclusionCoordinator.EndQuery(renderPass, camera, queryKey, occlusionOwnership);");
        visibleDemotionPath.ShouldContain("RenderWithGpuScope(cmd, renderPass);");
        visibleDemotionPath.ShouldContain("CpuQueryProxyIsNearPlaneUnsafe(camera!, visibleProbeBounds)");

        string recoveryProbePath = Slice(
            cpuDirect,
            "// Phase 3: deferred AABB probes for occluded recovery.",
            "private static CpuOcclusionProbeCandidate CreateCpuOcclusionProbeCandidate(",
            StringComparison.Ordinal);
        recoveryProbePath.ShouldContain("CpuOcclusionProxyRenderer.Draw(probe.WorldBounds, camera!);");

        string proxyRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Occlusion/CpuOcclusionProxyRenderer.cs");
        proxyRenderer.ShouldContain("public static void Draw(in AABB worldBounds, XRCamera camera)");
        proxyRenderer.ShouldContain("RuntimeEngine.Rendering.State.RenderingCameraOverride = camera;");
        proxyRenderer.ShouldContain("RuntimeEngine.Rendering.State.RenderingCameraOverride = null;");

        string depthPrepass = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardDepthNormalPrePass.cs");
        depthPrepass.ShouldContain("commands.RenderCPU(pass, false, camera, suppressCpuOcclusionForPass: true);");

        string motionVectors = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_RenderMotionVectorsPass.cs");
        motionVectors.ShouldContain("commands.RenderCPU(pass, suppressCpuOcclusionForPass: true);");
        motionVectors.ShouldNotContain("commands.RenderCPU(pass);");

        gpuOcclusion.ShouldContain("return VulkanFeatureProfile.ResolveOcclusionCullingMode(RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode);");
        gpuOcclusion.ShouldNotContain("_loggedCpuQueryModeSuppressedByProfile");
        gpuOcclusion.ShouldNotContain("CpuQueryAsync && VulkanFeatureProfile.ActiveProfile != EVulkanGpuDrivenProfile.Diagnostics");
        gpuOcclusion.ShouldNotContain("Occlusion mode {0} suppressed");

        queryFrameOp.ShouldContain("internal sealed record QueryOp(");
        frameOps.ShouldContain("internal bool EnqueueOcclusionQueryBegin(XRRenderQuery query, EQueryTarget target)");
        frameOps.ShouldContain("internal bool EnqueueOcclusionQueryEnd(XRRenderQuery query)");
        frameOps.ShouldContain("EnqueueFrameOp(new QueryOp(");

        recorder.ShouldContain("bool hasQueryFrameOps = HasQueryFrameOps(ops)");
        recorder.ShouldContain("PrepareQueryFrameOpsForCommandBufferReuse");
        recorder.ShouldNotContain("(!VulkanPrimaryCommandBufferReuseEnabled || hasQueryFrameOps)");
        recorder.ShouldContain("private static bool HasQueryFrameOps(FrameOp[] ops)");
        recorder.ShouldContain("case QueryOp queryOp:");
        recorder.ShouldContain("activeInlineQuery = queryOp.Query.BeginQuery(");
        recorder.ShouldContain("queryOp.QueryTarget,");
        recorder.ShouldContain("queryOp.Query.EndQuery(commandBuffer);");
        recorder.ShouldContain("recordingScratch.PreparedInlineQueries.Clear();");
        recorder.ShouldContain("for (int prepareIndex = 0; prepareIndex < ops.Length; prepareIndex++)");
        recorder.ShouldContain("recordingScratch.PreparedInlineQueries.Add(pendingQuery.Query)");
        recorder.ShouldContain("pendingQuery.Query.PrepareForRecording(");
        recorder.ShouldContain("recordingScratch.PreparedInlineQueries.Contains(queryOp.Query)");
        recorder.ShouldNotContain("queryOp.Query.PrepareForRecording(commandBuffer");
        string fastPrimaryReuse = Slice(
            recorder,
            "private bool TryReuseCleanCommandChainPrimaryVariant(",
            "private bool TryRefreshReusableCommandBufferFrameData(",
            StringComparison.Ordinal);
        fastPrimaryReuse.ShouldContain("PrepareQueryFrameOpsForCommandBufferReuse(ops)");
        string scheduledMeshSecondary = Slice(
            recorder,
            "bool TryExecuteScheduledMeshCommandChainSecondaryRun(",
            "bool TryExecuteMeshCommandChainSecondaryRun(",
            StringComparison.Ordinal);
        scheduledMeshSecondary.ShouldNotContain("CommandChainsEnabledForCurrentRecording");
        scheduledMeshSecondary.ShouldContain("scheduledCommandChainKeysByOpIndex is null");

        commandChains.ShouldContain("LowerFrameOpsToRenderPacketsExcludingQueryBrackets");
        commandChains.ShouldContain("ordinal == -1 ? int.MaxValue : ordinal");
        commandChains.ShouldContain("MaxCommandChainsPerSchedule");
        commandChains.ShouldContain("queryBracketDepth == 0");
        commandChains.ShouldContain("Keeping occlusion query brackets inline while scheduling the remaining frame ops as command chains");
        commandChains.ShouldContain("allowExternalSwapchainTarget");
        commandChains.ShouldContain("allowExternalSwapchainTarget\n            ? CommandChainsEnabled");
        commandChains.ShouldContain("Dictionary<ulong, int> structuralOccurrences");
        commandChains.ShouldNotContain("return HashCode.Combine(sourceOrdinal, foldedStructuralSignature);");
        openXr.ShouldContain("commandChainSchedule = null;");
        openXr.ShouldContain("allowExternalSwapchainTarget: true");
        openXr.ShouldContain("openxr-primary-miss:cpu-query-async");
        openXr.ShouldContain("RebaseFrameOpResourcesToActiveResourcePlan(ops, plannerContext.ResourceRegistry)");
        openXr.ShouldContain("RebaseFrameOpTargetsToActiveResourcePlan");
        openXr.ShouldContain("snapshot.SamplersByName[pair.Key] = currentTexture");
        openXr.ShouldContain("? capturedTexture.Name");

        query.ShouldContain("Api!.CmdResetQueryPool(commandBuffer, _queryPool, 0, _queryPoolCapacity);");
        query.ShouldContain("PrepareForCommandBufferReuse(EQueryTarget target)");
        query.ShouldContain("ResetQueryPoolForCommandBufferReuse");
        query.ShouldContain("QueryCount = queryPoolCapacity");
        query.ShouldContain("_activeQueryCount = Math.Clamp(queryCount, 1u, _queryPoolCapacity)");
        query.ShouldContain("ResolveOcclusionQueryViewSlotCount(viewMask)");
        query.ShouldContain("DestroyQueryPool();");
        query.ShouldContain("_hasSubmittedResultEpoch");
        query.ShouldContain("Volatile.Read(ref _hasSubmittedResultEpoch) == 0");
        query.ShouldContain("Renderer.RegisterVulkanRenderQuery(_queryPool, this);");
        resourceLifetime.ShouldContain("key.Type == ObjectType.QueryPool");
        resourceLifetime.ShouldContain("query.MarkResultEpochSubmitted();");

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
