using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Data.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering;

internal sealed class GLTieredTextureResidencyBackend : ITextureResidencyBackend
{
    private const int MaxConcurrentStreamingDecodes = 2;
    private const int MaxRenderThreadApplyResidentDataPerFrame = 1;

    private static readonly uint[] ResidentCandidates =
    [
        1u,
        2u,
        4u,
        8u,
        16u,
        32u,
        64u,
        128u,
        256u,
        512u,
        1024u,
        2048u,
        4096u,
        8192u,
    ];

    private static readonly PriorityAsyncSemaphore DecodeGate = new(MaxConcurrentStreamingDecodes);

    private static int s_activeDecodeCount;
    private static int s_queuedDecodeCount;
    private static long s_uploadBytesFrameTicks = -1;
    private static long s_uploadBytesScheduledThisFrame;
    private static long s_applyResidentDataFrameTicks = -1;
    private static int s_applyResidentDataCountThisFrame;

    public string Name => nameof(GLTieredTextureResidencyBackend);
    public uint PreviewMaxDimension => XRTexture2D.ImportedPreviewMaxDimensionInternal;
    public bool SupportsSparseResidency => false;
    public int ActiveDecodeCount => Volatile.Read(ref s_activeDecodeCount);
    public int QueuedDecodeCount => Volatile.Read(ref s_queuedDecodeCount);
    public int ActiveGpuUploadCount => XRTexture2D.QueuedProgressiveUploadCount;
    public long UploadBytesScheduledThisFrame => GetUploadBytesScheduledThisFrame();

    public long EstimateCommittedBytes(uint sourceWidth, uint sourceHeight, uint residentMaxDimension, ESizedInternalFormat format = ESizedInternalFormat.Rgba8, int sparseNumLevels = 0, SparseTextureStreamingPageSelection pageSelection = default)
        => XRTexture2D.EstimateResidentBytes(sourceWidth, sourceHeight, residentMaxDimension, format);

    public uint GetNextLowerResidentSize(uint sourceMaxDimension, uint currentResidentSize)
    {
        uint minimumResidentSize = XRTexture2D.GetMinimumResidentSize(sourceMaxDimension);
        if (currentResidentSize <= minimumResidentSize)
            return minimumResidentSize;

        for (int index = ResidentCandidates.Length - 1; index >= 0; index--)
        {
            uint candidate = ResidentCandidates[index];
            if (candidate >= currentResidentSize)
                continue;
            if (sourceMaxDimension != 0 && candidate > sourceMaxDimension)
                continue;

            return Math.Max(candidate, minimumResidentSize);
        }

        return minimumResidentSize;
    }

