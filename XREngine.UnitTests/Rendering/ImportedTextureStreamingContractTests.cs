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
        source.ShouldContain("if (!RuntimeRenderingHostServices.Current.IsRenderThread)");
        source.ShouldContain("TextureStreaming.FinalizeSparseTransitions");
        source.ShouldContain("FinalizePendingSparseTransitionOnRenderThread(");
        source.ShouldContain("IsCurrentDeferredSparseTransition(");
    }

    [Test]
    public void ImportedTextureStreaming_EvaluatesResidencyAfterCollectBeforeSwapBuffers()
    {
        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs");
        string timerSource = ReadWorkspaceFile("XREngine/Core/Time/EngineTimer.cs");
        string interfaceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderSchedulingServices.cs");
        string hostSource = ReadWorkspaceFile("XREngine/Engine/Engine.RuntimeRenderingHostServices.cs");

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
        string assetManagerSource = ReadWorkspaceFile("XRENGINE/Core/Engine/Loading/AssetManager.Loading.SerializationAndCache.cs");
        string streamingSource = ReadTextureStreamingSources();

        assetManagerSource.ShouldContain("XRTexture2D.IsTextureStreamingAssetUsable(cachePath)");
        assetManagerSource.ShouldContain("LogTextureCacheEvent(\"Texture.CacheHit\"");
        assetManagerSource.ShouldContain("LogTextureCacheEvent(\"Texture.CacheMiss\"");
        assetManagerSource.ShouldContain("\"Texture.CacheStale\"");
        assetManagerSource.ShouldContain("LogTextureCacheEvent(\"Texture.CacheFallbackToSource\"");
        assetManagerSource.ShouldContain("LogTextureCacheEvent(\"Texture.CacheWrite\"");
        assetManagerSource.ShouldContain("QueueTextureStreamingCacheImport(normalizedPath, cachePath, cacheVariantKey);");
        assetManagerSource.ShouldNotContain("ShouldSuppressTextureStreamingCacheWarmup");
        assetManagerSource.ShouldNotContain("cache warmup suppressed during active imported-model scope");

        streamingSource.ShouldContain("if (XRTexture2D.HasAssetExtensionInternal(authorityPath))");
        streamingSource.ShouldContain("return new AssetTextureStreamingSource(authorityPath, originalSourcePath);");
        streamingSource.ShouldContain("return new ThirdPartyTextureStreamingSource(authorityPath);");
    }

    [Test]
    public void ImportedTextureStreaming_CooksCachedMipChainsOnGpuBeforeCpuFallback()
    {
        string assetManagerSource = ReadWorkspaceFile("XRENGINE/Core/Engine/Loading/AssetManager.Loading.SerializationAndCache.cs");
        string payloadSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.StreamingPayload.cs");
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Features/Streaming/OpenGLRenderer.TextureStreamingCacheCook.cs");
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture2D.TextureStreamingCacheCook.cs");
        string normalizedAssetManagerSource = assetManagerSource.Replace("\r\n", "\n");

        normalizedAssetManagerSource.ShouldContain("XRTexture2D.TryCreateTextureStreamingCacheAsset(\n                texture,");
        assetManagerSource.ShouldContain("TextureStreaming_v3_preview");

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

        managerSource.ShouldContain("TryGetVulkanAllocatorStreamingPressure(");
        managerSource.ShouldContain("bool suppressPromotionsForVulkanPressure = usingVulkanAllocatorBudget && vulkanAllocatorPressure;");
        managerSource.ShouldContain("if (suppressPromotionsForVulkanPressure && desiredResidentSize > snapshot.ResidentMaxDimension)");
        managerSource.ShouldContain("if (suppressPromotionsForVulkanPressure)\n                    return false;");
        managerSource.ShouldNotContain("TryApplyVulkanAllocatorStreamingBudget(");
        managerSource.ShouldNotContain("ref trackedBudgetBytes");
        managerSource.ShouldNotContain("ref nonManagedBytes");
    }

    [Test]
    public void ImportedTextureStreaming_VulkanDensePromotionsUseSynchronizedUploadService()
    {
        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs");
        string serviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanTextureUploadService.cs");
        string hookSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Streaming/VulkanRenderer.TextureStreamingHooks.cs");
        string imageTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string denseBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VulkanDenseTextureResidencyBackend.cs");
        string glBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/OpenGLTextureResidencyBackends.cs");

        managerSource.ShouldContain("bool freezeResidentSizeForVulkan = ShouldFreezeVulkanImportedTextureResidency(snapshot);");
        managerSource.ShouldContain("desiredResidentSize = ResolveVulkanSafeResidentSize(snapshot, desiredResidentSize);");
        managerSource.ShouldContain("&& !freezeResidentSizeForVulkan");
        managerSource.ShouldContain("private static uint ResolveVulkanSafeResidentSize(");
        managerSource.ShouldContain("private static bool ShouldFreezeVulkanImportedTextureResidency(ImportedTextureStreamingSnapshot snapshot)");
        managerSource.ShouldContain("docs/work/todo/rendering/vulkan-imported-texture-streaming-todo.md");
        managerSource.ShouldContain("VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable");
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
        managerSource.ShouldContain("|| VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable;");
        managerSource.ShouldNotContain("includeMipChain = normalizedTarget > minimumResidentSize;");
        managerSource.ShouldNotContain("includeMipChain = normalizedTarget > backend.PreviewMaxDimension;");
        imageTextureSource.ShouldNotContain("WaitForInFlightWorkBeforeImportedTextureReplacement(");
        imageTextureSource.ShouldNotContain("Renderer.WaitForAllInFlightWork();");
        imageTextureSource.ShouldContain("Renderer.RetireImageResources(new RetiredImageResources(");

        serviceSource.ShouldContain("internal sealed class VulkanTextureUploadService");
        serviceSource.ShouldContain("VulkanTextureUploadGenerationState");
        serviceSource.ShouldContain("RejectStaleOrCanceledResult(");
        serviceSource.ShouldContain("TryScheduleImportedTextureUpload(");
        hookSource.ShouldContain("private readonly VulkanTextureUploadService _textureUploadService = new();");
        hookSource.ShouldContain("TryScheduleImportedTextureResidencyTransition(");
        denseBackendSource.ShouldContain("RuntimeRenderingHostServices.Current.CurrentRenderer ?? AbstractRenderer.Current");
        glBackendSource.ShouldContain("RuntimeRenderingHostServices.Current.CurrentRenderer ?? AbstractRenderer.Current");
    }

    [Test]
    public void VulkanTextureUploadFrameOps_DoNotPoisonReusablePrimaryCommandBufferCache()
    {
        string recordingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        recordingSource.ShouldContain("if (hasTextureUploadFrameOps)\n                MarkCommandBufferVariantTransientAfterTextureUpload(variant);");
        recordingSource.ShouldContain("private static void MarkCommandBufferVariantTransientAfterTextureUpload(CommandBufferCacheVariant variant)");
        recordingSource.ShouldContain("variant.DirtyReason = \"transient texture upload\";");
        recordingSource.ShouldContain("variant.FrameOpsSignature = ulong.MaxValue;");
        recordingSource.ShouldContain("variant.RecordedSwapchainWriteCount = 0;");
        recordingSource.ShouldContain("variant.RecordedSwapchainWriteCount = 0;");
        recordingSource.ShouldContain("if (variant.RecordedSwapchainWriteCount <= 0 ||");
    }

    [Test]
    public void VulkanTextureSamplers_EnableAnisotropyWhenDeviceFeatureIsEnabled()
    {
        string imageTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string textureViewSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");
        string samplerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Samplers/VKSampler.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");

        logicalDeviceSource.ShouldContain("deviceFeatures.SamplerAnisotropy = Vk.True;");
        logicalDeviceSource.ShouldContain("_supportsAnisotropy = true;");

        imageTextureSource.ShouldContain("if (Renderer.SamplerAnisotropyEnabled)");
        imageTextureSource.ShouldContain("AnisotropyEnable = anisotropyEnable");
        imageTextureSource.ShouldContain("MaxAnisotropy = maxAnisotropy");

        textureViewSource.ShouldContain("if (Renderer.SamplerAnisotropyEnabled)");
        textureViewSource.ShouldContain("samplerInfo.AnisotropyEnable = Vk.True;");
        textureViewSource.ShouldContain("samplerInfo.MaxAnisotropy = MathF.Min(props.Limits.MaxSamplerAnisotropy, 16f);");
        textureViewSource.ShouldNotContain("Renderer.SamplerAnisotropyEnabled && Data.NumLevels > 1");

        samplerSource.ShouldContain("Data.EnableAnisotropy && Renderer.SamplerAnisotropyEnabled");
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
        string meshRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs")
            + ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.FrameOpDiagnostics.cs");
        string serviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanTextureUploadService.cs");
        string hookSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Streaming/VulkanRenderer.TextureStreamingHooks.cs");
        string imageTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string backendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/OpenGLTextureResidencyBackends.cs");
        string diagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/TextureRuntimeDiagnostics.cs");
        string validationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Validation.cs");

        meshRendererSource.ShouldContain("TextureUploadFrameOp");
        meshRendererSource.ShouldContain("FrameOpKindTextureUpload");
        commandBufferSource.ShouldContain("RecordTextureUploadOp(");
        commandBufferSource.ShouldContain("ImageLayout.TransferDstOptimal");
        commandBufferSource.ShouldContain("Api!.CmdCopyBufferToImage(");
        commandBufferSource.ShouldContain("ImageLayout.ShaderReadOnlyOptimal");
        commandBufferSource.ShouldContain("PublishSynchronizedImportedTextureUpload(upload)");
        commandBufferSource.ShouldContain("RetireTextureUploadStagingResources(upload)");
        commandBufferSource.ShouldContain("VulkanTextureUploadGenerationState.Retired");

        serviceSource.ShouldContain("VulkanImportedTexturePendingUpload");
        serviceSource.ShouldContain("StagingResources");
        serviceSource.ShouldContain("PreparedTimestamp");
        serviceSource.ShouldNotContain("NewTransferCommandScope()");
        serviceSource.ShouldNotContain("NewCommandScope()");
        hookSource.ShouldContain("EnqueueImportedTextureUpload(");
        imageTextureSource.ShouldContain("TryCreateSynchronizedImportedUpload(");
        imageTextureSource.ShouldContain("ReleasePreparedImportedUploadResources(");
        imageTextureSource.ShouldContain("Renderer.MarkCommandBuffersDirty(");
        imageTextureSource.ShouldContain("ImportedTextureUploadPublished texture=");
        imageTextureSource.ShouldContain("Renderer.SetDebugObjectName(ObjectType.Image");
        imageTextureSource.ShouldContain("Renderer.SetDebugObjectName(ObjectType.Buffer");
        backendSource.ShouldContain("TryScheduleVulkanSynchronizedUpload(");
        backendSource.ShouldContain("RuntimeGraphicsApiKind.Vulkan");
        diagnosticsSource.ShouldContain("Texture.VulkanUploadLatency");
        validationSource.ShouldContain("SetDebugObjectName(ObjectType objectType");
    }

    [Test]
    public void VulkanTextureUploadService_ValidatesCopyRegionsAgainstPreparedImageExtent()
    {
        VulkanImportedTextureUploadStagingResource invalidCopy = new(
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
            64u * 64u * 4u);

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
            validCopyRegion,
            64u * 64u * 4u);

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
        string imageTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string serviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanTextureUploadService.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string transferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanRenderer.TextureUploadTransfer.cs");

        imageTextureSource.ShouldContain("Extent3D extent = _layout.Extent;");
        imageTextureSource.ShouldContain("uint mipLevels = Math.Max(_layout.MipLevels, 1u);");
        imageTextureSource.ShouldContain("uint arrayLayers = Math.Max(_layout.ArrayLayers, 1u);");
        imageTextureSource.ShouldContain("VulkanImportedTextureUploadValidation.TryValidateCopyRegions(");
        imageTextureSource.ShouldContain("new TextureLayout(");
        imageTextureSource.ShouldContain("Math.Max(pendingUpload.ArrayLayers, 1u)");

        serviceSource.ShouldContain("public uint ArrayLayers { get; }");
        serviceSource.ShouldContain("public bool TryValidateCopyRegions(out string? failureReason)");
        serviceSource.ShouldContain("copy[{i}] extent");
        commandBufferSource.ShouldContain("if (!upload.TryValidateCopyRegions(out string? validationFailure))");
        commandBufferSource.ShouldContain("VulkanTextureUploadGenerationState.Failed");
        transferSource.ShouldContain("if (!upload.TryValidateCopyRegions(out string? validationFailure))");
    }

    [Test]
    public void VulkanCommandChains_TreatImportedTextureUploadsAsPrimaryCommandWork()
    {
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs")
            + ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.FrameOpDiagnostics.cs");
        string commandChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandChainLowering.cs");

        commandBufferSource.ShouldContain("private static bool HasTextureUploadFrameOps(FrameOp[] ops)");
        commandBufferSource.ShouldContain("usingCommandChains && variant.FrameOpsSignature != frameOpsSignature");
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

        textureSource.ShouldContain("ConcurrentDictionary<XRTexture2D, TextureUploadWorkItem>");
        textureSource.ShouldContain("HasHigherPriorityProgressiveUpload(texture, workItem)");
        textureSource.ShouldContain("ReleaseProgressiveUploadSlot();");
        textureSource.ShouldContain("TextureUploadPriorityClass.VisibleNow => 3");
        importedStreamingSource.ShouldContain("TextureUploadPriorityClass priorityClass = TextureUploadPriorityClass.Background");
    }

    [Test]
    public void ImportedTextureStreaming_SparseDemotionRefreshesTargetMipsBeforeSamplingThem()
    {
        string sparseSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture2D.SparseStreaming.cs");
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
        string glTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture2D.cs");
        string diagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/TextureRuntimeDiagnostics.cs");

        textureSource.ShouldContain("TextureRuntimeDiagnostics.LogSparseStateClearedForDenseUpload(");
        textureSource.ShouldContain("texture.ClearSparseTextureStreamingState();");
        textureSource.ShouldContain("old sparse resident base");
        glTextureSource.ShouldContain("bool switchingFromSparseStorage = _sparseStorageAllocated && !Data.SparseTextureStreamingEnabled;");
        glTextureSource.ShouldContain("if (switchingFromSparseStorage");
        diagnosticsSource.ShouldContain("Texture.SparseStateClearedForDenseUpload");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path);
    }

    private static string ReadTextureStreamingSources()
    {
        return string.Concat(
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureStreamingContracts.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureStreamingSources.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureResidencyPolicy.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureStreamingRegistry.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/TextureTransitionQueue.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/OpenGLTextureResidencyBackends.cs"),
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
