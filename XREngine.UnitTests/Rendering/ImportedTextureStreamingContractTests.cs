using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ImportedTextureStreamingContractTests
{
    [Test]
    public void ImportedTextureStreaming_RejectsStaleResidentDataBeforeApplyingToLiveTexture()
    {
        string source = ReadTextureStreamingSources();

        source.ShouldContain("Func<bool>? shouldAcceptResult = null");
        source.ShouldContain("bool IsCurrentTransition()");
        source.ShouldContain("ReferenceEquals(record.PendingLoadCts, cts) && !cts.IsCancellationRequested");
        source.ShouldContain("shouldAcceptResult: IsCurrentTransition");
        source.ShouldContain("RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadTask(");
        source.ShouldContain("XRTexture2D.ApplyResidentData(target, residentData, includeMipChain);");
    }

    [Test]
    public void ImportedTextureStreaming_FinalizesDeferredSparseTransitionsOnRenderThread()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs");

        source.ShouldContain("private int _sparseFinalizeScheduled;");
        source.ShouldContain("if (!RuntimeRenderingHostServices.FrameTiming.IsRenderThread)");
        source.ShouldContain("TextureStreaming.FinalizeSparseTransitions");
        source.ShouldContain("FinalizePendingSparseTransitionOnRenderThread(");
        source.ShouldContain("IsCurrentDeferredSparseTransition(");
    }

    [Test]
    public void ImportedTextureStreaming_EvaluatesResidencyAfterCollectBeforeSwapBuffers()
    {
        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs");
        string timerSource = ReadWorkspaceFile("XREngine.Runtime.Host/Core/Time/EngineTimer.cs");
        string interfaceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderSchedulingServices.cs");
        string hostSource = ReadWorkspaceFile("XREngine.Runtime.Host/RenderingHost/Engine.RuntimeRenderingHostServices.cs");

        timerSource.ShouldContain("PostCollectVisible?.Invoke();");
        interfaceSource.ShouldContain("void SubscribeViewportPostCollectVisible(Action postCollectVisible);");
        hostSource.ShouldContain("Engine.Time.Timer.PostCollectVisible += postCollectVisible;");
        managerSource.ShouldContain("SubscribeViewportPostCollectVisible(OnPostCollectVisible)");
        managerSource.ShouldContain("private void OnPostCollectVisible()");
        managerSource.ShouldContain("TextureStreaming.PostCollectVisible");

        int swapStart = managerSource.IndexOf("private void OnSwapBuffers()", StringComparison.Ordinal);
        swapStart.ShouldBeGreaterThanOrEqualTo(0);
        int swapEnd = managerSource.IndexOf("private void FinalizePendingSparseTransitions", swapStart, StringComparison.Ordinal);
        swapEnd.ShouldBeGreaterThan(swapStart);
        string swapBody = managerSource.Substring(swapStart, swapEnd - swapStart);
        swapBody.ShouldNotContain("Evaluate(frameId);");
        swapBody.ShouldNotContain("UpdatePromotionFades(frameId);");
    }

    [Test]
    public void ImportedTextureStreaming_UsesFullSparsePageCoverageUntilPageTrackingIsMaterialAware()
    {
        string source = ReadTextureStreamingSources();

        source.ShouldContain("private static readonly bool EnablePartialSparsePageResidency = false;");
        source.ShouldContain("material UV transforms, wrapping, filtering, or rapid camera movement");
        source.ShouldContain("if (!EnablePartialSparsePageResidency)");
        source.ShouldContain("return SparseTextureStreamingPageSelection.Full;");
    }

    [Test]
    public void ImportedTextureTiming_TotalThreshold_ReachesTextureLogSlowPath()
    {
        string diagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/TextureRuntimeDiagnostics.cs");
        string importedStreamingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.ImportedStreaming.cs");

        diagnosticsSource.ShouldContain("double cloneMilliseconds");
        diagnosticsSource.ShouldContain("|| totalMilliseconds >= totalThresholdMilliseconds");
        diagnosticsSource.ShouldContain("cloneMs={cloneMilliseconds:F2}");
        diagnosticsSource.ShouldContain("totalThresholdMs={totalThresholdMilliseconds:F2}");
        importedStreamingSource.ShouldContain("ImportedTextureTimingLogThresholdMilliseconds);");
    }

    [Test]
    public void ImportedTextureStreaming_PrefersFreshCachedTextureAssetAuthority()
    {
        string assetManagerSource = ReadWorkspaceFile("XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.SerializationAndCache.cs");
        string codecSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Serialization/TextureStreamingCacheCodec.cs");
        string registrationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Serialization/RenderingSerializationRegistration.cs");
        string streamingSource = ReadTextureStreamingSources();

        // Core resolves the texture authority through the rendering-owned cache
        // codec. A fresh, usable cache asset is the authority; otherwise the
        // source stays the authority and the cache is warmed in the background.
        registrationSource.ShouldContain("ThirdPartyCacheCodecRegistry.Install(new TextureStreamingCacheCodec())");
        codecSource.ShouldContain("public string? AuthorityRole => ThirdPartyCacheAuthorityRoles.TextureStreaming;");
        codecSource.ShouldContain("=> XRTexture2D.IsTextureStreamingAssetUsable(cachePath);");
        assetManagerSource.ShouldContain("ThirdPartyCacheCodecRegistry.FindByAuthorityRole(");
        assetManagerSource.ShouldContain("if (codec is not null && !codec.IsCacheUsable(cachePath))");
        assetManagerSource.ShouldContain("if (IsCacheAssetFresh(cachePath, normalizedPath, assetType))\n                return cachePath;");
        assetManagerSource.ShouldContain("QueueFeatureCacheImport(normalizedPath, cachePath, assetType, cacheVariantKey);");

        // Cache read and write decisions other than a hit or a write are logged.
        assetManagerSource.ShouldContain("\"[ThirdPartyCache] Cache read {0} for {1} '{2}'. reason={3}; detail={4}\"");
        assetManagerSource.ShouldContain("$\"[ThirdPartyCache] Cache write {result.Status} for {assetType.Name} '{cachePath}'. \"");
        assetManagerSource.ShouldNotContain("ShouldSuppressTextureStreamingCacheWarmup");
        assetManagerSource.ShouldNotContain("cache warmup suppressed during active imported-model scope");

        streamingSource.ShouldContain("if (XRTexture2D.HasAssetExtensionInternal(authorityPath))");
        streamingSource.ShouldContain("return new AssetTextureStreamingSource(authorityPath, originalSourcePath);");
        streamingSource.ShouldContain("return new ThirdPartyTextureStreamingSource(authorityPath);");
    }

    [Test]
    public void ImportedTextureStreaming_CooksCachedMipChainsOnGpuBeforeCpuFallback()
    {
        string codecSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Serialization/TextureStreamingCacheCodec.cs");
        string payloadSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.StreamingPayload.cs");
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Features/Streaming/OpenGLRenderer.TextureStreamingCacheCook.cs");
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture2D.TextureStreamingCacheCook.cs");
        codecSource.ShouldContain("XRTexture2D.TryCreateTextureStreamingCacheAsset(\n                texture,");
        codecSource.ShouldContain("TextureStreaming_v3_preview");

        payloadSource.ShouldContain("TryCreateTextureStreamingCacheAssetGpu");
        payloadSource.ShouldContain("RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadTask(");
        payloadSource.ShouldContain("TryBuildTexture2DMipChainRgba8Async");
        payloadSource.ShouldContain("Falling back to CPU mip generation");

        rendererSource.ShouldContain("Api.GetTextureSubImage(");
        rendererSource.ShouldContain("GLEnum.PixelPackBuffer");
        rendererSource.ShouldContain("Api.FenceSync(GLEnum.SyncGpuCommandsComplete");
        rendererSource.ShouldContain("PollTextureStreamingCacheMipChainReadback");

        textureSource.ShouldContain("TryPushBaseLevelAndGenerateMipmapsForTextureStreamingCacheCook");
        textureSource.ShouldContain("FinalizePushData(allowPostPushCallback: false)");
    }

    [Test]
    public void ImportedTextureStreaming_LogsCookedCacheReadAndReusesCanceledResidentData()
    {
        string managerSource = ReadTextureStreamingSources();
        string diagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/TextureRuntimeDiagnostics.cs");

        managerSource.ShouldContain("TextureRuntimeDiagnostics.LogCacheRead(");
        managerSource.ShouldContain("usedCookedPayload: true");
        managerSource.ShouldContain("internal static class TextureStreamingResidentDataReuseCache");
        managerSource.ShouldContain("TextureStreamingResidentDataReuseCache.TryGet");
        managerSource.ShouldContain("TextureRuntimeDiagnostics.LogResidentDataReused(");
        managerSource.ShouldContain("cancellationPhase = \"during decode/cache read\"");
        managerSource.ShouldContain("ReportCanceled(\"during finalization\")");

        diagnosticsSource.ShouldContain("Texture.CacheReadSlow");
        diagnosticsSource.ShouldContain("Texture.ResidentDataReused");
        diagnosticsSource.ShouldContain("cacheReadMs={cacheReadMilliseconds:F2}");
        diagnosticsSource.ShouldContain("canceled=");
        diagnosticsSource.ShouldContain("TransitionCanceledCount");
    }

    [Test]
    public void ImportedTextureStreaming_AllowsVisiblePreviewReadyRepromotionAfterDemotion()
    {
        string managerSource = ReadTextureStreamingSources();

        managerSource.ShouldContain("if (snapshot.LastVisibleFrameId == frameId && !snapshot.PreviewReady)");
        managerSource.ShouldContain("bool isPromotion = assignedResidentSize > currentResidentSize");
        managerSource.ShouldContain("bool isVisiblePreviewReadyPromotion = snapshot.LastVisibleFrameId == frameId");
        managerSource.ShouldContain("&& snapshot.PreviewReady");
        managerSource.ShouldContain("&& !isVisiblePreviewReadyPromotion");
    }

    [Test]
    public void ImportedTextureStreaming_VulkanHeapPressureSuppressesPromotionsWithoutZeroingManagedBudget()
    {
        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs");
        string normalizedManagerSource = managerSource.Replace("\r\n", "\n");
        string capabilitySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RendererModules/IVulkanAllocatorStreamingBackendCapability.cs");

        managerSource.ShouldContain("TryGetVulkanAllocatorStreamingPressure(");
        managerSource.ShouldContain("TryGetBackendCapability<IVulkanAllocatorStreamingBackendCapability>");
        capabilitySource.ShouldContain("TryGetAllocatorBudgetSnapshot(");
        managerSource.ShouldContain("bool suppressPromotionsForVulkanPressure = usingVulkanAllocatorBudget && vulkanAllocatorPressure;");
        managerSource.ShouldContain("if (suppressPromotionsForVulkanPressure && desiredResidentSize > snapshot.ResidentMaxDimension)");
        normalizedManagerSource.ShouldContain("if (suppressPromotionsForVulkanPressure)\n                    return false;");
        managerSource.ShouldNotContain("TryApplyVulkanAllocatorStreamingBudget(");
        managerSource.ShouldNotContain("ref trackedBudgetBytes");
        managerSource.ShouldNotContain("ref nonManagedBytes");
    }

    [Test]
    public void ImportedTextureStreaming_VulkanDensePromotionsUseSynchronizedUploadService()
    {
        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs");
        string providerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VulkanTextureStreamingBackendProvider.cs");
        string serviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanTextureUploadService.cs");
        string hookSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.TextureStreaming.cs");
        string imageTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs")
            + ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.ImportedUpload.cs");
        string denseBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VulkanDenseTextureResidencyBackend.cs");
        string glBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/OpenGLTextureResidencyBackends.cs");

        managerSource.ShouldContain("bool freezeResidentSizeForVulkan = ShouldFreezeVulkanImportedTextureResidency(snapshot);");
        managerSource.ShouldContain("desiredResidentSize = ResolveVulkanSafeResidentSize(snapshot, desiredResidentSize);");
        managerSource.ShouldContain("&& !freezeResidentSizeForVulkan");
        managerSource.ShouldContain("private static uint ResolveVulkanSafeResidentSize(");
        managerSource.ShouldContain("private static bool ShouldFreezeVulkanImportedTextureResidency(ImportedTextureStreamingSnapshot snapshot)");
        managerSource.ShouldContain("VulkanProvider.IsSynchronizedUploadAvailable");
        providerSource.ShouldContain("VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable");
        managerSource.ShouldContain("XREngineEnvironmentVariables.VulkanImportedTexturePreviewFreeze");
        managerSource.ShouldContain("&& IsVulkanImportedTexturePreviewFreezeForced();");
        managerSource.ShouldContain("RenderDiagnosticsFlags.VkImportedTexturePreviewFreeze");
        managerSource.ShouldNotContain("&& (!VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable");
        managerSource.ShouldContain("ShouldPreserveVulkanDenseResidentSizeWithoutPressure(");
        managerSource.ShouldContain("ShouldPreserveDenseResidentSizeWithoutPressure(");
        managerSource.ShouldContain("VulkanDenseNonPressureDemotionPreserveBudgetFillRatio");
        ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureResidencyPolicy.cs")
            .ShouldContain("visibility grace demotion");
        managerSource.ShouldContain("includeMipChain = ShouldIncludeResidentMipChain(backend, normalizedTarget);");
        managerSource.ShouldContain("private static bool ShouldIncludeResidentMipChain(ITextureResidencyBackend backend, uint normalizedTarget)");
        managerSource.ShouldContain("|| VulkanProvider.IsSynchronizedUploadAvailable;");
        managerSource.ShouldNotContain("includeMipChain = normalizedTarget > minimumResidentSize;");
        managerSource.ShouldNotContain("includeMipChain = normalizedTarget > backend.PreviewMaxDimension;");
        imageTextureSource.ShouldNotContain("WaitForInFlightWorkBeforeImportedTextureReplacement(");
        imageTextureSource.ShouldNotContain("Renderer.WaitForAllInFlightWork();");
        imageTextureSource.ShouldContain("BackendContext.Resources.Images.RetireOwnedResources(new RetiredImageResources(");

        serviceSource.ShouldContain("internal sealed partial class VulkanTextureUploadService");
        serviceSource.ShouldContain("VulkanTextureUploadGenerationState");
        serviceSource.ShouldContain("RejectStaleOrCanceledResult(");
        serviceSource.ShouldContain("TryScheduleImportedTextureUpload(");
        // The resource runtime owns one upload service. The frame loop drives it
        // and is the bound scheduler for synchronized imported texture uploads.
        ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs")
            .ShouldContain("Uploads = new VulkanTextureUploadService();");
        hookSource.ShouldContain("_resourceRuntime.Uploads.ProcessPendingUploads(");
        hookSource.ShouldContain("internal sealed partial class VulkanFrameLoop : IVulkanTextureUploadScheduler");
        hookSource.ShouldContain("TryScheduleImportedTextureResidencyTransition(");
        denseBackendSource.ShouldContain("VulkanTextureStreamingBackendProvider.Instance.TryScheduleSynchronizedUpload(");
        providerSource.ShouldContain("IVulkanTextureUploadScheduler? scheduler = Volatile.Read(ref _scheduler);");
        glBackendSource.ShouldContain("TextureStreamingBackendRegistry.TryGet(");
        glBackendSource.ShouldContain("RuntimeGraphicsApiKind.Vulkan");
    }

    [Test]
    public void VulkanTextureUploadFrameOps_DoNotPoisonReusablePrimaryCommandBufferCache()
    {
        string framePlanSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/FramePlan.cs");
        string recordingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs");

        // The sealed frame plan keeps texture uploads in their own operation
        // stream. Reusable primary content is recorded from the static stream only.
        framePlanSource.ShouldContain("return new FrameOperationSequence(_operations);");
        framePlanSource.ShouldContain("return new FrameOperationSequence(_textureUploadOperations);");

        // Uploads record into a separate transient upload command buffer before
        // the primary is reused or recorded.
        recordingSource.ShouldContain("input.FramePlan.GetNativeTextureUploadOperationsForRecording();");
        recordingSource.ShouldContain("!TryRecordTextureUploadCommandBuffer(");
        recordingSource.ShouldContain("out uploadCommandBuffer,");
        recordingSource.ShouldContain("TextureUploadCommandBuffer = uploadCommandBuffer,");
        recordingSource.ShouldContain("\"texture-upload operations are not admitted by the executable output DAG\"");
        int uploadRecording = recordingSource.IndexOf("!TryRecordTextureUploadCommandBuffer(", StringComparison.Ordinal);
        int staticOperations = recordingSource.IndexOf("? input.FramePlan.GetNativeStaticOperationsForRecording()", StringComparison.Ordinal);
        uploadRecording.ShouldBeGreaterThanOrEqualTo(0);
        staticOperations.ShouldBeGreaterThan(uploadRecording);
    }

    [Test]
    public void VulkanTextureSamplers_EnableAnisotropyWhenDeviceFeatureIsEnabled()
    {
        string imageTextureSource = SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string textureViewSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");
        string samplerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Samplers/VkSampler.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");
        string capabilityPublicationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDevicePublication.cs");

        logicalDeviceSource.ShouldContain("deviceFeatures.SamplerAnisotropy = Vk.True;");
        logicalDeviceSource.ShouldContain("_supportsAnisotropy = true;");
        // The enabled device feature is published as a device capability.
        capabilityPublicationSource.ShouldContain("Include(ref capabilities, EVulkanDeviceCapability.Anisotropy, MutableCapabilities._supportsAnisotropy);");

        imageTextureSource.ShouldContain("if (BackendContext.Supports(EVulkanDeviceCapability.Anisotropy))");
        imageTextureSource.ShouldContain("AnisotropyEnable = anisotropyEnable");
        imageTextureSource.ShouldContain("MaxAnisotropy = maxAnisotropy");

        textureViewSource.ShouldContain("if (BackendContext.Supports(EVulkanDeviceCapability.Anisotropy))");
        textureViewSource.ShouldContain("samplerInfo.AnisotropyEnable = Vk.True;");
        textureViewSource.ShouldContain("samplerInfo.MaxAnisotropy = MathF.Min(props.Limits.MaxSamplerAnisotropy, 16f);");
        textureViewSource.ShouldNotContain("EVulkanDeviceCapability.Anisotropy) && Data.NumLevels > 1");

        samplerSource.ShouldContain("Data.EnableAnisotropy && BackendContext.Supports(EVulkanDeviceCapability.Anisotropy)");
        samplerSource.ShouldContain("samplerInfo.AnisotropyEnable = Vk.True;");
        samplerSource.ShouldContain("samplerInfo.MaxAnisotropy = MathF.Min(samplerInfo.MaxAnisotropy, props.Limits.MaxSamplerAnisotropy);");
    }

    [Test]
    public void ImportedTextureStreaming_VulkanFreezeTelemetryIsExplicitInLogsAndUi()
    {
        string contractsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureStreamingContracts.cs");
        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs");
        string diagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/TextureRuntimeDiagnostics.cs");
        string panelSource = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.TextureStreamingPanel.cs");

        contractsSource.ShouldContain("bool VulkanFrozen");
        contractsSource.ShouldContain("string FreezeReason");
        contractsSource.ShouldContain("long ResidentGeneration");
        contractsSource.ShouldContain("long PublishedGeneration");
        contractsSource.ShouldContain("long UploadGeneration");

        managerSource.ShouldContain("ResolveTelemetryBackendName(");
        managerSource.ShouldContain("Vulkan dense tiered (legacy GL compat backend)");
        diagnosticsSource.ShouldContain("vulkanFrozen=");
        diagnosticsSource.ShouldContain("freezeReason='");
        diagnosticsSource.ShouldContain("Texture.VulkanUploadState");
        diagnosticsSource.ShouldContain("Texture.VulkanUploadRejected");
        panelSource.ShouldContain("t.DisplayBackendName");
        panelSource.ShouldContain("tex.DisplayBackendName");
        panelSource.ShouldContain("tex.ResidentGeneration");
        panelSource.ShouldContain("tex.PublishedGeneration");
        panelSource.ShouldContain("tex.UploadGeneration");
        panelSource.ShouldContain("vk-frozen");
    }

    [Test]
    public void VulkanTextureUploadService_RejectsStaleOrCanceledGenerationBeforePublication()
    {
        VulkanTextureUploadService service = new();
        XRTexture2D texture = new() { Name = "stale-vulkan-upload" };
        VulkanImportedTextureUploadRequest request = new(
            new WeakReference<XRTexture2D>(texture),
            texture.Name,
            "stale.png",
            512u,
            new VulkanImportedTextureUploadMipRange(0, 1, 512u, 512u),
            XREngine.Data.Rendering.ESizedInternalFormat.Rgba8,
            "sRGB",
            512L * 512L * 4L,
            Ticket: default,
            StreamingGeneration: 7L,
            TextureUploadPriorityClass.VisibleNow,
            CancellationToken.None);

        service.ShouldAcceptResult(request, currentStreamingGeneration: 8L).ShouldBeFalse();
        VulkanImportedTextureUploadResult result = service.RejectStaleOrCanceledResult(request, currentStreamingGeneration: 8L);
        result.State.ShouldBe(VulkanImportedTextureUploadResultState.Canceled);
        result.SourceGeneration.ShouldBe(7L);
        result.FailureReason.ShouldNotBeNull();
        result.FailureReason.ShouldContain("stale generation");

        using CancellationTokenSource cts = new();
        cts.Cancel();
        VulkanImportedTextureUploadRequest canceled = request with { CancellationToken = cts.Token };
        service.ShouldAcceptResult(canceled, currentStreamingGeneration: 7L).ShouldBeFalse();
    }

    [Test]
    public void VulkanTextureUploadService_RecordsCopiesBarriersAndFrameSafePublication()
    {
        const string VulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string frameOpSource = ReadWorkspaceFile(VulkanRoot + "Commands/FrameOps/TextureUploadFrameOp.cs");
        string frameOpStreamSource = ReadWorkspaceFile(VulkanRoot + "Commands/FrameOps/FrameOperationStream.cs");
        string commandBufferSource = ReadWorkspaceFile(VulkanRoot + "Commands/Transfers/VulkanRenderer.TextureUploadRecording.cs");
        string serviceSource = SourceContractWorkspace.ReadVulkanSourcesContaining("partial class VulkanTextureUploadService");
        string pendingUploadSource = ReadWorkspaceFile(VulkanRoot + "Resources/Uploads/VulkanImportedTexturePendingUpload.cs");
        string hookSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.TextureStreaming.cs");
        string imageTextureSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/Textures/VkImageBackedTexture.ImportedUpload.cs");
        string materialTableSource = ReadWorkspaceFile(VulkanRoot + "Resources/Authority/VulkanRenderer.BindlessMaterialTextureTable.cs");
        string backendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/OpenGLTextureResidencyBackends.cs");
        string diagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/TextureRuntimeDiagnostics.cs");
        string deviceContextSource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceContext.Instance.cs");

        // Uploads are primary plan nodes with their own payload stream.
        frameOpSource.ShouldContain("public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.TextureUpload;");
        frameOpStreamSource.ShouldContain("case EVulkanPrimaryPlanNodeKind.TextureUpload: _payloads.TextureUploads[i] = new(((TextureUploadFrameOp)op).Upload); break;");

        // Recording: transfer-destination barrier, buffer-to-image copies, and a
        // barrier to the final layout. Publication waits for command completion.
        commandBufferSource.ShouldContain("internal unsafe void RecordTextureUploadOp(");
        commandBufferSource.ShouldContain("NewLayout = ImageLayout.TransferDstOptimal,");
        commandBufferSource.ShouldContain("CopyPreparedUploadBufferToImage(");
        commandBufferSource.ShouldContain("OldLayout = ImageLayout.TransferDstOptimal,");
        commandBufferSource.ShouldContain("NewLayout = upload.FinalLayout,");
        commandBufferSource.ShouldContain("VulkanTextureUploadGenerationState.DescriptorPublishPending");
        commandBufferSource.ShouldContain("QueueRecordedTextureUploadForSubmit(upload);");

        serviceSource.ShouldContain("VulkanImportedTexturePendingUpload");
        serviceSource.ShouldContain("upload.Texture.PublishSynchronizedImportedTextureUpload(");
        serviceSource.ShouldContain("RetireTextureUploadStagingResources(resourceRuntime, upload);");
        serviceSource.ShouldContain("VulkanTextureUploadGenerationState.Retired");
        serviceSource.ShouldNotContain("NewTransferCommandScope()");
        serviceSource.ShouldNotContain("NewCommandScope()");
        pendingUploadSource.ShouldContain("StagingResources");
        pendingUploadSource.ShouldContain("PreparedTimestamp");
        hookSource.ShouldContain("EnqueueImportedTextureUpload(");

        imageTextureSource.ShouldContain("TryCreateSynchronizedImportedUpload(");
        imageTextureSource.ShouldContain("ReleasePreparedImportedUploadResources(");
        imageTextureSource.ShouldContain("BackendContext.DeviceContext.SetDebugObjectName(\n                        ObjectType.Image,");
        imageTextureSource.ShouldContain("MarkDescriptorDirty();");
        materialTableSource.ShouldContain("FlushGlobalMaterialTextureDescriptorUpdatesLocked();");
        // The new descriptor commits before the previous generation retires.
        int descriptorPublication = imageTextureSource.IndexOf(
            "BackendContext.Resources.Descriptors.PublishGlobalMaterialTextureDescriptor(",
            StringComparison.Ordinal);
        int previousRetirement = imageTextureSource.IndexOf(
            "BackendContext.Resources.Images.RetireOwnedResources(\n                    in previousResources,",
            StringComparison.Ordinal);
        descriptorPublication.ShouldBeGreaterThanOrEqualTo(0);
        previousRetirement.ShouldBeGreaterThan(descriptorPublication);
        backendSource.ShouldContain("TryScheduleVulkanSynchronizedUpload(");
        backendSource.ShouldContain("RuntimeGraphicsApiKind.Vulkan");
        diagnosticsSource.ShouldContain("Texture.VulkanUploadLatency");
        deviceContextSource.ShouldContain("public unsafe void SetDebugObjectName(ObjectType objectType");
    }

    [Test]
    public void VulkanTextureUploadService_ValidatesCopyRegionsAgainstPreparedImageExtent()
    {
        VulkanImportedTextureUploadStagingResource invalidCopy = new(
            default,
            default,
            default,
            new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D(64u, 64u, 1u),
            },
            64u * 64u * 4u,
            1UL);

        VulkanImportedTextureUploadValidation.TryValidateCopyRegions(
            "promotion",
            17UL,
            new Extent3D(8u, 8u, 1u),
            mipLevels: 1u,
            arrayLayers: 1u,
            [invalidCopy],
            out string? failureReason).ShouldBeFalse();
        failureReason.ShouldNotBeNull();
        failureReason.ShouldContain("64x64x1");
        failureReason.ShouldContain("mip 0 extent 8x8x1");

        BufferImageCopy validCopyRegion = invalidCopy.CopyRegion;
        validCopyRegion.ImageExtent = new Extent3D(64u, 64u, 1u);
        VulkanImportedTextureUploadStagingResource validBaseCopy = new(
            default,
            default,
            default,
            validCopyRegion,
            64u * 64u * 4u,
            1UL);

        VulkanImportedTextureUploadValidation.TryValidateCopyRegions(
            "promotion",
            18UL,
            new Extent3D(64u, 64u, 1u),
            mipLevels: 1u,
            arrayLayers: 1u,
            [validBaseCopy],
            out failureReason).ShouldBeTrue();
        failureReason.ShouldBeNull();
    }

    [Test]
    public void VulkanTextureUploadService_SizesPendingUploadsFromLogicalResidentLayout()
    {
        string imageTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.ImportedUpload.cs");
        string pendingUploadSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanImportedTexturePendingUpload.cs");
        string validationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanImportedTextureUploadValidation.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Transfers/VulkanRenderer.TextureUploadRecording.cs");

        // The pending upload is sized from the logical layout of the applied
        // resident data, not from the currently published image.
        imageTextureSource.ShouldContain("XRTexture2D.ApplyResidentDataForVulkanPublication(texture, residentData, includeMipChain);");
        imageTextureSource.ShouldContain("uploadLayout = NormalizeLayout(DescribeTexture());");
        imageTextureSource.ShouldContain("Extent3D extent = uploadLayout.Extent;");
        imageTextureSource.ShouldContain("uint mipLevels = Math.Max(uploadLayout.MipLevels, 1u);");
        imageTextureSource.ShouldContain("uint arrayLayers = Math.Max(uploadLayout.ArrayLayers, 1u);");
        imageTextureSource.ShouldContain("TextureLayout publishedLayout = new(");
        imageTextureSource.ShouldContain("Math.Max(pendingUpload.ArrayLayers, 1u)");

        pendingUploadSource.ShouldContain("public uint ArrayLayers { get; }");
        pendingUploadSource.ShouldContain("public bool TryValidateCopyRegions(out string? failureReason)");
        pendingUploadSource.ShouldContain("=> VulkanImportedTextureUploadValidation.TryValidateCopyRegions(");
        // Transfer ownership validation also validates the copy regions.
        pendingUploadSource.ShouldContain("return TryValidateCopyRegions(out failureReason);");
        validationSource.ShouldContain("copy[{i}] extent");
        commandBufferSource.ShouldContain("if (!upload.TryValidateCopyRegions(out string? validationFailure))");
        commandBufferSource.ShouldContain("VulkanTextureUploadGenerationState.Failed");
    }

    [Test]
    public void VulkanCommandChains_TreatImportedTextureUploadsAsPrimaryCommandWork()
    {
        string commandBufferSource = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "EVulkanPrimaryPlanNodeKind.TextureUpload => operations.Stream.GetTextureUpload(index).GetHashCode()",
            "EVulkanPrimaryPlanNodeKind.TextureUpload => RecordTextureUploadPayload(");
        string commandChainSource = SourceContractWorkspace.ReadVulkanSourcesContaining("case TextureUploadFrameOp upload:");

        // Texture uploads are primary plan nodes: the primary recorder records
        // them and the reuse dependency signature includes each upload payload.
        commandBufferSource.ShouldContain("EVulkanPrimaryPlanNodeKind.TextureUpload => operations.Stream.GetTextureUpload(index).GetHashCode()");
        commandBufferSource.ShouldContain("EVulkanPrimaryPlanNodeKind.TextureUpload => RecordTextureUploadPayload(");
        commandBufferSource.ShouldContain("RecordTextureUploadOp(state.CommandBuffer, payload.Upload);");
        commandChainSource.ShouldContain("case TextureUploadFrameOp upload:");
        commandChainSource.ShouldContain("hash.Add(upload.Upload.PublicationToken);");
        commandChainSource.ShouldContain("hash.Add(upload.Upload.Request.StreamingGeneration);");
        commandChainSource.ShouldContain("hash.Add(upload.Upload.Image.Handle);");
        commandChainSource.ShouldContain("hash.Add(upload.Upload.StagingResources.Length);");
    }

    [Test]
    public void ImportedTextureStreaming_PrioritizesVisibleLargeScreenTransitions()
    {
        string managerSource = ReadTextureStreamingSources();

        managerSource.ShouldContain("internal sealed class PriorityAsyncSemaphore");
        managerSource.ShouldContain("await DecodeGate.WaitAsync(priority, cancellationToken)");
        managerSource.ShouldContain("ResolveTransitionJobPriority(");
        managerSource.ShouldContain("snapshot.MaxProjectedPixelSpan >= UrgentVisibleProjectedPixelSpan");
        managerSource.ShouldContain("snapshot.MaxScreenCoverage >= UrgentVisibleScreenCoverage");
        managerSource.ShouldContain("priority: transitionPriority");
    }

    [Test]
    public void ImportedTextureStreaming_ProgressiveUploadsYieldToVisibleWork()
    {
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.cs")
            + ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/TextureUploadScheduler.cs");
        string importedStreamingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.ImportedStreaming.cs");

        // The upload scheduler guards its registry with one lock.
        textureSource.ShouldContain("private readonly Dictionary<XRTexture2D, TextureUploadWorkItem> _progressiveUploads = [];");
        textureSource.ShouldContain("lock (_slotLock)\n            return _progressiveUploads.TryAdd(texture, workItem);");
        textureSource.ShouldContain("HasHigherPriorityProgressiveUpload(texture, workItem)");
        textureSource.ShouldContain("ReleaseProgressiveUploadSlot();");
        textureSource.ShouldContain("TextureUploadPriorityClass.VisibleNow => 3");
        importedStreamingSource.ShouldContain("TextureUploadPriorityClass priorityClass = TextureUploadPriorityClass.Background");
    }

    [Test]
    public void ImportedTextureStreaming_SparseDemotionRefreshesTargetMipsBeforeSamplingThem()
    {
        string sparseSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture2D.SparseStreaming.cs");
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.ImportedStreaming.cs");
        string managerSource = ReadTextureStreamingSources();

        sparseSource.ShouldContain("Demotion still has to populate the target mip range");
        sparseSource.ShouldContain("UploadSparseResidentMipmaps(request, support, desiredPageSelection, numSparseLevels);");
        sparseSource.ShouldContain("SetSparseMipSamplingRange(requestedBaseMipLevel, request.LogicalMipCount - 1);");

        textureSource.ShouldContain("internal static uint GetMinimumResidentSize(uint sourceMaxDimension)");
        managerSource.ShouldContain("XRTexture2D.GetMinimumResidentSize(sourceMaxDimension)");
    }

    [Test]
    public void ImportedTextureStreaming_DenseResidentUploadsClearSparseState()
    {
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.ImportedStreaming.cs");
        string glTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture2D.cs");
        string diagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/TextureRuntimeDiagnostics.cs");

        textureSource.ShouldContain("TextureRuntimeDiagnostics.LogSparseStateClearedForDenseUpload(");
        textureSource.ShouldContain("texture.ClearSparseTextureStreamingState();");
        textureSource.ShouldContain("old sparse resident base");
        glTextureSource.ShouldContain("bool switchingFromSparseStorage = _sparseStorageAllocated && !Data.SparseTextureStreamingEnabled;");
        glTextureSource.ShouldContain("if (switchingFromSparseStorage");
        diagnosticsSource.ShouldContain("Texture.SparseStateClearedForDenseUpload");
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    private static string ReadTextureStreamingSources()
    {
        return string.Concat(
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureStreamingContracts.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureStreamingSources.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureResidencyPolicy.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureStreamingRegistry.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureTransitionQueue.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/OpenGLTextureResidencyBackends.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/PriorityAsyncSemaphore.cs"));
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