    public EnumeratorJob SchedulePreviewLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low,
        long streamingGeneration = 0)
        => ScheduleResidentLoad(
            source,
            target,
            PreviewMaxDimension,
            includeMipChain: false,
            SparseTextureStreamingPageSelection.Full,
            onPrepared,
            onDeferred,
            onFinished,
            onError,
            onCanceled,
            shouldAcceptResult,
            cancellationToken,
            priority,
            onProgress,
            streamingGeneration);

    public EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        SparseTextureStreamingPageSelection pageSelection,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low,
        long streamingGeneration = 0)
        => ScheduleResidentLoad(
            source,
            target,
            maxResidentDimension,
            includeMipChain,
            pageSelection,
            onPrepared,
            onDeferred,
            onFinished,
            onError,
            onCanceled,
            shouldAcceptResult,
            cancellationToken,
            priority,
            onProgress: null,
            streamingGeneration);

    private static EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        SparseTextureStreamingPageSelection pageSelection,
        Action<TextureStreamingResidentData>? onPrepared,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred,
        Action<XRTexture2D>? onFinished,
        Action<Exception>? onError,
        Action? onCanceled,
        Func<bool>? shouldAcceptResult,
        CancellationToken cancellationToken,
        JobPriority priority,
        Action<float>? onProgress,
        long streamingGeneration)
    {
        Interlocked.Increment(ref s_queuedDecodeCount);

        void ReportCanceled(string phase)
        {
            TextureRuntimeDiagnostics.LogTransitionCanceled(
                RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
                target.Name,
                source.SourcePath,
                maxResidentDimension,
                nameof(GLTieredTextureResidencyBackend),
                phase);
            onCanceled?.Invoke();
        }

        Task loadTask = Task.Run(async () =>
        {
            bool decodeActivated = false;
            string cancellationPhase = "before decode/cache read";
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ReportCanceled(cancellationPhase);
                    return;
                }

                await DecodeGate.WaitAsync(priority, cancellationToken).ConfigureAwait(false);
                Interlocked.Decrement(ref s_queuedDecodeCount);
                Interlocked.Increment(ref s_activeDecodeCount);
                decodeActivated = true;

                TextureStreamingResidentData residentData;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    cancellationPhase = "during decode/cache read";
                    if (TextureStreamingResidentDataReuseCache.TryGet(source, maxResidentDimension, includeMipChain, out residentData))
                    {
                        TextureRuntimeDiagnostics.LogResidentDataReused(
                            RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
                            source.SourcePath,
                            maxResidentDimension,
                            includeMipChain,
                            residentData.ResidentMaxDimension,
                            residentData.Mipmaps.Length,
                            nameof(GLTieredTextureResidencyBackend));
                    }
                    else
                    {
                        residentData = source.LoadResidentData(maxResidentDimension, includeMipChain, cancellationToken);
                        TextureStreamingResidentDataReuseCache.Store(source, maxResidentDimension, includeMipChain, residentData);
                    }
                }
                finally
                {
                    if (decodeActivated)
                    {
                        Interlocked.Decrement(ref s_activeDecodeCount);
                        DecodeGate.Release();
                    }
                }

                void ApplyResidentDataOnRenderThreadCore()
                {
                    if (cancellationToken.IsCancellationRequested
                        || (shouldAcceptResult is not null && !shouldAcceptResult()))
                    {
                        ReportCanceled(cancellationPhase);
                        return;
                    }

                    cancellationPhase = "after CPU prep";
                    onPrepared?.Invoke(residentData);

                    if (cancellationToken.IsCancellationRequested
                        || (shouldAcceptResult is not null && !shouldAcceptResult()))
                    {
                        ReportCanceled(cancellationPhase);
                        return;
                    }

                    cancellationPhase = "during upload";
                    if (TryScheduleVulkanSynchronizedUpload(
                        target,
                        source,
                        residentData,
                        includeMipChain,
                        maxResidentDimension,
                        streamingGeneration,
                        priority,
                        shouldAcceptResult,
                        cancellationToken,
                        onFinished,
                        onError,
                        onCanceled,
                        onProgress,
                        ReportCanceled))
                    {
                        return;
                    }

                    XRTexture2D.ApplyResidentData(target, residentData, includeMipChain);
                    onProgress?.Invoke(0.5f);

                    if (cancellationToken.IsCancellationRequested
                        || (shouldAcceptResult is not null && !shouldAcceptResult()))
                    {
                        ReportCanceled(cancellationPhase);
                        return;
                    }

                    long uploadBytes = XRTexture2D.CalculateResidentUploadBytes(residentData);
                    RecordUploadBytes(uploadBytes);
                    XRTexture2D.ScheduleGpuUploadInternal(target, cancellationToken, () =>
                    {
                        if (cancellationToken.IsCancellationRequested
                            || (shouldAcceptResult is not null && !shouldAcceptResult()))
                        {
                            ReportCanceled("during finalization");
                            return;
                        }

                        onProgress?.Invoke(1.0f);
                        onFinished?.Invoke(target);
                    }, ImportedTextureStreamingManager.ResolveUploadPriorityClass(priority));
                }

                bool ApplyResidentDataOnRenderThreadBudgeted()
                {
                    if (!TryEnterRenderThreadApplyBudget())
                        return false;

                    ApplyResidentDataOnRenderThreadCore();
                    return true;
                }

                if (RuntimeRenderingHostServices.Current.IsRenderThread && TryEnterRenderThreadApplyBudget())
                    ApplyResidentDataOnRenderThreadCore();
                else
                    RuntimeRenderingHostServices.Current.EnqueueRenderThreadCoroutine(
                        ApplyResidentDataOnRenderThreadBudgeted,
                        $"TextureStreaming.ApplyResidentData[{target.Name}]",
                        RenderThreadJobKind.TextureUpload);
            }
            catch (OperationCanceledException)
            {
                ReportCanceled(cancellationPhase);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
            finally
            {
                if (!decodeActivated)
                {
                    int remaining = Interlocked.Decrement(ref s_queuedDecodeCount);
                    if (remaining < 0)
                        Interlocked.Exchange(ref s_queuedDecodeCount, 0);
                }
            }
        });

        IEnumerable Routine()
        {
            yield return loadTask;
        }

        return RuntimeRenderingHostServices.Current.ScheduleEnumeratorJob(
            Routine,
            priority,
            cancellationToken: CancellationToken.None);
    }

    private static bool TryScheduleVulkanSynchronizedUpload(
        XRTexture2D target,
        ITextureStreamingSource source,
        TextureStreamingResidentData residentData,
        bool includeMipChain,
        uint maxResidentDimension,
        long streamingGeneration,
        JobPriority priority,
        Func<bool>? shouldAcceptResult,
        CancellationToken cancellationToken,
        Action<XRTexture2D>? onFinished,
        Action<Exception>? onError,
        Action? onCanceled,
        Action<float>? onProgress,
        Action<string> reportCanceled)
    {
        if (RuntimeRenderingHostServices.Current.CurrentRenderBackend != RuntimeGraphicsApiKind.Vulkan)
            return false;

        if ((RuntimeRenderingHostServices.Current.CurrentRenderer ?? AbstractRenderer.Current) is not VulkanRenderer renderer)
        {
            onError?.Invoke(new InvalidOperationException("Vulkan imported texture upload service could not resolve the active Vulkan renderer."));
            return true;
        }

        if (!VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable)
        {
            onError?.Invoke(new InvalidOperationException("Vulkan synchronized imported texture upload service is not available."));
            return true;
        }

        if (cancellationToken.IsCancellationRequested
            || (shouldAcceptResult is not null && !shouldAcceptResult()))
        {
            reportCanceled("before synchronized Vulkan upload");
            return true;
        }

        long uploadBytes = XRTexture2D.CalculateResidentUploadBytes(residentData);
        RecordUploadBytes(uploadBytes);
        onProgress?.Invoke(0.5f);

        bool queued = renderer.TryScheduleImportedTextureResidencyTransition(
            target,
            residentData,
            includeMipChain,
            maxResidentDimension,
            streamingGeneration,
            ImportedTextureStreamingManager.ResolveUploadPriorityClass(priority),
            shouldAcceptResult,
            tex =>
            {
                onProgress?.Invoke(1.0f);
                onFinished?.Invoke(tex);
            },
            onCanceled,
            onError,
            cancellationToken);

        if (!queued)
            reportCanceled("synchronized Vulkan upload queue rejected request");

        _ = source;
        return true;
    }

    private static bool TryEnterRenderThreadApplyBudget()
    {
        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Interlocked.Exchange(ref s_applyResidentDataFrameTicks, currentFrame);
        if (previousFrame != currentFrame)
            Interlocked.Exchange(ref s_applyResidentDataCountThisFrame, 0);

        while (true)
        {
            int currentCount = Volatile.Read(ref s_applyResidentDataCountThisFrame);
            if (currentCount >= MaxRenderThreadApplyResidentDataPerFrame)
                return false;

            if (Interlocked.CompareExchange(ref s_applyResidentDataCountThisFrame, currentCount + 1, currentCount) == currentCount)
                return true;
        }
    }

    private static void RecordUploadBytes(long bytes)
    {
        if (bytes <= 0)
            return;

        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Interlocked.Exchange(ref s_uploadBytesFrameTicks, currentFrame);
        if (previousFrame != currentFrame)
            Interlocked.Exchange(ref s_uploadBytesScheduledThisFrame, 0L);

        _ = Interlocked.Add(ref s_uploadBytesScheduledThisFrame, bytes);
    }

    private static long GetUploadBytesScheduledThisFrame()
    {
        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Volatile.Read(ref s_uploadBytesFrameTicks);
        if (previousFrame != currentFrame)
            return 0L;

        return Volatile.Read(ref s_uploadBytesScheduledThisFrame);
    }
}

