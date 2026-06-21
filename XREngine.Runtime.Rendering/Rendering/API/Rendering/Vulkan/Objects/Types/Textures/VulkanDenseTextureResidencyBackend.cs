using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Data.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering;

internal sealed class VulkanDenseTextureResidencyBackend : ITextureResidencyBackend
{
    private const int MaxConcurrentStreamingDecodes = 2;
    private const int MaxRenderThreadSchedulePerFrame = 1;

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
    private static int s_activeGpuUploadCount;
    private static long s_uploadBytesFrameTicks = -1;
    private static long s_uploadBytesScheduledThisFrame;
    private static long s_scheduleFrameTicks = -1;
    private static int s_scheduleCountThisFrame;

    public string Name => nameof(VulkanDenseTextureResidencyBackend);
    public uint PreviewMaxDimension => XRTexture2D.ImportedPreviewMaxDimensionInternal;
    public bool SupportsSparseResidency => false;
    public int ActiveDecodeCount => Volatile.Read(ref s_activeDecodeCount);
    public int QueuedDecodeCount => Volatile.Read(ref s_queuedDecodeCount);
    public int ActiveGpuUploadCount => Volatile.Read(ref s_activeGpuUploadCount);
    public long UploadBytesScheduledThisFrame => GetUploadBytesScheduledThisFrame();

    public long EstimateCommittedBytes(
        uint sourceWidth,
        uint sourceHeight,
        uint residentMaxDimension,
        ESizedInternalFormat format = ESizedInternalFormat.Rgba8,
        int sparseNumLevels = 0,
        SparseTextureStreamingPageSelection pageSelection = default)
    {
        _ = sparseNumLevels;
        _ = pageSelection;
        return XRTexture2D.EstimateResidentBytes(sourceWidth, sourceHeight, residentMaxDimension, format);
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
        _ = pageSelection;
        _ = onDeferred;

        Interlocked.Increment(ref s_queuedDecodeCount);

        bool decodeDequeued = false;
        void MarkDecodeDequeued()
        {
            if (decodeDequeued)
                return;

            decodeDequeued = true;
            int remaining = Interlocked.Decrement(ref s_queuedDecodeCount);
            if (remaining < 0)
                Interlocked.Exchange(ref s_queuedDecodeCount, 0);
        }

        void ReportCanceled(string phase)
        {
            TextureRuntimeDiagnostics.LogTransitionCanceled(
                RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
                target.Name,
                source.SourcePath,
                maxResidentDimension,
                nameof(VulkanDenseTextureResidencyBackend),
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
                MarkDecodeDequeued();
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
                            nameof(VulkanDenseTextureResidencyBackend));
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

                void ScheduleUploadOnRenderThread()
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

                    cancellationPhase = "during synchronized Vulkan upload";
                    ScheduleVulkanSynchronizedUpload(
                        target,
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
                        ReportCanceled);
                }

                bool ScheduleUploadBudgeted()
                {
                    if (!TryEnterRenderThreadScheduleBudget())
                        return false;

                    VulkanTextureUploadService.RecordResidentDataPackageConsumed();
                    ScheduleUploadOnRenderThread();
                    return true;
                }

                if (RuntimeRenderingHostServices.Current.IsRenderThread && TryEnterRenderThreadScheduleBudget())
                    ScheduleUploadOnRenderThread();
                else
                {
                    VulkanTextureUploadService.RecordResidentDataPackageQueued();
                    RuntimeRenderingHostServices.Current.EnqueueRenderThreadCoroutine(
                        ScheduleUploadBudgeted,
                        $"TextureStreaming.ScheduleVulkanUpload[{target.Name}]",
                        RenderThreadJobKind.TextureUpload);
                }
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
                MarkDecodeDequeued();
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

    private static void ScheduleVulkanSynchronizedUpload(
        XRTexture2D target,
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
        Debug.VulkanEvery(
            $"Vulkan.Compat.ImportedTextureStreaming.{target.GetHashCode()}",
            TimeSpan.FromSeconds(10),
            "[Vulkan Compat] Imported texture streaming for '{0}' is using dense resident mip uploads. Preferred Vulkan path is the synchronized VulkanTextureUploadService; true Vk sparse image page binding is not implemented yet.",
            target.Name ?? target.GetDescribingName());

        if (RuntimeRenderingHostServices.Current.CurrentRenderBackend != RuntimeGraphicsApiKind.Vulkan)
        {
            onError?.Invoke(new InvalidOperationException("Vulkan dense texture residency backend was selected while the active renderer is not Vulkan."));
            return;
        }

        if (AbstractRenderer.Current is not VulkanRenderer renderer)
        {
            onError?.Invoke(new InvalidOperationException("Vulkan imported texture upload service could not resolve the active Vulkan renderer."));
            return;
        }

        if (!VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable)
        {
            onError?.Invoke(new InvalidOperationException("Vulkan synchronized imported texture upload service is not available. Use VulkanTextureUploadService for imported-texture residency instead of GL-style texture mutation."));
            return;
        }

        if (cancellationToken.IsCancellationRequested
            || (shouldAcceptResult is not null && !shouldAcceptResult()))
        {
            reportCanceled("before synchronized Vulkan upload");
            return;
        }

        long uploadBytes = XRTexture2D.CalculateResidentUploadBytes(residentData);
        Interlocked.Increment(ref s_activeGpuUploadCount);

        void CompleteUploadCounter()
        {
            int remaining = Interlocked.Decrement(ref s_activeGpuUploadCount);
            if (remaining < 0)
                Interlocked.Exchange(ref s_activeGpuUploadCount, 0);
        }

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
                CompleteUploadCounter();
                onProgress?.Invoke(1.0f);
                onFinished?.Invoke(tex);
            },
            () =>
            {
                CompleteUploadCounter();
                onCanceled?.Invoke();
            },
            ex =>
            {
                CompleteUploadCounter();
                onError?.Invoke(ex);
            },
            cancellationToken);

        if (!queued)
        {
            CompleteUploadCounter();
            reportCanceled("synchronized Vulkan upload queue rejected request");
            return;
        }

        RecordUploadBytes(uploadBytes);
        onProgress?.Invoke(0.5f);
    }

    private static bool TryEnterRenderThreadScheduleBudget()
    {
        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Interlocked.Exchange(ref s_scheduleFrameTicks, currentFrame);
        if (previousFrame != currentFrame)
            Interlocked.Exchange(ref s_scheduleCountThisFrame, 0);

        while (true)
        {
            int currentCount = Volatile.Read(ref s_scheduleCountThisFrame);
            if (currentCount >= MaxRenderThreadSchedulePerFrame)
                return false;

            if (Interlocked.CompareExchange(ref s_scheduleCountThisFrame, currentCount + 1, currentCount) == currentCount)
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
