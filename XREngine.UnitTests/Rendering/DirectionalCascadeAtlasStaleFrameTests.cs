using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class DirectionalCascadeAtlasStaleFrameTests
{
    [Test]
    public void ShadowAtlasRenderThread_UsesPublishedPlanAndPlanStampedCompletions()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs")
            .Replace("\r\n", "\n");

        string selectPlan = ExtractRegion(
            source,
            "private ShadowAtlasRenderPlan SelectRenderPlanForExecution(int renderPlanIndex)",
            "private static int GetPlanEntryTileCost");
        selectPlan.ShouldContain("ShadowAtlasRenderPlan plan = _renderPlans[renderPlanIndex];");
        selectPlan.ShouldContain("LogRenderPlanExecutionSource(plan);");
        selectPlan.ShouldContain("return plan;");
        selectPlan.ShouldNotContain("Environment.CurrentManagedThreadId == _planningThreadId");

        source.ShouldContain("private readonly object[] _renderPlanLocks = [new(), new()];");
        source.ShouldNotContain("_renderPlanSync");
        string buildPlan = ExtractRegion(
            source,
            "private void BuildRenderPlan()",
            "private ulong AllocateRenderPlanId()");
        buildPlan.ShouldContain("int pendingRenderPlanIndex = GetPendingRenderPlanIndex();");
        buildPlan.ShouldContain("lock (_renderPlanLocks[pendingRenderPlanIndex])");

        string renderPlan = ExtractRegion(
            source,
            "public int RenderScheduledTiles(bool collectVisibleNow = false)",
            "private void LogDirectionalCascadeGroupBudgetDeferral(");
        renderPlan.ShouldContain("int renderPlanIndex = Volatile.Read(ref _publishedRenderPlanIndex);");
        renderPlan.ShouldContain("lock (_renderPlanLocks[renderPlanIndex])");
        renderPlan.ShouldContain("SelectRenderPlanForExecution(renderPlanIndex)");

        ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasRenderPlan.cs")
            .ShouldContain("public ulong PlanId");
        source.ShouldContain("MarkTileRendered(plan, request, allocation, entry.RecordIndex)");
        source.ShouldContain("MarkTileRendered(plan.FrameId, plan.PlanId, request, allocation, recordIndex)");
        source.ShouldContain("CommitRenderedTileToLightSlot(completion)");
        source.ShouldNotContain("EnqueueTileCompletion(request.Key, request.ContentHash, _frameId)");
    }

    [Test]
    public void DirectionalCascadePlan_IsAtomicAndDoesNotGateVulkanSequentialFallback()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs")
            .Replace("\r\n", "\n");

        string buildPlanGroup = ExtractRegion(
            source,
            "if (TryGetDirectionalCascadeGroupContainingRequest(request, out ShadowAtlasGroupedDirectionalCascadeAllocation directionalGroup)",
            "if (TryGetPointFaceGroupContainingRequest(request, out ShadowAtlasGroupedPointFaceAllocation pointGroup)");
        buildPlanGroup.ShouldContain("TryGetDirectionalCascadeGroupRenderRequirement");
        buildPlanGroup.ShouldNotContain("CanRenderDirectionalCascadeGroup");

        string renderGroup = ExtractRegion(
            source,
            "private bool TryRenderDirectionalCascadeGroup(\n        ShadowAtlasRenderPlan plan,\n        int planIndex,",
            "private bool TryRenderDirectionalCascadeGroupSequentially(");
        // A group that the light cannot render as one layered pass falls back
        // to sequential tiles. Grouped capability only selects the command
        // preparation and does not gate the fallback.
        renderGroup.ShouldContain(
            "usedSequentialFallback = TryRenderDirectionalCascadeGroupSequentially(\n                plan,\n                light,\n                entry,\n                prepareSequentialCommands: canRenderGrouped,\n                collectVisibleNow: collectVisibleNow,");
        renderGroup.ShouldContain("EnqueueDirectionalCascadePlanMemberCompletions(plan, entry, light);");
        source.ShouldNotContain("TryRenderDirectionalCascadeGroup(\n        ShadowMapRequest seedRequest");
        source.ShouldNotContain("TryRenderPointFaceGroup(\n        ShadowMapRequest seedRequest");
    }

    [Test]
    public void DirectionalCascadeSlots_UseRenderedSampleProvenanceForStaleAtlasTiles()
    {
        string directionalSource = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs")
            .Replace("\r\n", "\n");
        string lightsSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs")
            .Replace("\r\n", "\n");

        directionalSource.ShouldContain("DirectionalCascadeSampleState[] RenderedSamples");
        directionalSource.ShouldContain("TryCreateDirectionalCascadeSampleState");
        directionalSource.ShouldContain("CommitRenderedCascadeAtlasSlot");
        directionalSource.ShouldContain("DoesRenderedSampleMatchAllocation");
        directionalSource.ShouldContain("CanUseRenderedSampleForStaleAllocation");
        directionalSource.ShouldContain("sample.RenderedFrame >= allocation.LastRenderedFrame");
        directionalSource.ShouldContain("or ShadowFallbackMode.ContactOnly");
        directionalSource.ShouldContain("ResolvePreservedCascadeFallback");
        directionalSource.ShouldContain("ResolveStaleCascadeFallback");
        directionalSource.ShouldContain("RenderedSampleStale");
        directionalSource.ShouldContain("staleSample: true");
        directionalSource.ShouldContain("CreateRenderedSampleAllocation");
        directionalSource.ShouldContain("CreateUnsampledAtlasSlot");
        directionalSource.ShouldContain("ShouldPreserveCascadeAtlasUniformData");
        directionalSource.ShouldContain("allocation.ActiveFallback is ShadowFallbackMode.None");
        directionalSource.ShouldContain("or ShadowFallbackMode.ContactOnly");
        directionalSource.ShouldContain("previous.PageIndex != allocation.PageIndex");
        directionalSource.ShouldContain("RefreshStaleAtlasSlotAllocation");
        directionalSource.ShouldContain("ContentVersion = previous.ContentVersion");
        directionalSource.ShouldContain("LastRenderedFrame = previous.LastRenderedFrame");
        directionalSource.ShouldContain("bool enabled = slot.HasCascadeUniformData");

        lightsSource.ShouldContain("DirectionalCascadeSample: directionalCascadeSample");
        lightsSource.ShouldContain("request.DirectionalCascadeSample");
        lightsSource.ShouldContain("!slot.HasCascadeUniformData");
    }

    [Test]
    public void DirectionalCascadeSlots_PublishAndCommitWholeGenerationsAtomically()
    {
        string directionalSource = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs")
            .Replace("\r\n", "\n");
        string lightsSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs")
            .Replace("\r\n", "\n");
        string atlasManager = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs")
            .Replace("\r\n", "\n");

        directionalSource.ShouldContain("DirectionalCascadeAtlasSlot[] PendingAtlasSlots");
        directionalSource.ShouldContain("CompleteDirectionalAtlasSlotPublish(bool publish, ShadowAtlasManager shadowAtlas)");
        directionalSource.ShouldContain("state.AtlasSlots = state.PendingAtlasSlots;");
        directionalSource.ShouldContain("GetAtlasSlotWriteTarget(state)");
        directionalSource.ShouldContain("CommitRenderedCascadeAtlasSlots(ReadOnlySpan<DirectionalCascadeAtlasRenderCommit> commits)");
        directionalSource.ShouldContain("state.AtlasRequestContentHashes[index] == sample.ContentHash");
        directionalSource.ShouldContain("state.PendingAtlasSlots[index] = renderedSlot;");

        string publishDiagnostics = ExtractRegion(
            lightsSource,
            "private void PublishShadowAtlasDiagnostics()",
            "private static void AccumulateShadowAtlasDiagnostic(");
        publishDiagnostics.ShouldContain("bool publishDirectionalSlots = false;");
        publishDiagnostics.ShouldContain("finally");
        publishDiagnostics.ShouldContain("CompleteDirectionalAtlasSlotPublish(publishDirectionalSlots, ShadowAtlas)");

        atlasManager.ShouldContain("EnqueueDirectionalCascadePlanMemberCompletions(plan, entry, light);");
        atlasManager.ShouldContain("light.CommitRenderedCascadeAtlasSlots(commits[..commitCount]);");
        atlasManager.IndexOf("light.CommitRenderedCascadeAtlasSlots(commits[..commitCount]);", StringComparison.Ordinal)
            .ShouldBeLessThan(atlasManager.IndexOf("EnqueueTileCompletion(completion);", atlasManager.IndexOf("private void EnqueueDirectionalCascadePlanMemberCompletions", StringComparison.Ordinal), StringComparison.Ordinal));
    }

    [Test]
    public void LegacyDirectionalCascades_RequireRenderedContentAndReadyDescriptors()
    {
        string directionalSource = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs")
            .Replace("\r\n", "\n");
        string deferredSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs")
            .Replace("\r\n", "\n");
        string forwardSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs")
            .Replace("\r\n", "\n");
        string dirtyReasonSource = ReadRepoFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Reuse/Invalidation/VulkanRenderer.CommandBufferDirtyReasons.cs")
            .Replace("\r\n", "\n");
        string loweringSource = SourceContractWorkspace.ReadVulkanSourcesContaining("private void InvalidateCommandChainScheduleForResourceChange(string reason)")
            .Replace("\r\n", "\n");

        directionalSource.ShouldContain("GetSampleableCascadedShadowReceiverTexture");
        directionalSource.ShouldContain("state.LegacyRenderedContentRevision != state.ContentRevision");
        directionalSource.ShouldContain("state.LegacyRenderedCascadeCount < activeCascadeCount");
        directionalSource.ShouldContain("ReferenceEquals(state.LegacyRenderedReceiverTexture, receiverTexture)");
        directionalSource.ShouldContain("renderer.IsTextureReadyForShaderSampling(receiverTexture)");
        directionalSource.ShouldContain("MarkLegacyCascadeRenderComplete(source, cascadeCount);");
        directionalSource.ShouldContain("EnsureCascadeShadowResources();");

        deferredSource.ShouldContain("GetSampleableCascadedShadowReceiverTexture(activeRenderingCamera)");
        deferredSource.ShouldContain("IsTextureReadyForShadowSampling(atlasTexture)");
        deferredSource.ShouldContain("if (!IsTextureReadyForShadowSampling(selectedShadowMap))");
        forwardSource.ShouldContain("GetSampleableCascadedShadowReceiverTexture(directionalShadowCamera)");
        forwardSource.ShouldContain("IsTextureReadyForShadowSampling(directionalAtlas)");

        string commandRuntime = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.cs")
            .Replace("\r\n", "\n");

        dirtyReasonSource.ShouldContain("InvalidateCommandChainScheduleForResourceChange");
        loweringSource.ShouldContain("private void InvalidateCommandChainScheduleForResourceChange(string reason)");
        // The command runtime owns the one schedule cache. A resource change
        // clears it so that no primary reuses a stale schedule.
        loweringSource.ShouldContain("_commandRuntime.InvalidateScheduleCache();");
        commandRuntime.ShouldContain(
            "public void InvalidateScheduleCache()\n    {\n        if (_scheduleCache is not null)\n            Array.Clear(_scheduleCache);\n    }");
    }

    [Test]
    public void DeferredLightCombine_PreservesViewportContextAcrossCommandChainRebuilds()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs")
            .Replace("\r\n", "\n");

        source.ShouldContain("XRViewport? viewport = ActivePipelineInstance.RenderState.WindowViewport\n                ?? ActivePipelineInstance.LastWindowViewport;");
        source.ShouldContain("if (ActivePipelineInstance.RenderState.Scene is null && world is null)");
        source.Split(
            "XRViewport? viewport = ActivePipelineInstance.RenderState.WindowViewport\n                ?? ActivePipelineInstance.LastWindowViewport;",
            StringSplitOptions.None).Length.ShouldBeGreaterThanOrEqualTo(3);
        source.ShouldContain("return (ActivePipelineInstance.RenderState.WindowViewport\n                ?? ActivePipelineInstance.LastWindowViewport)?.World?.Lights;");
        source.ShouldNotContain("var world = ActivePipelineInstance.RenderState.WindowViewport?.World;");
    }

    [Test]
    public void DirectionalCascadeShaders_ReprojectStaleAtlasSamplesWithRenderedUniforms()
    {
        string forwardShader = ReadRepoFile("Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl")
            .Replace("\r\n", "\n");
        string deferredShader = ReadRepoFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightingDir.fs")
            .Replace("\r\n", "\n");
        string lightStructs = ReadRepoFile("Build/CommonAssets/Shaders/Snippets/LightStructs.glsl")
            .Replace("\r\n", "\n");
        string lightComponent = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.cs")
            .Replace("\r\n", "\n");
        string pipelineSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs")
            .Replace("\r\n", "\n");
        string forwardGpu = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs")
            + ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardDirectionalLightGpu.cs")
            .Replace("\r\n", "\n");

        string shadowRecord = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/DirectionalShadowGpuRecord.cs")
            .Replace("\r\n", "\n");
        string cascadeShadows = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs")
            .Replace("\r\n", "\n");
        const string renderedMatrixSelection =
            "mat4 lightMatrix = atlasSampleAllowed ? record.RenderedWorldToLight : record.CurrentWorldToLight;";

        // Each cascade publishes one record that holds the current and the
        // rendered matrices. The stale age is in ReceiverOffsetsAge.z.
        lightStructs.ShouldContain("struct DirectionalShadowGpuRecord");
        lightStructs.ShouldContain("mat4 RenderedWorldToLight;");
        lightStructs.ShouldContain("vec4 ReceiverOffsetsAge;");
        shadowRecord.ShouldContain("[FieldOffset(64)] public Matrix4x4 RenderedWorldToLight;");
        shadowRecord.ShouldContain("[FieldOffset(160)] public Vector4 ReceiverOffsetsAge;");
        cascadeShadows.ShouldContain("RenderedWorldToLight = renderedMatrix,");
        cascadeShadows.ShouldContain("ReceiverOffsetsAge = new(currentReceiverOffset, renderedReceiverOffset, staleAge, 0.0f),");
        forwardShader.ShouldContain("uniform float DirectionalShadowAtlasMaxStaleFrames");
        forwardShader.ShouldContain("const int XRENGINE_SHADOW_FALLBACK_STALE_TILE = 3;");
        forwardShader.ShouldContain("bool staleTileFallback = atlasState.z == XRENGINE_SHADOW_FALLBACK_STALE_TILE;");
        forwardShader.ShouldContain("(!staleTileFallback || renderedAge <= DirectionalShadowAtlasMaxStaleFrames)");
        forwardShader.ShouldContain("fallbackMode > 0 && fallbackMode != XRENGINE_SHADOW_FALLBACK_LEGACY");
        forwardShader.ShouldContain("float renderedAge = record.ReceiverOffsetsAge.z;");
        forwardShader.ShouldContain(renderedMatrixSelection);
        forwardShader.ShouldContain("XRENGINE_ApplyDirectionalStaleAtlasEdgeFade");

        deferredShader.ShouldContain("uniform float DirectionalShadowAtlasMaxStaleFrames");
        deferredShader.ShouldContain("const int XRENGINE_SHADOW_FALLBACK_STALE_TILE = 3;");
        deferredShader.ShouldContain("bool staleTileFallback = atlasState.z == XRENGINE_SHADOW_FALLBACK_STALE_TILE;");
        deferredShader.ShouldContain("(!staleTileFallback || renderedAge <= DirectionalShadowAtlasMaxStaleFrames)");
        deferredShader.ShouldContain("fallbackMode > 0 && fallbackMode != XRENGINE_SHADOW_FALLBACK_LEGACY");
        deferredShader.ShouldContain("float renderedAge = DirectionalShadowRecords[cascadeIndex].ReceiverOffsetsAge.z;");
        deferredShader.ShouldContain(renderedMatrixSelection);
        deferredShader.ShouldContain("ApplyDirectionalStaleAtlasEdgeFade");
        deferredShader.ShouldContain("DeferredDebugMode == 17");
        deferredShader.ShouldContain("DeferredDebugMode == 18");

        pipelineSource.ShouldContain("DirectionalShadowStaleAge = 17");
        pipelineSource.ShouldContain("DirectionalShadowStaleUvValidity = 18");

        cascadeShadows.ShouldContain("internal void CopyPublishedDirectionalShadowRecords(");
        lightComponent.ShouldContain("program.Uniform(\"DirectionalShadowAtlasMaxStaleFrames\"");
        forwardGpu.ShouldContain("private readonly DirectionalShadowGpuRecord[] _forwardDirectionalShadowRecords");
        forwardGpu.ShouldContain("directionalLights[i].CopyPublishedDirectionalShadowRecords(");
        forwardGpu.ShouldContain("program.Uniform(\"DirectionalShadowAtlasMaxStaleFrames\"");
    }

    [Test]
    public void DirectionalCascadeAtlas_AuditLogsPlanSourceAndCompletionLatency()
    {
        string atlasManager = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs")
            .Replace("\r\n", "\n");
        string directionalLight = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs")
            .Replace("\r\n", "\n");

        atlasManager.ShouldContain("LogRenderPlanExecutionSource(plan);");
        atlasManager.ShouldContain("[DirectionalShadowAudit][AtlasPlanExecution]");
        atlasManager.ShouldContain("source=Published");
        atlasManager.ShouldContain("LogTileCompletionLatency(completion);");
        atlasManager.ShouldContain("[DirectionalShadowAudit][TileCompletionLatency]");
        atlasManager.ShouldContain("latencyFrames=");

        directionalLight.ShouldContain("LogDirectionalCascadeProvenance(");
        directionalLight.ShouldContain("[DirectionalShadowAudit][CascadeProvenance]");
        directionalLight.ShouldContain("DirectionalCascade.StaleSampled=");
        directionalLight.ShouldContain("DirectionalCascade.MixedGenerationPrevented=");
        directionalLight.ShouldContain("ERendererProfilerCounter.DirectionalCascadeStaleSampled");
        directionalLight.ShouldContain("ERendererProfilerCounter.DirectionalCascadeMixedGenerationPrevented");
        ReadRepoFile("XREngine.Data/Rendering/Enums/ERendererProfilerCounter.cs")
            .ShouldContain("DirectionalCascadePhysicalReprojected");
        ReadRepoFile("XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs")
            .ShouldContain("directional_cascade_forced_fresh_render");
    }

    [Test]
    public void ShadowAtlasDiagnostics_UseReconciledResidentState()
    {
        string atlasManager = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs")
            .Replace("\r\n", "\n");
        string lightsSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs")
            .Replace("\r\n", "\n");

        atlasManager.ShouldContain("public bool TryGetPlanningAllocation(ShadowRequestKey key, out ShadowAtlasAllocation allocation)\n        => TryGetResidentAllocation(key, out allocation);");
        atlasManager.ShouldContain("TryGetDiagnosticAllocationIndex(");
        atlasManager.ShouldContain("MergeReconciledRenderedState(");
        atlasManager.ShouldContain("resident.LastRenderedFrame < allocation.LastRenderedFrame");
        atlasManager.ShouldContain("IsSamePhysicalAllocation(");
        atlasManager.ShouldContain("bool publishCommittedDirectionalSample =");
        atlasManager.ShouldContain("ShouldRenderFreshTileBeforeStale(request)");
        atlasManager.ShouldContain("resident.ContentVersion != request.ContentHash");
        atlasManager.ShouldContain("ActiveFallback = publishCommittedDirectionalSample\n                ? ShadowFallbackMode.None\n                : resident.ActiveFallback");
        atlasManager.ShouldContain("SkipReason = publishCommittedDirectionalSample\n                ? SkipReason.None\n                : resident.SkipReason");
        atlasManager.ShouldContain("ResolveUnavailableShadowFallback(request, allowStaleTile: false)");
        atlasManager.ShouldContain("return IsDirectionalRequest(request) ? ShadowFallbackMode.ContactOnly : ShadowFallbackMode.Lit;");

        string publishDiagnostics = ExtractRegion(
            lightsSource,
            "private void PublishShadowAtlasDiagnostics()",
            "private static void AccumulateShadowAtlasDiagnostic(");
        publishDiagnostics.ShouldContain("ShadowAtlas.TryGetDiagnosticAllocationIndex(request, out int recordIndex, out ShadowAtlasAllocation allocation)");
        publishDiagnostics.ShouldNotContain("frameData.TryGetAllocationIndex");
    }

    [Test]
    public void DirectionalCascadeRefreshPressure_IsBoundedAndDiagnosable()
    {
        string atlasManager = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs")
            .Replace("\r\n", "\n");
        string lightsSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs")
            .Replace("\r\n", "\n");
        string directionalLight = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs")
            .Replace("\r\n", "\n");

        lightsSource.ShouldContain("AddDirectionalCascadeMatrix(ref hash, view, projection, desiredResolution);");
        lightsSource.ShouldContain("AddQuantizedMatrix(ref hash, viewProjection, quantum);");
        lightsSource.ShouldContain("clipTexel / 4096.0f");

        string sequential = ExtractRegion(
            atlasManager,
            "private bool TryRenderDirectionalCascadeGroupSequentially(",
            "private void EnqueuePlanMemberCompletions(");
        // Prepared sequential commands already hold the visible set, so the
        // tiles do not collect it again.
        sequential.ShouldContain("if (!light.PrepareSequentialCascadeShadowAtlasCommands(");
        sequential.ShouldContain("collectVisibleNow = false;");
        sequential.IndexOf("if (!light.PrepareSequentialCascadeShadowAtlasCommands(", StringComparison.Ordinal)
            .ShouldBeLessThan(sequential.IndexOf("collectVisibleNow = false;", StringComparison.Ordinal));
        atlasManager.ShouldContain("LogDirectionalCascadeGroupBudgetDeferral(");
        atlasManager.ShouldContain("keeping the previous coherent atlas generation");
        atlasManager.ShouldContain("ResolveTilesScheduledMetric()");
        atlasManager.ShouldContain("entry.MemberCount == group.CascadeCount");

        directionalLight.ShouldContain("SequentialVulkanCascadeAtlas");
        directionalLight.ShouldContain("_cascadeShadowRenderMode = EDirectionalCascadeShadowRenderMode.Auto");
        directionalLight.ShouldContain("CreateAtlasPageCascadeShadowRenderPlan");
        directionalLight.ShouldContain("SupportsDirectionalCascadeAtlasGroupedRendering");
        directionalLight.ShouldNotContain("DirectionalCascadeShadowFallbackReason.VulkanCascadeAtlasGroupedRenderingDisabled);");
        directionalLight.ShouldContain("HasDirectionalCascadeAtlasRenderRequest");
        directionalLight.ShouldContain("ShouldCollectDirectionalCascadeAtlasViewport");
        directionalLight.ShouldContain("AtlasCascadeVisibleSetCached");
        directionalLight.ShouldContain("GetDirectionalCascadeStableRequestFrameCount");
        directionalLight.ShouldContain("StableAtlasRequestFrameCounts");
        lightsSource.ShouldContain("ShouldSkipDirectionalCascadeRefreshByCadence");
        // The cadence holds the last coherent atlas generation while the source
        // camera moves. It refreshes after a pose-stability debounce. The dirty
        // stale-age limit forces a fresh render and never holds the stale tile.
        directionalLight.ShouldContain("internal int GetDirectionalCascadeSourceCameraStableFrameCount(");
        lightsSource.ShouldContain("ResolveDirectionalCascadeSettledRefreshStableFrames");
        lightsSource.ShouldContain("private static int ResolveDirectionalCascadeSettledRefreshStableFrames(int activeCascadeCount)\n            => Math.Clamp(activeCascadeCount, 2, 8);");
        lightsSource.ShouldContain("int stableSourceCameraFrames =\n                light.GetDirectionalCascadeSourceCameraStableFrameCount(source);");
        lightsSource.ShouldContain("if (stableSourceCameraFrames < ResolveDirectionalCascadeSettledRefreshStableFrames(activeCascadeCount))\n                return true;");
        lightsSource.ShouldContain("if (HasExceededDirtyShadowStaleAge(projectionType, hasPrevious, previous, contentHash))\n                dirtyReason |= ShadowDirtyReason.StaleContentAgeLimitReached;");
        lightsSource.ShouldContain("ulong maximumDeferredDirtyFrames = ResolveMaximumDeferredDirtyShadowFrames(projectionType);");
        lightsSource.ShouldContain("return (ulong)Math.Max(0, RuntimeEngine.Rendering.Settings.MaxDirectionalCascadeAtlasStaleFrames);");
        lightsSource.ShouldContain("if ((dirtyReason & ShadowDirtyReason.StaleContentAgeLimitReached) != 0)\n            {\n                forcedFresh = true;\n                return false;\n            }");
        lightsSource.ShouldContain("forcedFresh = true;\n                return false;");
        atlasManager.ShouldContain("bool keepDirectionalStaleTileUntilRefresh =\n            fallbackCanUseStaleTile &&\n            IsDirectionalRequest(request) &&\n            !shouldRefreshBeforeStale;");
        atlasManager.ShouldNotContain("bool keepDirectionalStaleTileUntilRefresh = fallbackCanUseStaleTile && IsDirectionalRequest(request);");
        lightsSource.ShouldContain("SkipReason.StaleTileReused");
    }

    [TestCase(1024u, 0.03125f, 0.25f)]
    [TestCase(4096u, 0.02f, 0.10f)]
    public void DirectionalCascadeLargeJumpThresholds_AreClipSpaceScaled(
        uint desiredResolution,
        float expectedLinearThreshold,
        float expectedTranslationThreshold)
    {
        Lights3DCollection.ResolveDirectionalCascadeLinearJumpThreshold(desiredResolution)
            .ShouldBe(expectedLinearThreshold, 1e-6f);
        Lights3DCollection.ResolveDirectionalCascadeTranslationJumpThreshold(desiredResolution)
            .ShouldBe(expectedTranslationThreshold, 1e-6f);
    }

    private static string ReadRepoFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XREngine.slnx")))
                return File.ReadAllText(Path.Combine(directory, relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not locate repository root from test directory.");
    }

    private static string ExtractRegion(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);

        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        return source[start..end];
    }
}