internal sealed class GLSparseTextureResidencyBackend : ITextureResidencyBackend
{
    private const int MaxConcurrentStreamingDecodes = 2;
    private const int MaxQueuedDecodeBacklog = 8;
    private const int MaxSharedUploadsInFlight = 1;
    private const int MaxSparseTransitionSchedulesPerFrame = 1;
    private static readonly uint[] ResidentCandidates =
    [
        1u,
        2u,
        4u,
        8u,
        16u,
        32u,
        64u,
        128u,
        256u,
        512u,
        1024u,
        2048u,
        4096u,
        8192u,
    ];

    private static readonly PriorityAsyncSemaphore DecodeGate = new(MaxConcurrentStreamingDecodes);

    private static int s_activeDecodeCount;
    private static int s_queuedDecodeCount;
    private static int s_inFlightUploadCount;
    private static long s_uploadBytesFrameTicks = -1;
    private static long s_uploadBytesScheduledThisFrame;
    private static long s_sparseTransitionScheduleFrameTicks = -1;
    private static int s_sparseTransitionScheduleCountThisFrame;

    public string Name => nameof(GLSparseTextureResidencyBackend);
    public uint PreviewMaxDimension => XRTexture2D.ImportedPreviewMaxDimensionInternal;
    public bool SupportsSparseResidency => true;
    public int ActiveDecodeCount => Volatile.Read(ref s_activeDecodeCount);
    public int QueuedDecodeCount => Volatile.Read(ref s_queuedDecodeCount);
    public int ActiveGpuUploadCount => Volatile.Read(ref s_inFlightUploadCount);
    public long UploadBytesScheduledThisFrame => GetUploadBytesScheduledThisFrame();

