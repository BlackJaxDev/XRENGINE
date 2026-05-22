using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

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
        source.ShouldContain("RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(");
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
        string interfaceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderingHostServices.cs");
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
        assetManagerSource.ShouldContain("LogTextureCacheEvent(\"Texture.CacheStale\"");
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
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLRenderer.TextureStreamingCacheCook.cs");
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.TextureStreamingCacheCook.cs");
        string normalizedAssetManagerSource = assetManagerSource.Replace("\r\n", "\n");

        normalizedAssetManagerSource.ShouldContain("XRTexture2D.TryCreateTextureStreamingCacheAsset(\n                texture,");
        assetManagerSource.ShouldContain("TextureStreaming_v2_preview");

        payloadSource.ShouldContain("TryCreateTextureStreamingCacheAssetGpu");
        payloadSource.ShouldContain("RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(");
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
        string sparseSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.SparseStreaming.cs");
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
        string glTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");
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
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/OpenGLTextureResidencyBackends.cs"),
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
