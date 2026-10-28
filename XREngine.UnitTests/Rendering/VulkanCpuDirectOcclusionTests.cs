using System;
using System.IO;
using System.Text.RegularExpressions;
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
        string frameOps = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string queryFrameOp = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/QueryOp.cs");
        string queryCapability = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.cs");
        string recorder = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string commandChains = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "LowerFrameOpsToRenderPacketsExcludingQueryBrackets",
            "bool commandChainsEnabledForTarget = allowExternalSwapchainTarget",
            "Dictionary<ulong, int> structuralOccurrences");
        string openXr = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string query = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Queries/VkRenderQuery.cs");
        string resourceLifetime = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string resourcePlannerContext = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerContext.cs");
        string contextCompatibility = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/FrameOpContextCompatibility.cs");
        string renderGraph = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");
        string commandContainer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/ViewportRenderCommandContainer.cs");
        string runtimeEngine = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");
        string queryManager = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Occlusion/AsyncOcclusionQueryManager.cs");

        coordinator.ShouldContain("using XREngine.Rendering.Vulkan;");
        coordinator.ShouldContain("TryGetBackendCapability<IOcclusionQueryBackendCapability>");
        coordinator.ShouldContain("capability?.BeginOcclusionQuery(query) == true");
        coordinator.ShouldContain("capability?.EndOcclusionQuery(query) == true");
        queryCapability.ShouldContain("BeginOcclusionQuery(XRRenderQuery query) => _frameLoop.TryEnqueueQueryOperation(query, ERenderQueryOperation.Begin)");
        queryCapability.ShouldContain("EndOcclusionQuery(XRRenderQuery query) => _frameLoop.TryEnqueueQueryOperation(query, ERenderQueryOperation.End)");
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
        // Only the WebGPU backend reads a frozen or captured view depth mode.
        // All other backends read the depth mode of the active rendering camera.
        string depthMode = Slice(
            runtimeEngine,
            "public static XRCamera.EDepthMode GetDepthMode()",
            "public static float GetDefaultDepthClearValue()",
            StringComparison.Ordinal);
        depthMode.ShouldContain("XRCamera? camera = RenderingCamera;");
        depthMode.ShouldContain("if (renderer is not null && renderer.BackendId == RendererBackendId.WebGPU)");
        depthMode.ShouldContain("return camera?.DepthMode ?? XRCamera.EDepthMode.Normal;");

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
        int drawIndex = scheduledVisibleQuery.IndexOf("RenderWithGpuScope(cmd, renderPass, measureCpuSocSubmission);", StringComparison.Ordinal);
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
        // The renderer capability enqueues occlusion brackets through the frame loop.
        // The command runtime freezes each bracket as a prepared QueryOp.
        string legacyCommandApi = ReadExactWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.LegacyCommandApi.cs");
        string queryOperations = ReadExactWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.QueryOperations.cs");
        string operationQueue = ReadExactWorkspaceFile(VulkanRoot + "Commands/FrameOps/VulkanFrameOperationQueue.cs");
        legacyCommandApi.ShouldContain("internal bool TryEnqueueQueryOperation(XRRenderQuery query, ERenderQueryOperation operation)");
        legacyCommandApi.ShouldContain("=> query.Descriptor.Kind == ERenderQueryKind.Occlusion && _commandRuntime.TryEnqueueQueryOperation(");
        queryOperations.ShouldContain("=> queue.EnqueuePreparedQuery(query, descriptor, operation, passIndex, target, context);");
        operationQueue.ShouldContain("internal bool EnqueuePreparedQuery(");
        operationQueue.ShouldContain("EnqueuePrepared(new QueryOp(");

        // Command-buffer reuse refreshes the query pools of reused primaries.
        string reuseDependencies = ReadExactWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Reuse/Dependencies/VulkanRenderer.CommandBufferReuse.Dependencies.cs");
        string reuseFrameData = ReadExactWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/FrameData/VulkanRenderer.CommandBufferReuse.FrameData.cs");
        reuseDependencies.ShouldContain("private static bool HasQueryFrameOps(FrameOperationSequence ops)");
        reuseDependencies.ShouldContain("if (ops.GetHeader(i).OpCode == EVulkanPrimaryPlanNodeKind.Query)");
        reuseDependencies.ShouldContain("Query: ComputeQueryGeneration(staticOps, volatileOps),");
        reuseFrameData.ShouldNotContain("(!VulkanPrimaryCommandBufferReuseEnabled || hasQueryFrameOps)");

        // The primary recorder brackets the contributing draws with the prepared inline query.
        string primaryOperations = ReadExactWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs");
        string primarySetup = ReadExactWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Setup.cs");
        primaryOperations.ShouldContain("private int RecordQueryPayload(scoped ref PrimaryCommandBufferRecordingState state, in QueryPayload payload, in VulkanPrimaryOperationRecordingInfo info)");
        primaryOperations.ShouldContain("if (payload.Operation == ERenderQueryOperation.Begin) BeginInlineQueryPayload(ref state, in payload, info.OperationIndex, info.PassIndex);");
        primaryOperations.ShouldContain("state.ActiveInlineQuery = query.BeginQuery(state.CommandBuffer) == ERenderQueryReadStatus.Ready ? query : null;");
        primaryOperations.ShouldContain("query.EndQuery(state.CommandBuffer);");
        primaryOperations.ShouldContain("if (state.RecordingScratch.PreparedInlineQueries.Contains(query) && state.RecordingScratch.BegunInlineQueries.Add(query))");
        primarySetup.ShouldContain("recordingState.RecordingScratch.PreparedInlineQueries.Clear();");
        primarySetup.ShouldContain("for (int prepareIndex = 0; prepareIndex < recordingState.Ops.Length; prepareIndex++)");
        primarySetup.ShouldContain("!recordingState.RecordingScratch.PreparedInlineQueries.Add(pendingQuery.Query))");
        primarySetup.ShouldContain("if (!pendingQuery.Query.PrepareForRecording(recordingState.CommandBuffer, queryCount))");

        // The fast reuse path trusts the schedule cache identity and the primary
        // publication identity. It then prepares the query pools again before reuse.
        string fastPrimaryReuse = Slice(
            reuseFrameData,
            "private bool TryReuseCleanCommandChainPrimaryVariant(",
            "private bool TryRevalidatePrimarySecondaryArtifactsAfterGlobalMutation(",
            StringComparison.Ordinal);
        fastPrimaryReuse.ShouldContain("if (variant.RecordedCommandChainScheduleCacheIdentity !=\n                cachedSchedule.CacheIdentity)");
        fastPrimaryReuse.ShouldContain("\"PrimaryPublicationIdentity\");");
        fastPrimaryReuse.ShouldContain("(HasQueryFrameOps(ops) && !PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, ops))");
        string secondaries = ReadExactWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs");
        string scheduledMeshSecondary = Slice(
            secondaries,
            "bool TryExecuteScheduledMeshCommandChainSecondaryRun(",
            "bool TryExecuteMeshCommandChainSecondaryRun(",
            StringComparison.Ordinal);
        scheduledMeshSecondary.ShouldNotContain("CommandChainsEnabledForCurrentRecording");
        scheduledMeshSecondary.ShouldContain("scheduledCommandChainKeysByOpIndex is null");
        scheduledMeshSecondary.ShouldContain("activeInlineQuery is not null");
        scheduledMeshSecondary.IndexOf("for (int i = 0; i < runCount; i++)", StringComparison.Ordinal)
            .ShouldBeLessThan(scheduledMeshSecondary.IndexOf("EndActiveRenderPass(ref recordingState);", StringComparison.Ordinal));
        // The generic secondary run is the last method of its file.
        int genericMeshSecondaryStart = secondaries.IndexOf("bool TryExecuteMeshCommandChainSecondaryRun(", StringComparison.Ordinal);
        genericMeshSecondaryStart.ShouldBeGreaterThanOrEqualTo(0);
        string genericMeshSecondary = secondaries[genericMeshSecondaryStart..];
        genericMeshSecondary.ShouldContain("activeInlineQuery is not null");

        commandChains.ShouldContain("LowerFrameOpsToRenderPacketsExcludingQueryBrackets");
        commandChains.ShouldContain("ordinal == -1 ? int.MaxValue : ordinal");
        commandChains.ShouldContain("MaxCommandChainsPerSchedule");
        commandChains.ShouldContain("queryBracketDepth == 0");
        commandChains.ShouldContain("Keeping occlusion query brackets inline while scheduling the remaining frame ops as command chains");
        commandChains.ShouldContain("allowExternalSwapchainTarget");
        commandChains.ShouldContain("allowExternalSwapchainTarget\n            ? CommandChainsExplicitlyRequested");
        commandChains.ShouldContain("Dictionary<ulong, int> structuralOccurrences");
        commandChains.ShouldNotContain("return HashCode.Combine(sourceOrdinal, foldedStructuralSignature);");
        // An OpenXR eye with CPU query occlusion records without a command-chain schedule.
        string openXrEyeRendering = ReadExactWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.EyeRendering.cs");
        string openXrMirrorPreview = ReadExactWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.MirrorPreview.cs");
        string openXrPrewarm = ReadExactWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.PrewarmValidation.cs");
        openXrEyeRendering.ShouldContain("CommandChainSchedule? commandChainSchedule =\n            RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode == EOcclusionCullingMode.CpuQueryAsync\n                ? null\n                : TryBuildOpenXrEyeCommandChainSchedule(");
        openXrMirrorPreview.ShouldContain("allowExternalSwapchainTarget: true");
        openXrPrewarm.ShouldContain("RebaseFrameOpResourcesToActiveResourcePlan(ops);");
        openXrPrewarm.ShouldContain("RebaseFrameOpTargetsToActiveResourcePlan");
        openXrPrewarm.ShouldContain("snapshot.SamplersByName[pair.Key] = currentTexture");
        openXrPrewarm.ShouldContain("? capturedTexture.Name");

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
        // The query authority owns pool registration and submission completion.
        query.ShouldContain("VulkanQueryAuthority.RegisterRenderQuery(BackendContext.Resources.Lifetime.Tracker, _allocation.Pool, this);");
        query.ShouldContain("BackendContext.Resources.Queries.IsSubmissionCompleted(epoch.Submission)");
        query.ShouldNotContain("Renderer.DeviceWaitIdle();");
        query.ShouldContain("QueryResultFlags.ResultWithAvailabilityBit");
        query.ShouldNotContain("QueryResultFlags.ResultPartialBit");
        query.ShouldContain("InvalidateRecordedResultEpoch(CommandBuffer commandBuffer)");
        query.ShouldContain("MarkResultEpochSubmitted(");
        query.ShouldContain("Rejected overlapping submission ownership");
        query.ShouldContain("_recordedEpochs.Count != 0 && !_recordedEpochs.ContainsKey(commandBufferHandle)");
        query.ShouldContain("!_submittedEpoch.IsValid && _recordedEpochs.Count == 0");
        query.ShouldNotContain("Renderer.NotifyVulkanResourceUseCompleted(ObjectType.QueryPool, _queryPool.Handle)");
        queryManager.ShouldContain("capability.TryGetAnySamplesPassed(query, out result, expectedTicket)");
        // A tracked submission marks the result epoch of each query in the submitted pools.
        string trackedSubmission = Compact(ReadExactWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs"));
        string queryAuthority = ReadExactWorkspaceFile(VulkanRoot + "Resources/Authority/VulkanQueryAuthority.cs");
        trackedSubmission.ShouldContain("if (key.Type == ObjectType.QueryPool && tracker.RenderQueriesByPool.TryGetValue(");
        trackedSubmission.ShouldContain("queries[queryIndex].MarkResultEpochSubmitted( handle, in submission);");
        queryAuthority.ShouldContain("internal bool IsSubmissionCompleted(in VulkanLifetimeSubmission submission)");
        contextCompatibility.ShouldContain("AreRecordingCompatible(");
        contextCompatibility.ShouldContain("AreQueryScopeCompatible(");
        resourcePlannerContext.ShouldNotContain("hash.Add(context.ContextId)");
        // A context change keeps the render scope open only for a compatible inline query.
        // An interrupted or empty query bracket makes the primary artifact one-shot.
        string renderScopes = ReadExactWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.RenderScopes.cs");
        string renderScopeController = ReadExactWorkspaceFile(VulkanRoot + "Commands/Recording/VulkanRenderScopeController.cs");
        string primaryFinalization = ReadExactWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Finalization.cs");
        string primaryRecording = ReadExactWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs");
        primaryOperations.ShouldContain("if (state.HasActiveContext && FrameOpContextCompatibility.AreRecordingCompatible(state.ActiveContext, context)) return true;");
        primaryOperations.ShouldContain("state.ActiveInlineQuery is not null, context.SchedulingIdentity, state.ActivePassIndex, state.ActiveSchedulingIdentity, FrameOpContextCompatibility.AreQueryScopeCompatible(state.ActiveContext, context));");
        recorder.ShouldContain("FrameOpContextCompatibility.AreCommandChainBatchCompatible(");
        renderScopeController.ShouldContain("return hasInlineQuery &&\n            ReferenceEquals(Target, incomingTarget) &&");
        renderScopes.ShouldContain("recordingState.ActiveInlineQuery.InvalidateRecordedResultEpoch(recordingState.CommandBuffer);");
        primaryOperations.ShouldContain("if (!state.ActiveInlineQueryRecordedDraw) { state.FrameOpsRequireRerecordLocal = true; query.InvalidateRecordedResultEpoch(state.CommandBuffer); }");
        primaryFinalization.ShouldContain("recordingState.FrameOpsRequireRerecord =\n                recordingState.FrameOpsRequireRerecordLocal;");
        primaryRecording.ShouldContain("owner.Dirty = context.FrameOpsRequireRerecord;");

        recorder.ShouldContain("hash.Add(ComputeFrameOpStructuralSignature(");
        recorder.ShouldContain("int queryBracketDepth = 0;");
        // The query generation covers every draw inside a query bracket.
        reuseDependencies.ShouldContain("private static void AddQueryGenerationParts(");
        reuseDependencies.ShouldContain("if (queryBracketDepth > 0)\n                {\n                    hash.Add(i);\n                    hash.Add(ComputeFrameOperationFrameDataSignature(ops, i));");

        // The frame-operation scheduler fences sorting at each query operation.
        string scheduler = ReadExactWorkspaceFile(VulkanRoot + "RenderGraph/VulkanFrameOperationScheduler.cs");
        scheduler.ShouldContain("=> sortKey.OpCode is EVulkanPrimaryPlanNodeKind.MeshDraw or\n            EVulkanPrimaryPlanNodeKind.Query or\n            EVulkanPrimaryPlanNodeKind.Blit or");
        scheduler.ShouldContain("=> target is null && header.OpCode is\n            EVulkanPrimaryPlanNodeKind.Clear or\n            EVulkanPrimaryPlanNodeKind.MeshDraw or\n            EVulkanPrimaryPlanNodeKind.Query or");
        scheduler.ShouldContain("int queryOrderBlock = 0;");
        scheduler.ShouldContain("if (header.OpCode == EVulkanPrimaryPlanNodeKind.Query)\n                    queryOrderBlock++;");
        scheduler.ShouldNotContain("ResolveQueryOrderBlock(");
        scheduler.ShouldContain("int queryBlockCompare = x.QueryOrderBlock.CompareTo(y.QueryOrderBlock);");
        scheduler.ShouldNotContain("if (previous.Operation is QueryOp)");
        renderGraph.ShouldNotContain("ResolveQueryOrderBlock(");
        renderGraph.ShouldNotContain("if (previous.Operation is QueryOp)");
        coordinator.ShouldContain("state.PendingQueriesAtBudgetRefresh +");
        coordinator.ShouldContain("maxQueries - pendingQueries - state.VisibleBudgetUsed - state.RecoveryBudgetUsed");
        coordinator.ShouldContain("ReplaceOverduePendingQuery(state, queryState);");
        coordinator.ShouldContain("ExpireOverdueHierarchyQuery(state, group, frameId)");
        queryManager.ShouldContain("public void Release(XRRenderQuery query, bool pendingResult = false)");
        queryManager.ShouldContain("query.Destroy();");

        // A lost device stops the command container before and during execution.
        commandContainer.ShouldContain("if (instance is null)\n                return;\n            if (AbstractRenderer.Current?.IsDeviceLost == true)");
        commandContainer.ShouldContain("if (AbstractRenderer.Current?.IsDeviceLost == true)\n                        break;");
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
        VkRenderQuery.TryDecodeOcclusionResult(
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
        VkRenderQuery.TryDecodeOcclusionResult(
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

    private const string VulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    private static string ReadExactWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadExactFile(relativePath).Replace("\r\n", "\n");

    private static string Compact(string source)
        => Regex.Replace(source, @"\s+", " ");

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