    public long EstimateCommittedBytes(uint sourceWidth, uint sourceHeight, uint residentMaxDimension, ESizedInternalFormat format = ESizedInternalFormat.Rgba8, int sparseNumLevels = 0, SparseTextureStreamingPageSelection pageSelection = default)
    {
        if (residentMaxDimension == 0)
            return 0L;

        int logicalMipCount = XRTexture2D.GetLogicalMipCount(sourceWidth, sourceHeight);
        if (logicalMipCount <= 0)
            return 0L;

        int requestedBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(sourceWidth, sourceHeight, residentMaxDimension);
        SparseTextureStreamingSupport support = RuntimeRenderingHostServices.Current.GetSparseTextureStreamingSupport(format);
        return XRTexture2D.EstimateSparsePageSelectionBytes(
            sourceWidth,
            sourceHeight,
            requestedBaseMipLevel,
            logicalMipCount,
            sparseNumLevels,
            support,
            pageSelection,
            format);
    }

    public uint GetNextLowerResidentSize(uint sourceMaxDimension, uint currentResidentSize)
    {
        uint minimumResidentSize = XRTexture2D.GetMinimumResidentSize(sourceMaxDimension);
        if (currentResidentSize <= minimumResidentSize)
            return minimumResidentSize;

        for (int index = ResidentCandidates.Length - 1; index >= 0; index--)
        {
            uint candidate = ResidentCandidates[index];
            if (candidate >= currentResidentSize)
                continue;
            if (sourceMaxDimension != 0 && candidate > sourceMaxDimension)
                continue;

            return Math.Max(candidate, minimumResidentSize);
        }

        return minimumResidentSize;
    }

