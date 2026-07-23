using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Vulkan;

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
        string resourcePlanner = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string renderGraph = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");
        string commandContainer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/ViewportRenderCommandContainer.cs");
        string runtimeEngine = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");
        string queryManager = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Occlusion/AsyncOcclusionQueryManager.cs");

        coordinator.ShouldContain("using XREngine.Rendering.Vulkan;");
        coordinator.ShouldContain("AbstractRenderer.Current is OpenGLRenderer or VulkanRenderer");
        coordinator.ShouldContain("vk.EnqueueOcclusionQueryBegin(query)");
        coordinator.ShouldContain("vk.EnqueueOcclusionQueryEnd(query)");
        coordinator.ShouldContain("VulkanQueryResolveMinLatencyFrames");
        coordinator.ShouldContain("ShouldDelayPendingQueryPoll(queryState, frameId)");
        coordinator.ShouldNotContain("NormalizeBackendQueryResult(");
        coordinator.ShouldNotContain("CpuOcclusion.VulkanNegativeResultQuarantined");
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
            "// A visible-demotion query must bracket the exact contributing draw at",
            "if (!useCpuQueryOcclusion && useCpuSocOcclusion",
            StringComparison.Ordinal);
        visibleDemotionPath.ShouldContain("TryScheduleVisibleDrawQuery(");
        visibleDemotionPath.ShouldNotContain("CpuOcclusionProxyRenderer.Draw(visibleProbeBounds, camera!);");
        visibleDemotionPath.ShouldContain("CpuQueryProxyIsNearPlaneUnsafe(camera!, visibleProbeBounds)");
        string scheduledVisibleQuery = Slice(
            visibleDemotionPath,
            "bool queryScheduled =",
            "if (ShouldLogSponzaCpuDiag(cmd))",
            StringComparison.Ordinal);
        int beginIndex = scheduledVisibleQuery.IndexOf(
            "s_cpuOcclusionCoordinator.BeginQuery(",
            StringComparison.Ordinal);
        int drawIndex = scheduledVisibleQuery.IndexOf("RenderWithGpuScope(cmd, renderPass);", StringComparison.Ordinal);
        int endIndex = scheduledVisibleQuery.IndexOf(
            "s_cpuOcclusionCoordinator.EndQuery(",
            StringComparison.Ordinal);
        beginIndex.ShouldBeGreaterThanOrEqualTo(0);
        drawIndex.ShouldBeGreaterThan(beginIndex);
        endIndex.ShouldBeGreaterThan(drawIndex);

        string recoveryProbePath = Slice(
            cpuDirect,
            "// Phase 3: deferred AABB probes for occluded recovery.",
            "private static CpuOcclusionProbeCandidate CreateCpuOcclusionProbeCandidate(",
            StringComparison.Ordinal);
        recoveryProbePath.ShouldContain("CpuOcclusionProxyRenderer.Draw(probe.WorldBounds, camera!);");

        string proxyRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Occlusion/CpuOcclusionProxyRenderer.cs");
        proxyRenderer.ShouldContain("public static void Draw(in AABB worldBounds, XRCamera camera)");
        proxyRenderer.ShouldContain("RuntimeEngine.Rendering.State.RenderingCameraOverride = camera;");
        proxyRenderer.ShouldContain("RenderingCameraOverride is a thread-local stack");
        proxyRenderer.ShouldContain("RuntimeEngine.Rendering.State.RenderingCameraOverride = null;");
        proxyRenderer.ShouldContain("rp.DepthTest.Function = EComparison.Lequal;");
        frameOps.ShouldContain("RuntimeEngine.Rendering.State.MapDepthComparison(dt.Function)");

        string depthPrepass = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardDepthNormalPrePass.cs");
        depthPrepass.ShouldContain("commands.RenderCPUMeshOnly(pass);");

        gpuOcclusion.ShouldContain("return VulkanFeatureProfile.ResolveOcclusionCullingMode(RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode);");
        gpuOcclusion.ShouldNotContain("_loggedCpuQueryModeSuppressedByProfile");
        gpuOcclusion.ShouldNotContain("CpuQueryAsync && VulkanFeatureProfile.ActiveProfile != EVulkanGpuDrivenProfile.Diagnostics");
        gpuOcclusion.ShouldNotContain("Occlusion mode {0} suppressed");

        queryFrameOp.ShouldContain("internal sealed record QueryOp(");
        frameOps.ShouldContain("internal bool EnqueueOcclusionQueryBegin(XRRenderQuery query)");
        frameOps.ShouldContain("internal bool EnqueueOcclusionQueryEnd(XRRenderQuery query)");
        frameOps.ShouldContain("EnqueueFrameOp(new QueryOp(");

        recorder.ShouldContain("bool hasQueryFrameOps = HasQueryFrameOps(ops)");
        recorder.ShouldContain("PrepareQueryFrameOpsForCommandBufferReuse");
        recorder.ShouldNotContain("(!VulkanPrimaryCommandBufferReuseEnabled || hasQueryFrameOps)");
        recorder.ShouldContain("private static bool HasQueryFrameOps(FrameOp[] ops)");
        recorder.ShouldContain("case QueryOp queryOp:");
        recorder.ShouldContain("queryOp.Query.BeginQuery(commandBuffer) == ERenderQueryReadStatus.Ready");
        recorder.ShouldContain("queryOp.Query.EndQuery(commandBuffer);");
        recorder.ShouldContain("recordingScratch.PreparedInlineQueries.Clear();");
        recorder.ShouldContain("for (int prepareIndex = 0; prepareIndex < ops.Length; prepareIndex++)");
        recorder.ShouldContain("recordingScratch.PreparedInlineQueries.Add(pendingQuery.Query)");
        recorder.ShouldContain("pendingQuery.Query.PrepareForRecording(");
        recorder.ShouldContain("recordingScratch.PreparedInlineQueries.Contains(queryOp.Query)");
        recorder.ShouldContain("pendingQuery.Query.PrepareForRecording(commandBuffer, queryCount)");
        string fastPrimaryReuse = Slice(
            recorder,
            "private bool TryReuseCleanCommandChainPrimaryVariant(",
            "private bool TryRefreshReusableCommandBufferFrameData(",
            StringComparison.Ordinal);
        fastPrimaryReuse.ShouldContain("variant.RecordedGenerations.Query != currentGenerations.Query");
        fastPrimaryReuse.ShouldNotContain("variant.FrameOpsSignature != frameOpsSignature");
        fastPrimaryReuse.ShouldContain("PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, ops)");
        string scheduledMeshSecondary = Slice(
            recorder,
            "bool TryExecuteScheduledMeshCommandChainSecondaryRun(",
            "bool TryExecuteMeshCommandChainSecondaryRun(",
            StringComparison.Ordinal);
        scheduledMeshSecondary.ShouldNotContain("CommandChainsEnabledForCurrentRecording");
        scheduledMeshSecondary.ShouldContain("scheduledCommandChainKeysByOpIndex is null");
        scheduledMeshSecondary.ShouldContain("activeInlineQuery is not null");
        scheduledMeshSecondary.IndexOf("for (int i = 0; i < runCount; i++)", StringComparison.Ordinal)
            .ShouldBeLessThan(scheduledMeshSecondary.IndexOf("EndActiveRenderPass();", StringComparison.Ordinal));
        string genericMeshSecondary = Slice(
            recorder,
            "bool TryExecuteMeshCommandChainSecondaryRun(",
            "void ExecuteDynamicUiBatchTextOverlay()",
            StringComparison.Ordinal);
        genericMeshSecondary.ShouldContain("activeInlineQuery is not null");

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
        openXr.ShouldContain("RebaseFrameOpResourcesToActiveResourcePlan(ops);");
        openXr.ShouldContain("RebaseFrameOpTargetsToActiveResourcePlan");
        openXr.ShouldContain("snapshot.SamplersByName[pair.Key] = currentTexture");
        openXr.ShouldContain("? capturedTexture.Name");

        query.ShouldContain("Api!.CmdResetQueryPool(");
        query.ShouldContain("PrepareForCommandBufferReuse(CommandBuffer commandBuffer)");
        query.ShouldNotContain("Api!.ResetQueryPool(Device");
        query.ShouldNotContain("ResetQueryPoolBeforeRecording");
        query.ShouldNotContain("ResetQueryPoolForCommandBufferReuse");
        query.ShouldContain("VulkanQueryPoolAllocation _allocation");
        query.ShouldContain("RenderQueryTicket ticket = new(");
        query.ShouldContain("internal static uint ResolveOcclusionQueryViewSlotCount(uint viewMask)");
        query.ShouldContain("_recordedEpochs");
        query.ShouldContain("_submittedEpoch");
        query.ShouldContain("Renderer.RegisterVulkanRenderQuery(_allocation.Pool, this);");
        query.ShouldContain("Renderer.IsVulkanSubmissionCompleted(epoch.Submission)");
        query.ShouldNotContain("Renderer.DeviceWaitIdle();");
        query.ShouldContain("QueryResultFlags.ResultWithAvailabilityBit");
        query.ShouldNotContain("QueryResultFlags.ResultPartialBit");
        query.ShouldContain("InvalidateRecordedResultEpoch(CommandBuffer commandBuffer)");
        query.ShouldContain("MarkResultEpochSubmitted(");
        query.ShouldContain("Rejected overlapping submission ownership");
        query.ShouldContain("_recordedEpochs.Count != 0 && !_recordedEpochs.ContainsKey(commandBufferHandle)");
        query.ShouldContain("!_submittedEpoch.IsValid && _recordedEpochs.Count == 0");
        query.ShouldNotContain("Renderer.NotifyVulkanResourceUseCompleted(ObjectType.QueryPool, _queryPool.Handle)");
        queryManager.ShouldContain("vkQuery.TryGetAnySamplesPassed(out result, expectedTicket)");
        resourceLifetime.ShouldContain("key.Type == ObjectType.QueryPool");
        resourceLifetime.ShouldContain("queries[queryIndex].MarkResultEpochSubmitted(commandBufferHandle, in submission);");
        resourceLifetime.ShouldContain("private bool IsVulkanResourceUseCompleted(ObjectType type, ulong handle)");
        resourcePlanner.ShouldContain("AreFrameOpContextsRecordingCompatible(");
        resourcePlanner.ShouldContain("AreFrameOpContextsQueryScopeCompatible(");
        resourcePlanner.ShouldNotContain("hash.Add(context.ContextId)");
        recorder.ShouldContain("AreFrameOpContextsRecordingCompatible(activeContext, op.Context)");
        recorder.ShouldContain("AreFrameOpContextsRecordingCompatible(candidate.Context, activeContext)");
        recorder.ShouldContain("bool preservedInlineQueryPass = activeInlineQuery is not null");
        recorder.ShouldContain("bool preservedRenderPass = preservedSwapchainPass || preservedInlineQueryPass;");
        recorder.ShouldContain("activeInlineQuery.InvalidateRecordedResultEpoch(commandBuffer);");
        recorder.ShouldContain("queryFrameOpsRequireRerecordLocal = true;");
        recorder.ShouldContain("MarkCommandBufferVariantTransient(variant, \"query draw was not recorded\")");

        recorder.ShouldContain("hash.Add(ComputeFrameOpStructuralSignature(");
        recorder.ShouldContain("int queryBracketDepth = 0;");
        recorder.ShouldContain("if (queryBracketDepth > 0)");

        renderGraph.ShouldContain("op is MeshDrawOp or QueryOp or BlitOp");
        renderGraph.ShouldContain("QueryOp q => q.Target is null");
        renderGraph.ShouldContain("int queryOrderBlock = 0;");
        renderGraph.ShouldContain("if (op is QueryOp)");
        renderGraph.ShouldContain("queryOrderBlock++;");
        renderGraph.ShouldNotContain("ResolveQueryOrderBlock(");
        renderGraph.ShouldContain("queryBlockCompare");
        renderGraph.ShouldNotContain("if (previous.Operation is QueryOp)");
        coordinator.ShouldContain("state.PendingQueriesAtBudgetRefresh +");
        coordinator.ShouldContain("maxQueries - pendingQueries - state.VisibleBudgetUsed - state.RecoveryBudgetUsed");
        coordinator.ShouldContain("ReplaceOverduePendingQuery(state, queryState);");
        coordinator.ShouldContain("ExpireOverdueHierarchyQuery(state, group, frameId)");
        queryManager.ShouldContain("public void Release(XRRenderQuery query, bool pendingResult = false)");
        queryManager.ShouldContain("query.Destroy();");

        commandContainer.ShouldContain("if (instance is null || AbstractRenderer.Current?.IsDeviceLost == true)");
        commandContainer.ShouldContain("if (AbstractRenderer.Current?.IsDeviceLost == true)\n                    break;");
        commandContainer.ShouldContain("// Device loss is already diagnosed by the backend.");
    }

    [TestCase(new ulong[] { 0, 1 }, 0ul)]
    [TestCase(new ulong[] { 7, 1 }, 1ul)]
    [TestCase(new ulong[] { 0, 1, 3, 1 }, 1ul)]
    [TestCase(new ulong[] { 0, 1, 0, 1 }, 0ul)]
    public void VulkanOcclusionResultDecoder_RequiresAllSlotsAndOrsVisibility(
        ulong[] resultAndAvailability,
        ulong expected)
    {
        VulkanRenderer.VkRenderQuery.TryDecodeOcclusionResult(
            resultAndAvailability,
            out ulong result).ShouldBeTrue();
        result.ShouldBe(expected);
    }

    [TestCase(new ulong[] { 1, 0 })]
    [TestCase(new ulong[] { 1, 1, 0, 0 })]
    [TestCase(new ulong[] { 1 })]
    [TestCase(new ulong[] { })]
    public void VulkanOcclusionResultDecoder_KeepsIncompleteOrMalformedEpochPending(
        ulong[] resultAndAvailability)
    {
        VulkanRenderer.VkRenderQuery.TryDecodeOcclusionResult(
            resultAndAvailability,
            out ulong result).ShouldBeFalse();
        result.ShouldBe(0ul);
    }

    [Test]
    public void MotionVectorsCpuReplay_ReusesPrimaryCpuQueryVisibility()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_RenderMotionVectorsPass.cs");
        string collection = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");

        source.ShouldContain("commands.RenderCPUFiltered(");
        source.ShouldContain("respectCpuQueryOcclusion: true");
        source.ShouldNotContain("suppressCpuOcclusionForPass: true");

        string filtered = Slice(
            collection,
            "public void RenderCPUFiltered(int renderPass, Predicate<RenderCommand> filter, bool respectCpuQueryOcclusion)",
            "private static void RenderWithGpuScope",
            StringComparison.Ordinal);
        filtered.ShouldContain("cmd is IRenderCommandMesh queryMesh &&");
        filtered.ShouldContain("!CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(queryMesh)");
    }

    [Test]
    public void AsyncQueryPool_DoesNotReassignPendingEpochToAnotherOwner()
    {
        using IDisposable _ = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        AsyncOcclusionQueryManager manager = new();

        XRRenderQuery pending = manager.AcquireBooleanOcclusion();
        manager.Release(pending, pendingResult: true);

        XRRenderQuery replacement = manager.AcquireBooleanOcclusion();
        replacement.ShouldNotBeSameAs(pending);

        manager.Release(replacement);
        XRRenderQuery pooled = manager.AcquireBooleanOcclusion();
        pooled.ShouldBeSameAs(replacement);
        pooled.Destroy();
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
