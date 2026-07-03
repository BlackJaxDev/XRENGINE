using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

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
            "private ShadowAtlasRenderPlan SelectRenderPlanForExecution()",
            "private static int GetPlanEntryTileCost");
        selectPlan.ShouldContain("ShadowAtlasRenderPlan plan = PublishedRenderPlan;");
        selectPlan.ShouldContain("LogRenderPlanExecutionSource(plan);");
        selectPlan.ShouldContain("return plan;");
        selectPlan.ShouldNotContain("Environment.CurrentManagedThreadId == _planningThreadId");

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
            "if (TryGetFirstPointFaceGroup(request, out ShadowAtlasGroupedPointFaceAllocation pointGroup)");
        buildPlanGroup.ShouldContain("TryGetDirectionalCascadeGroupRenderRequirement");
        buildPlanGroup.ShouldNotContain("CanRenderDirectionalCascadeGroup");

        source.ShouldContain("TryRenderDirectionalCascadeGroupSequentially(plan, light, entry, collectVisibleNow)");
        source.ShouldContain("EnqueuePlanMemberCompletions(plan, entry)");
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
        string pipelineSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs")
            .Replace("\r\n", "\n");
        string forwardGpu = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs")
            .Replace("\r\n", "\n");

        lightStructs.ShouldContain("mat4 RenderedCascadeMatrices[XRENGINE_MAX_CASCADES];");
        lightStructs.ShouldContain("float RenderedCascadeStaleAge[XRENGINE_MAX_CASCADES];");
        forwardShader.ShouldContain("uniform float DirectionalShadowAtlasMaxStaleFrames");
        forwardShader.ShouldContain("const int XRENGINE_SHADOW_FALLBACK_STALE_TILE = 3;");
        forwardShader.ShouldContain("bool staleTileFallback = atlasState.z == XRENGINE_SHADOW_FALLBACK_STALE_TILE;");
        forwardShader.ShouldContain("(!staleTileFallback || renderedAge <= DirectionalShadowAtlasMaxStaleFrames)");
        forwardShader.ShouldContain("fallbackMode > 0 && fallbackMode != XRENGINE_SHADOW_FALLBACK_LEGACY");
        forwardShader.ShouldContain("light.RenderedCascadeMatrices[cascadeIndex]");
        forwardShader.ShouldContain("XRENGINE_ApplyDirectionalStaleAtlasEdgeFade");

        deferredShader.ShouldContain("uniform float DirectionalShadowAtlasMaxStaleFrames");
        deferredShader.ShouldContain("const int XRENGINE_SHADOW_FALLBACK_STALE_TILE = 3;");
        deferredShader.ShouldContain("bool staleTileFallback = atlasState.z == XRENGINE_SHADOW_FALLBACK_STALE_TILE;");
        deferredShader.ShouldContain("(!staleTileFallback || renderedAge <= DirectionalShadowAtlasMaxStaleFrames)");
        deferredShader.ShouldContain("fallbackMode > 0 && fallbackMode != XRENGINE_SHADOW_FALLBACK_LEGACY");
        deferredShader.ShouldContain("LightData.RenderedCascadeMatrices[cascadeIndex]");
        deferredShader.ShouldContain("ApplyDirectionalStaleAtlasEdgeFade");
        deferredShader.ShouldContain("DeferredDebugMode == 17");
        deferredShader.ShouldContain("DeferredDebugMode == 18");

        pipelineSource.ShouldContain("DirectionalShadowStaleAge = 17");
        pipelineSource.ShouldContain("DirectionalShadowStaleUvValidity = 18");

        lightComponent.ShouldContain("CopyPublishedRenderedCascadeUniformData");
        lightComponent.ShouldContain("program.Uniform(\"DirectionalShadowAtlasMaxStaleFrames\"");
        forwardGpu.ShouldContain("fixed float RenderedCascadeMatrices[ForwardMaxCascades * 16];");
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
        ReadRepoFile("XRENGINE/Engine/Engine.ProfileCapture.cs")
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

        atlasManager.ShouldContain("PrepareDirectionalCascadeGroupSequentialCommands(light);");
        atlasManager.ShouldContain("collectVisibleNow: false");
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
        lightsSource.ShouldContain("IsLargeDirectionalCascadeMatrixJump(requestSample, renderedSample, desiredResolution)");
        lightsSource.ShouldContain("ResolveDirectionalCascadeSettledRefreshStableFrames");
        lightsSource.ShouldContain("private static int ResolveDirectionalCascadeSettledRefreshStableFrames(int activeCascadeCount)\n            => 1;");
        lightsSource.ShouldContain("ResolveDirectionalCascadeRefreshInterval");
        lightsSource.ShouldContain("stableRequestFrames >= ResolveDirectionalCascadeSettledRefreshStableFrames(activeCascadeCount)");
        lightsSource.ShouldContain("if (staleAge >= (ulong)maxStaleFrames)");
        lightsSource.ShouldContain("forcedFresh = true;\n                return false;");
        lightsSource.ShouldNotContain("if (staleAge >= (ulong)maxStaleFrames)\n                return true;");
        atlasManager.ShouldContain("bool keepDirectionalStaleTileUntilRefresh =\n            fallbackCanUseStaleTile &&\n            IsDirectionalRequest(request) &&\n            !shouldRefreshBeforeStale;");
        atlasManager.ShouldNotContain("bool keepDirectionalStaleTileUntilRefresh = fallbackCanUseStaleTile && IsDirectionalRequest(request);");
        lightsSource.ShouldContain("SkipReason.StaleTileReused");
    }

    private static string ReadRepoFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XREngine.slnx")))
                return File.ReadAllText(Path.Combine(directory, relativePath));

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