    public EnumeratorJob SchedulePreviewLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low,
        long streamingGeneration = 0)
        => ScheduleResidentLoad(
            source,
            target,
            PreviewMaxDimension,
            includeMipChain: true,
            SparseTextureStreamingPageSelection.Full,
            onPrepared,
            onDeferred,
            onFinished,
            onError,
            onCanceled,
            shouldAcceptResult,
            cancellationToken,
            priority,
            onProgress,
            streamingGeneration);

    public EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        SparseTextureStreamingPageSelection pageSelection,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low,
        long streamingGeneration = 0)
        => ScheduleResidentLoad(
            source,
            target,
            maxResidentDimension,
            includeMipChain: true,
            pageSelection,
            onPrepared,
            onDeferred,
            onFinished,
            onError,
            onCanceled,
            shouldAcceptResult,
            cancellationToken,
            priority,
            onProgress: null,
            streamingGeneration);

    private static EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        SparseTextureStreamingPageSelection pageSelection,
        Action<TextureStreamingResidentData>? onPrepared,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred,
        Action<XRTexture2D>? onFinished,
        Action<Exception>? onError,
        Action? onCanceled,
        Func<bool>? shouldAcceptResult,
        CancellationToken cancellationToken,
        JobPriority priority,
        Action<float>? onProgress,
        long streamingGeneration)
    {
        Interlocked.Increment(ref s_queuedDecodeCount);

        void ReportCanceled(string phase)
        {
            TextureRuntimeDiagnostics.LogTransitionCanceled(
                RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
                target.Name,
                source.SourcePath,
                maxResidentDimension,
                nameof(GLSparseTextureResidencyBackend),
                phase);
            onCanceled?.Invoke();
        }

        Task loadTask = Task.Run(async () =>
        {
            bool decodeActivated = false;
            string cancellationPhase = "before decode/cache read";
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ReportCanceled(cancellationPhase);
                    return;
                }

                if (Volatile.Read(ref s_queuedDecodeCount) > MaxQueuedDecodeBacklog)
                {
                    ReportCanceled("decode backlog limit");
                    return;
                }

                await DecodeGate.WaitAsync(priority, cancellationToken).ConfigureAwait(false);
                Interlocked.Decrement(ref s_queuedDecodeCount);
                Interlocked.Increment(ref s_activeDecodeCount);
                decodeActivated = true;

                TextureStreamingResidentData residentData;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    cancellationPhase = "during decode/cache read";
                    const bool cacheIncludeMipChain = true;
                    if (TextureStreamingResidentDataReuseCache.TryGet(source, maxResidentDimension, cacheIncludeMipChain, out residentData))
                    {
                        TextureRuntimeDiagnostics.LogResidentDataReused(
                            RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
                            source.SourcePath,
                            maxResidentDimension,
                            cacheIncludeMipChain,
                            residentData.ResidentMaxDimension,
                            residentData.Mipmaps.Length,
                            nameof(GLSparseTextureResidencyBackend));
                    }
                    else
                    {
                        residentData = source.LoadResidentData(maxResidentDimension, includeMipChain: true, cancellationToken);
                        TextureStreamingResidentDataReuseCache.Store(source, maxResidentDimension, cacheIncludeMipChain, residentData);
                    }
                }
                finally
                {
                    if (decodeActivated)
                    {
                        Interlocked.Decrement(ref s_activeDecodeCount);
                        DecodeGate.Release();
                    }
                }

                void ContinueOnRenderThreadCore()
                {
                    if (cancellationToken.IsCancellationRequested
                        || (shouldAcceptResult is not null && !shouldAcceptResult()))
                    {
                        ReportCanceled(cancellationPhase);
                        return;
                    }

                    cancellationPhase = "after CPU prep";
                    onPrepared?.Invoke(residentData);
                    onProgress?.Invoke(0.5f);

                    if (cancellationToken.IsCancellationRequested
                        || (shouldAcceptResult is not null && !shouldAcceptResult()))
                    {
                        ReportCanceled(cancellationPhase);
                        return;
                    }

                    cancellationPhase = "during upload";
                    long uploadBytes = XRTexture2D.CalculateResidentUploadBytes(residentData);

                    SparseTextureStreamingTransitionRequest transitionRequest = BuildSparseTransitionRequest(residentData, pageSelection);

                    void CompleteSparseTransition(SparseTextureStreamingTransitionResult transitionResult)
                    {
                        if (cancellationToken.IsCancellationRequested
                            || (shouldAcceptResult is not null && !shouldAcceptResult()))
                        {
                            ReportCanceled("during finalization");
                            return;
                        }

                        if (transitionResult.ExposureDeferred)
                        {
                            onDeferred?.Invoke(transitionRequest, transitionResult);
                            return;
                        }

                        if (!transitionResult.Applied || !transitionResult.UsedSparseResidency)
                        {
                            target.ClearSparseTextureStreamingState();
                            XRTexture2D.ApplyResidentData(target, residentData, includeMipChain: true);
                            XRTexture2D.ScheduleGpuUploadInternal(target, cancellationToken, () =>
                            {
                                if (cancellationToken.IsCancellationRequested
                                    || (shouldAcceptResult is not null && !shouldAcceptResult()))
                                {
                                    ReportCanceled("during finalization");
                                    return;
                                }

                                onProgress?.Invoke(1.0f);
                                onFinished?.Invoke(target);
                            }, ImportedTextureStreamingManager.ResolveUploadPriorityClass(priority));
                            return;
                        }

                        onProgress?.Invoke(1.0f);
                        onFinished?.Invoke(target);
                    }

                    // A promotion (requesting a finer mip than what is currently resident) can use
                    // the async upload path only when the OpenGL wrapper already has a complete,
                    // published sparse state. Initial loads and storage recreates fall back to the
                    // synchronous render-thread path so sampling never sees an uncommitted sparse
                    // texture while the shared-context upload is in flight.
                    bool allowAsyncUpload = transitionRequest.RequestedBaseMipLevel < target.SparseTextureStreamingResidentBaseMipLevel && TryEnterSharedUpload();
                    if (allowAsyncUpload)
                    {
                        RecordUploadBytes(uploadBytes);
                        bool asyncScheduled = RuntimeRenderingHostServices.Current.TryScheduleSparseTextureStreamingTransitionAsync(
                            target,
                            transitionRequest,
                            cancellationToken,
                            transitionResult =>
                            {
                                ExitSharedUpload();
                                if (RuntimeRenderingHostServices.Current.IsRenderThread)
                                    CompleteSparseTransition(transitionResult);
                                else
                                    RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(
                                        () => CompleteSparseTransition(transitionResult),
                                        $"TextureStreaming.CompleteSparseTransition[{target.Name}]",
                                        RenderThreadJobKind.TextureUpload);
                            },
                            ex =>
                            {
                                ExitSharedUpload();
                                onError?.Invoke(ex);
                            });
                        if (asyncScheduled)
                            return;

                        ExitSharedUpload();
                    }

                    // Sync render-thread path: increment s_inFlightUploadCount so that
                    // anyGpuUploadActive is true while this thread is blocked, preventing
                    // the stuck-transition detector from firing prematurely.
                    Interlocked.Increment(ref s_inFlightUploadCount);
                    try
                    {
                        RecordUploadBytes(uploadBytes);
                        SparseTextureStreamingTransitionResult transitionResult = target.ApplySparseTextureStreamingTransition(transitionRequest);
                        CompleteSparseTransition(transitionResult);
                    }
                    finally
                    {
                        ExitSharedUpload();
                    }
                }

                bool ContinueOnRenderThreadBudgeted()
                {
                    if (!TryEnterSparseTransitionScheduleBudget())
                        return false;

                    ContinueOnRenderThreadCore();
                    return true;
                }

                if (RuntimeRenderingHostServices.Current.IsRenderThread && TryEnterSparseTransitionScheduleBudget())
                    ContinueOnRenderThreadCore();
                else
                    RuntimeRenderingHostServices.Current.EnqueueRenderThreadCoroutine(
                        ContinueOnRenderThreadBudgeted,
                        $"TextureStreaming.ScheduleSparseTransition[{target.Name}]",
                        RenderThreadJobKind.TextureUpload);
            }
            catch (OperationCanceledException)
            {
                ReportCanceled(cancellationPhase);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
            finally
            {
                if (!decodeActivated)
                {
                    int remaining = Interlocked.Decrement(ref s_queuedDecodeCount);
                    if (remaining < 0)
                        Interlocked.Exchange(ref s_queuedDecodeCount, 0);
                }
            }
        });

        IEnumerable Routine()
        {
            yield return loadTask;
        }

        return RuntimeRenderingHostServices.Current.ScheduleEnumeratorJob(
            Routine,
            priority,
            cancellationToken: CancellationToken.None);
    }

    private static SparseTextureStreamingTransitionRequest BuildSparseTransitionRequest(TextureStreamingResidentData residentData, SparseTextureStreamingPageSelection pageSelection)
    {
        int requestedBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(
            residentData.SourceWidth,
            residentData.SourceHeight,
            residentData.ResidentMaxDimension);
        int logicalMipCount = XRTexture2D.GetLogicalMipCount(residentData.SourceWidth, residentData.SourceHeight);

        return new SparseTextureStreamingTransitionRequest(
            residentData.SourceWidth,
            residentData.SourceHeight,
            ESizedInternalFormat.Rgba8,
            logicalMipCount,
            requestedBaseMipLevel,
            residentData.Mipmaps,
            pageSelection.Normalize());
    }

    private static bool TryEnterSharedUpload()
    {
        while (true)
        {
            int current = Volatile.Read(ref s_inFlightUploadCount);
            if (current >= MaxSharedUploadsInFlight)
                return false;

            if (Interlocked.CompareExchange(ref s_inFlightUploadCount, current + 1, current) == current)
                return true;
        }
    }

    private static bool TryEnterSparseTransitionScheduleBudget()
    {
        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Interlocked.Exchange(ref s_sparseTransitionScheduleFrameTicks, currentFrame);
        if (previousFrame != currentFrame)
            Interlocked.Exchange(ref s_sparseTransitionScheduleCountThisFrame, 0);

        while (true)
        {
            int currentCount = Volatile.Read(ref s_sparseTransitionScheduleCountThisFrame);
            if (currentCount >= MaxSparseTransitionSchedulesPerFrame)
                return false;

            if (Interlocked.CompareExchange(ref s_sparseTransitionScheduleCountThisFrame, currentCount + 1, currentCount) == currentCount)
                return true;
        }
    }

    private static void ExitSharedUpload()
    {
        int remaining = Interlocked.Decrement(ref s_inFlightUploadCount);
        if (remaining < 0)
            Interlocked.Exchange(ref s_inFlightUploadCount, 0);
    }

    private static void RecordUploadBytes(long bytes)
    {
        if (bytes <= 0)
            return;

        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Interlocked.Exchange(ref s_uploadBytesFrameTicks, currentFrame);
        if (previousFrame != currentFrame)
            Interlocked.Exchange(ref s_uploadBytesScheduledThisFrame, 0L);

        _ = Interlocked.Add(ref s_uploadBytesScheduledThisFrame, bytes);
    }

    private static long GetUploadBytesScheduledThisFrame()
    {
        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Volatile.Read(ref s_uploadBytesFrameTicks);
        if (previousFrame != currentFrame)
            return 0L;

        return Volatile.Read(ref s_uploadBytesScheduledThisFrame);
    }
}
