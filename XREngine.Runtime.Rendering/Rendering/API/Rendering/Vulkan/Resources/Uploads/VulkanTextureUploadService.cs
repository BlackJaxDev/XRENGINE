using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

internal enum VulkanTextureUploadGenerationState
{
    Decoded,
    PrepQueued,
    PrepDeferred,
    PrepRunning,
    PrepReady,
    UploadQueued,
    UploadRecording,
    GpuUploadPending,
    TransferSubmitted,
    TransferComplete,
    Uploaded,
    DescriptorPublishPending,
    Published,
    Retired,
    Canceled,
    Failed,
}

internal enum VulkanImportedTextureUploadPreparationStep
{
    CreateImage,
    CreateImageView,
    CreateSampler,
    CreateNextStagingMip,
    Complete,
}

internal enum VulkanImportedTextureUploadResultState
{
    Success,
    Canceled,
    Failed,
}

internal readonly record struct VulkanImportedTextureUploadMipRange(
    int FirstMipLevel,
    int MipLevelCount,
    uint ExpectedWidth,
    uint ExpectedHeight);

internal readonly record struct VulkanImportedTextureUploadRequest(
    WeakReference<XRTexture2D> Texture,
    string? TextureName,
    string? SourcePath,
    uint TargetResidentMaxDimension,
    VulkanImportedTextureUploadMipRange MipRange,
    ESizedInternalFormat Format,
    string? ColorSpace,
    long EstimatedBytes,
    long StreamingGeneration,
    TextureUploadPriorityClass PriorityClass,
    CancellationToken CancellationToken)
{
    public bool TryGetTexture(out XRTexture2D? texture)
        => Texture.TryGetTarget(out texture);

    public TextureUploadKind UploadKind
        => TargetResidentMaxDimension <= XRTexture2D.ImportedPreviewMaxDimensionInternal
            ? TextureUploadKind.Preview
            : TextureUploadKind.Promotion;
}

internal readonly record struct VulkanImportedTextureUploadResult(
    long SourceGeneration,
    VulkanImportedTextureUploadResultState State,
    Image Image,
    DeviceMemory Memory,
    ImageView ImageView,
    Sampler Sampler,
    ImageLayout FinalLayout,
    VulkanImportedTextureUploadMipRange ResidentMipRange,
    uint ResidentMaxDimension,
    long CommittedBytes,
    ulong DescriptorPublicationToken,
    string? FailureReason)
{
    public static VulkanImportedTextureUploadResult Canceled(
        long sourceGeneration,
        VulkanImportedTextureUploadMipRange mipRange,
        string reason)
        => new(
            sourceGeneration,
            VulkanImportedTextureUploadResultState.Canceled,
            default,
            default,
            default,
            default,
            ImageLayout.Undefined,
            mipRange,
            0u,
            0L,
            0UL,
            reason);

    public static VulkanImportedTextureUploadResult Failed(
        long sourceGeneration,
        VulkanImportedTextureUploadMipRange mipRange,
        string reason)
        => new(
            sourceGeneration,
            VulkanImportedTextureUploadResultState.Failed,
            default,
            default,
            default,
            default,
            ImageLayout.Undefined,
            mipRange,
            0u,
            0L,
            0UL,
            reason);
}

internal readonly record struct VulkanImportedTextureUploadStagingResource(
    Buffer Buffer,
    DeviceMemory Memory,
    BufferImageCopy CopyRegion,
    ulong SizeBytes);

internal sealed class VulkanImportedTextureUploadPreparation(
    VulkanImportedTextureUploadRequest request,
    VulkanRenderer.VkTexture2D texture,
    TextureStreamingResidentData residentData,
    bool includeMipChain,
    ulong publicationToken,
    Func<bool>? shouldAcceptResult,
    Action<XRTexture2D>? onFinished,
    Action? onCanceled,
    Action<Exception>? onError,
    Format format,
    ImageAspectFlags aspectMask,
    Extent3D extent,
    uint mipLevels,
    uint arrayLayers,
    string debugName)
{
    public VulkanImportedTextureUploadRequest Request { get; } = request;
    public VulkanRenderer.VkTexture2D Texture { get; } = texture;
    public TextureStreamingResidentData ResidentData { get; } = residentData;
    public bool IncludeMipChain { get; } = includeMipChain;
    public ulong PublicationToken { get; } = publicationToken;
    public Func<bool>? ShouldAcceptResult { get; } = shouldAcceptResult;
    public Action<XRTexture2D>? OnFinished { get; } = onFinished;
    public Action? OnCanceled { get; } = onCanceled;
    public Action<Exception>? OnError { get; } = onError;
    public Format Format { get; } = format;
    public ImageAspectFlags AspectMask { get; } = aspectMask;
    public Extent3D Extent { get; } = extent;
    public uint MipLevels { get; } = mipLevels;
    public uint ArrayLayers { get; } = arrayLayers;
    public string DebugName { get; } = debugName;
    public List<VulkanImportedTextureUploadStagingResource> StagingResources { get; } = new List<VulkanImportedTextureUploadStagingResource>(Math.Max(residentData.Mipmaps.Length, 1));
    public long PrepStartTimestamp { get; } = TextureRuntimeDiagnostics.StartTiming();
    public VulkanImportedTextureUploadPreparationStep Step { get; set; } = VulkanImportedTextureUploadPreparationStep.CreateImage;
    public int NextMipLevel { get; set; }
    public Image Image;
    public DeviceMemory Memory;
    public ImageView ImageView;
    public Sampler Sampler;
    public long CommittedBytes;

    public bool ShouldAccept()
        => !Request.CancellationToken.IsCancellationRequested
            && (ShouldAcceptResult is null || ShouldAcceptResult());
}

internal sealed class VulkanImportedTextureUploadWorkerResult(
    VulkanImportedTexturePendingUpload? pendingUpload,
    string? failureReason,
    bool canceled,
    double prepMilliseconds,
    Exception? exception)
{
    public VulkanImportedTexturePendingUpload? PendingUpload { get; } = pendingUpload;
    public string? FailureReason { get; } = failureReason;
    public bool Canceled { get; } = canceled;
    public double PrepMilliseconds { get; } = prepMilliseconds;
    public Exception? Exception { get; } = exception;
}

internal enum VulkanImportedTextureUploadPrepResult
{
    Completed,
    Deferred,
    Canceled,
    Failed,
}

internal sealed class VulkanSubmittedImportedTextureUpload(
    VulkanImportedTexturePendingUpload upload,
    CommandBuffer commandBuffer,
    CommandPool commandPool,
    Fence fence,
    bool requiresGraphicsAcquire,
    uint transferQueueFamily,
    uint graphicsQueueFamily,
    long submitTimestamp,
    long bytesInFlight)
{
    public VulkanImportedTexturePendingUpload Upload { get; } = upload;
    public CommandBuffer CommandBuffer { get; } = commandBuffer;
    public CommandPool CommandPool { get; } = commandPool;
    public Fence Fence { get; } = fence;
    public bool RequiresGraphicsAcquire { get; } = requiresGraphicsAcquire;
    public uint TransferQueueFamily { get; } = transferQueueFamily;
    public uint GraphicsQueueFamily { get; } = graphicsQueueFamily;
    public long SubmitTimestamp { get; } = submitTimestamp;
    public long BytesInFlight { get; } = bytesInFlight;
}

internal sealed class VulkanImportedTexturePendingUpload(
    VulkanImportedTextureUploadRequest request,
    VulkanRenderer.VkTexture2D texture,
    Image image,
    DeviceMemory memory,
    ImageView imageView,
    Sampler sampler,
    Format format,
    ImageAspectFlags aspectMask,
    Extent3D extent,
    uint mipLevels,
    long committedBytes,
    ulong publicationToken,
    VulkanImportedTextureUploadStagingResource[] stagingResources,
    Func<bool>? shouldAcceptResult,
    Action<XRTexture2D>? onFinished,
    Action? onCanceled,
    Action<Exception>? onError)
{
    public VulkanImportedTextureUploadRequest Request { get; } = request;
    public VulkanRenderer.VkTexture2D Texture { get; } = texture;
    public Image Image { get; private set; } = image;
    public DeviceMemory Memory { get; private set; } = memory;
    public ImageView ImageView { get; private set; } = imageView;
    public Sampler Sampler { get; private set; } = sampler;
    public Format Format { get; } = format;
    public ImageAspectFlags AspectMask { get; } = aspectMask;
    public Extent3D Extent { get; } = extent;
    public uint MipLevels { get; } = mipLevels;
    public long CommittedBytes { get; } = committedBytes;
    public ulong PublicationToken { get; } = publicationToken;
    public VulkanImportedTextureUploadStagingResource[] StagingResources { get; } = stagingResources;
    public Func<bool>? ShouldAcceptResult { get; } = shouldAcceptResult;
    public Action<XRTexture2D>? OnFinished { get; } = onFinished;
    public Action? OnCanceled { get; } = onCanceled;
    public Action<Exception>? OnError { get; } = onError;
    public long PreparedTimestamp { get; } = TextureRuntimeDiagnostics.StartTiming();
    public long RecordTimestamp { get; private set; }
    public long PublicationTimestamp { get; private set; }

    public bool TryGetTexture(out XRTexture2D? texture)
        => Request.TryGetTexture(out texture);

    public bool ShouldPublish()
        => !Request.CancellationToken.IsCancellationRequested
            && (ShouldAcceptResult is null || ShouldAcceptResult());

    public void MarkRecordStarted()
        => RecordTimestamp = TextureRuntimeDiagnostics.StartTiming();

    public void MarkPublished()
        => PublicationTimestamp = TextureRuntimeDiagnostics.StartTiming();

    public void DetachPublishedImageHandles()
    {
        Image = default;
        Memory = default;
        ImageView = default;
        Sampler = default;
    }
}

/// <summary>
/// Synchronized Vulkan imported texture upload service. The service prepares
/// staging/new image resources on the render thread, then publishes them through
/// a frame-timeline operation so descriptor swaps are ordered with the copy.
/// </summary>
internal sealed class VulkanTextureUploadService
{
    public const string EnableSynchronizedImportedTextureUploadsEnvVar = XREngineEnvironmentVariables.VulkanTextureUploadService;
    private const int MaxPreparedUploadsPerDrain = 1;

    private static int s_synchronizedImportedTextureStreamingAvailable = 1;
    private long _nextDescriptorPublicationToken;
    private long _nextQueuedUploadSequence;
    private readonly object _prepQueueSync = new();
    private readonly List<VulkanImportedTextureUploadJob> _pendingPrepJobs = [];
    private readonly List<VulkanSubmittedImportedTextureUpload> _pendingTransferUploads = [];
    private readonly object _transferQueueSync = new();
    private int _prepDrainScheduled;
    private int _transferDrainScheduled;
    private int _renderThreadPrepCompatLogged;
    private int _workerPrepCompatLogged;
    private int _transferQueueCompatLogged;
    private static int s_pendingResidentDataPackages;
    private static int s_pendingVulkanPrepPackages;
    private static int s_activePrepPackages;
    private static int s_pendingTransferSubmissions;
    private static int s_pendingDescriptorPublications;
    private static long s_transferQueueBytesInFlight;
    private static long s_canceledStaleUploads;
    private static long s_failedUploads;
    private static double s_lastRenderThreadPrepMilliseconds;
    private static double s_lastWorkerPrepMilliseconds;
    private static double s_lastTransferWaitMilliseconds;
    private static double s_lastPublicationMilliseconds;

    public static bool IsSynchronizedImportedTextureStreamingAvailable
        => Volatile.Read(ref s_synchronizedImportedTextureStreamingAvailable) != 0;

    internal static void SetSynchronizedImportedTextureStreamingAvailable(bool available)
        => Volatile.Write(ref s_synchronizedImportedTextureStreamingAvailable, available ? 1 : 0);

    internal static void RecordResidentDataPackageQueued()
        => Interlocked.Increment(ref s_pendingResidentDataPackages);

    internal static void RecordResidentDataPackageConsumed()
    {
        int remaining = Interlocked.Decrement(ref s_pendingResidentDataPackages);
        if (remaining < 0)
            Interlocked.Exchange(ref s_pendingResidentDataPackages, 0);
    }

    public static void AppendProfilerSummary(StringBuilder builder)
    {
        builder.Append("VulkanTextureUploadPendingResidentDataPackages: ").Append(Volatile.Read(ref s_pendingResidentDataPackages)).AppendLine();
        builder.Append("VulkanTextureUploadPendingPrepPackages: ").Append(Volatile.Read(ref s_pendingVulkanPrepPackages)).AppendLine();
        builder.Append("VulkanTextureUploadActivePrepPackages: ").Append(Volatile.Read(ref s_activePrepPackages)).AppendLine();
        builder.Append("VulkanTextureUploadPendingTransfers: ").Append(Volatile.Read(ref s_pendingTransferSubmissions)).AppendLine();
        builder.Append("VulkanTextureUploadTransferBytesInFlight: ").Append(Volatile.Read(ref s_transferQueueBytesInFlight)).AppendLine();
        builder.Append("VulkanTextureUploadPendingDescriptorPublications: ").Append(Volatile.Read(ref s_pendingDescriptorPublications)).AppendLine();
        builder.Append("VulkanTextureUploadCanceledStale: ").Append(Volatile.Read(ref s_canceledStaleUploads)).AppendLine();
        builder.Append("VulkanTextureUploadFailed: ").Append(Volatile.Read(ref s_failedUploads)).AppendLine();
        builder.Append("VulkanTextureUploadRenderThreadPrepMs: ").Append(Volatile.Read(ref s_lastRenderThreadPrepMilliseconds).ToString("F3")).AppendLine();
        builder.Append("VulkanTextureUploadWorkerPrepMs: ").Append(Volatile.Read(ref s_lastWorkerPrepMilliseconds).ToString("F3")).AppendLine();
        builder.Append("VulkanTextureUploadTransferWaitMs: ").Append(Volatile.Read(ref s_lastTransferWaitMilliseconds).ToString("F3")).AppendLine();
        builder.Append("VulkanTextureUploadPublicationMs: ").Append(Volatile.Read(ref s_lastPublicationMilliseconds).ToString("F3")).AppendLine();
    }

    public static bool IsExplicitlyRequested()
        => string.Equals(
            Environment.GetEnvironmentVariable(EnableSynchronizedImportedTextureUploadsEnvVar),
            "1",
            StringComparison.OrdinalIgnoreCase);

    private sealed class VulkanImportedTextureUploadJob
    {
        public VulkanImportedTextureUploadJob(
            VulkanImportedTextureUploadRequest request,
            TextureStreamingResidentData residentData,
            bool includeMipChain,
            long sequence,
            Func<bool>? shouldAcceptResult,
            Action<XRTexture2D>? onFinished,
            Action? onCanceled,
            Action<Exception>? onError)
        {
            Request = request;
            ResidentData = residentData;
            IncludeMipChain = includeMipChain;
            Sequence = sequence;
            ShouldAcceptResult = shouldAcceptResult;
            OnFinished = onFinished;
            OnCanceled = onCanceled;
            OnError = onError;
            QueueTimestamp = Stopwatch.GetTimestamp();
        }

        public VulkanImportedTextureUploadRequest Request { get; }
        public TextureStreamingResidentData ResidentData { get; }
        public bool IncludeMipChain { get; }
        public long Sequence { get; }
        public long QueueTimestamp { get; }
        public Func<bool>? ShouldAcceptResult { get; }
        public Action<XRTexture2D>? OnFinished { get; }
        public Action? OnCanceled { get; }
        public Action<Exception>? OnError { get; }
        public VulkanRenderer.VkTexture2D? TextureWrapper { get; set; }
        public VulkanImportedTextureUploadPreparation? Preparation { get; set; }
        public Task<VulkanImportedTextureUploadWorkerResult>? WorkerPrepTask { get; set; }
        public long? PublicationToken { get; set; }

        public bool ShouldAccept()
            => !Request.CancellationToken.IsCancellationRequested
                && (ShouldAcceptResult is null || ShouldAcceptResult());

        public double QueueWaitMilliseconds
            => TextureRuntimeDiagnostics.ElapsedMilliseconds(QueueTimestamp);
    }

    public bool ShouldAcceptResult(
        in VulkanImportedTextureUploadRequest request,
        long currentStreamingGeneration)
        => !request.CancellationToken.IsCancellationRequested
            && request.StreamingGeneration == currentStreamingGeneration;

    public VulkanImportedTextureUploadResult RejectStaleOrCanceledResult(
        in VulkanImportedTextureUploadRequest request,
        long currentStreamingGeneration)
    {
        string reason = request.CancellationToken.IsCancellationRequested
            ? "request cancellation token is canceled"
            : $"stale generation request={request.StreamingGeneration} current={currentStreamingGeneration}";

        TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadRejected(
            RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
            request.TextureName,
            request.SourcePath,
            request.StreamingGeneration,
            currentStreamingGeneration,
            reason);

        return VulkanImportedTextureUploadResult.Canceled(
            request.StreamingGeneration,
            request.MipRange,
            reason);
    }

    public ulong AllocateDescriptorPublicationToken()
        => unchecked((ulong)Interlocked.Increment(ref _nextDescriptorPublicationToken));

    public void RecordState(
        in VulkanImportedTextureUploadRequest request,
        VulkanTextureUploadGenerationState state,
        string? detail = null)
    {
        TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadState(
            RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
            request.TextureName,
            request.SourcePath,
            request.StreamingGeneration,
            request.TargetResidentMaxDimension,
            request.EstimatedBytes,
            request.PriorityClass,
            state,
            detail);
    }

    public bool TryQueueImportedTextureUpload(
        in VulkanImportedTextureUploadRequest request,
        long currentStreamingGeneration,
        out VulkanImportedTextureUploadResult immediateResult)
    {
        if (!ShouldAcceptResult(request, currentStreamingGeneration))
        {
            immediateResult = RejectStaleOrCanceledResult(request, currentStreamingGeneration);
            return false;
        }

        RecordState(request, VulkanTextureUploadGenerationState.UploadQueued, "service contract accepted request");
        immediateResult = default;
        return true;
    }

    public bool TryScheduleImportedTextureUpload(
        VulkanRenderer renderer,
        XRTexture2D texture,
        TextureStreamingResidentData residentData,
        bool includeMipChain,
        uint targetResidentMaxDimension,
        long streamingGeneration,
        TextureUploadPriorityClass priorityClass,
        Func<bool>? shouldAcceptResult,
        Action<XRTexture2D>? onFinished,
        Action? onCanceled,
        Action<Exception>? onError,
        CancellationToken cancellationToken)
    {
        if (renderer.IsDeviceLost)
        {
            onCanceled?.Invoke();
            return false;
        }

        long estimatedBytes = XRTexture2D.CalculateResidentUploadBytes(residentData);
        VulkanImportedTextureUploadRequest request = new(
            new WeakReference<XRTexture2D>(texture),
            texture.Name,
            texture.FilePath,
            targetResidentMaxDimension,
            new VulkanImportedTextureUploadMipRange(
                0,
                residentData.Mipmaps.Length,
                residentData.Mipmaps.Length > 0 ? residentData.Mipmaps[0].Width : 0u,
                residentData.Mipmaps.Length > 0 ? residentData.Mipmaps[0].Height : 0u),
            ESizedInternalFormat.Rgba8,
            null,
            estimatedBytes,
            streamingGeneration,
            priorityClass,
            cancellationToken);

        if ((shouldAcceptResult is not null && !shouldAcceptResult())
            || !TryQueueImportedTextureUpload(request, streamingGeneration, out _))
        {
            RecordState(request, VulkanTextureUploadGenerationState.Canceled, "stale or canceled before resource preparation");
            Interlocked.Increment(ref s_canceledStaleUploads);
            onCanceled?.Invoke();
            return false;
        }

        VulkanImportedTextureUploadJob job = new(
            request,
            residentData,
            includeMipChain,
            Interlocked.Increment(ref _nextQueuedUploadSequence),
            shouldAcceptResult,
            onFinished,
            onCanceled,
            onError);

        LogCompatibilityPathState(renderer);
        if (!RenderDiagnosticsFlags.VkAsyncTextureUpload)
        {
            RecordState(request, VulkanTextureUploadGenerationState.PrepRunning, "async upload prep disabled; preparing immediately on render thread");
            while (true)
            {
                VulkanImportedTextureUploadPrepResult immediateResult = TryPrepareAndEnqueueImportedTextureUpload(
                    renderer,
                    job,
                    TextureRuntimeDiagnostics.StartTiming(),
                    0.0);
                if (immediateResult == VulkanImportedTextureUploadPrepResult.Deferred)
                    continue;

                return immediateResult == VulkanImportedTextureUploadPrepResult.Completed;
            }
        }

        QueueUploadPreparation(renderer, job);
        return true;
    }

    private void QueueUploadPreparation(VulkanRenderer renderer, VulkanImportedTextureUploadJob job)
    {
        int depth;
        double oldestWaitMilliseconds;
        lock (_prepQueueSync)
        {
            _pendingPrepJobs.Add(job);
            depth = _pendingPrepJobs.Count;
            oldestWaitMilliseconds = GetOldestQueueWaitMillisecondsNoLock();
        }

        Volatile.Write(ref s_pendingVulkanPrepPackages, depth);
        RecordState(
            job.Request,
            VulkanTextureUploadGenerationState.PrepQueued,
            $"queued Vulkan upload prep depth={depth} oldestWaitMs={oldestWaitMilliseconds:F3}");
        RenderWorkBudgetCoordinator.RecordTextureQueue(depth, oldestWaitMilliseconds);
        EnsurePrepDrainScheduled(renderer);
    }

    private void EnsurePrepDrainScheduled(VulkanRenderer renderer)
    {
        if (Interlocked.CompareExchange(ref _prepDrainScheduled, 1, 0) != 0)
            return;

        RuntimeRenderingHostServices.Current.EnqueueRenderThreadCoroutine(
            () => DrainQueuedUploadPreparation(renderer),
            "VulkanTextureUploadService.DrainUploadPrepQueue",
            RenderThreadJobKind.TextureUpload);
    }

    private bool DrainQueuedUploadPreparation(VulkanRenderer renderer)
    {
        if (renderer.IsDeviceLost)
        {
            CancelQueuedPreparation("Vulkan device was lost before upload preparation");
            Interlocked.Exchange(ref _prepDrainScheduled, 0);
            return true;
        }

        double prepBudgetMilliseconds = ResolvePrepBudgetMilliseconds();
        long drainStart = TextureRuntimeDiagnostics.StartTiming();
        int preparedThisDrain = 0;

        while (TryDequeueBestPrepJob(out VulkanImportedTextureUploadJob job))
        {
            if (!job.ShouldAccept())
            {
                RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, "request became stale before Vulkan upload prep");
                job.OnCanceled?.Invoke();
                continue;
            }

            if (!CanPrepareJobThisFrame(job, preparedThisDrain, drainStart, prepBudgetMilliseconds))
            {
                RequeueUploadPreparation(job);
                RecordState(
                    job.Request,
                    VulkanTextureUploadGenerationState.PrepDeferred,
                    $"budget deferred prep budgetMs={prepBudgetMilliseconds:F3} queueWaitMs={job.QueueWaitMilliseconds:F3}");
                return false;
            }

            VulkanImportedTextureUploadPrepResult prepResult = TryPrepareAndEnqueueImportedTextureUpload(
                renderer,
                job,
                drainStart,
                prepBudgetMilliseconds);
            if (prepResult == VulkanImportedTextureUploadPrepResult.Deferred)
            {
                RequeueUploadPreparation(job);
                RecordState(
                    job.Request,
                    VulkanTextureUploadGenerationState.PrepDeferred,
                    $"budget deferred prep budgetMs={prepBudgetMilliseconds:F3} queueWaitMs={job.QueueWaitMilliseconds:F3}");
                return false;
            }

            if (prepResult == VulkanImportedTextureUploadPrepResult.Completed)
                preparedThisDrain++;

            if (ShouldYieldAfterPreparation(preparedThisDrain, drainStart, prepBudgetMilliseconds))
                return HasQueuedPrepWorkOrCompleteDrain();
        }

        return HasQueuedPrepWorkOrCompleteDrain();
    }

    private VulkanImportedTextureUploadPrepResult TryPrepareAndEnqueueImportedTextureUpload(
        VulkanRenderer renderer,
        VulkanImportedTextureUploadJob job,
        long drainStart,
        double prepBudgetMilliseconds)
    {
        VulkanImportedTextureUploadRequest request = job.Request;
        if (renderer.IsDeviceLost)
        {
            RecordState(request, VulkanTextureUploadGenerationState.Canceled, "Vulkan device was lost before upload preparation");
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        if (!job.ShouldAccept())
        {
            RecordState(request, VulkanTextureUploadGenerationState.Canceled, "stale or canceled before upload preparation");
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        try
        {
            if (!EnsureJobPreparation(renderer, job, out VulkanImportedTextureUploadPreparation? preparation, out string? failureReason)
                || preparation is null)
            {
                bool canceled = failureReason is not null
                    && (failureReason.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                        || failureReason.Contains("collected", StringComparison.OrdinalIgnoreCase));
                RecordState(
                    request,
                    canceled ? VulkanTextureUploadGenerationState.Canceled : VulkanTextureUploadGenerationState.Failed,
                    failureReason ?? "failed to initialize Vulkan upload preparation");
                if (canceled)
                {
                    Interlocked.Increment(ref s_canceledStaleUploads);
                    job.OnCanceled?.Invoke();
                    return VulkanImportedTextureUploadPrepResult.Canceled;
                }

                Interlocked.Increment(ref s_failedUploads);
                job.OnError?.Invoke(new InvalidOperationException(failureReason ?? "Failed to initialize Vulkan upload preparation."));
                return VulkanImportedTextureUploadPrepResult.Failed;
            }

            if (RenderDiagnosticsFlags.VkTextureUploadPrepWorker)
                return TryDrainWorkerPreparation(renderer, job, preparation);

            while (true)
            {
                if (!job.ShouldAccept())
                {
                    preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                    job.Preparation = null;
                    RecordState(request, VulkanTextureUploadGenerationState.Canceled, "stale or canceled during Vulkan upload preparation");
                    Interlocked.Increment(ref s_canceledStaleUploads);
                    job.OnCanceled?.Invoke();
                    return VulkanImportedTextureUploadPrepResult.Canceled;
                }

                long stepStart = TextureRuntimeDiagnostics.StartTiming();
                Interlocked.Increment(ref s_activePrepPackages);
                bool stepOk;
                bool completed;
                VulkanImportedTexturePendingUpload? pendingUpload;
                string? stepFailure;
                try
                {
                    stepOk = preparation.Texture.TryAdvanceSynchronizedImportedUploadPreparation(
                        preparation,
                        out completed,
                        out pendingUpload,
                        out stepFailure);
                }
                finally
                {
                    int active = Interlocked.Decrement(ref s_activePrepPackages);
                    if (active < 0)
                        Interlocked.Exchange(ref s_activePrepPackages, 0);
                }

                double stepMilliseconds = TextureRuntimeDiagnostics.ElapsedMilliseconds(stepStart);
                TextureRuntimeDiagnostics.RecordUploadDuration(stepMilliseconds);
                RenderWorkBudgetCoordinator.RecordCompleted(RenderWorkSubsystem.TextureUpload, stepMilliseconds);
                RuntimeRenderingHostServices.Current.RecordRenderTextureUpload(request.EstimatedBytes, TimeSpan.FromMilliseconds(stepMilliseconds));
                Volatile.Write(ref s_lastRenderThreadPrepMilliseconds, stepMilliseconds);

                if (!stepOk)
                {
                    preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                    job.Preparation = null;
                    bool canceled = stepFailure is not null
                        && stepFailure.Contains("canceled", StringComparison.OrdinalIgnoreCase);
                    RecordState(
                        request,
                        canceled ? VulkanTextureUploadGenerationState.Canceled : VulkanTextureUploadGenerationState.Failed,
                        stepFailure ?? "failed to prepare Vulkan upload resources");
                    if (canceled)
                    {
                        Interlocked.Increment(ref s_canceledStaleUploads);
                        job.OnCanceled?.Invoke();
                        return VulkanImportedTextureUploadPrepResult.Canceled;
                    }

                    Interlocked.Increment(ref s_failedUploads);
                    job.OnError?.Invoke(new InvalidOperationException(stepFailure ?? "Failed to prepare Vulkan upload resources."));
                    return VulkanImportedTextureUploadPrepResult.Failed;
                }

                if (completed && pendingUpload is not null)
                {
                    double prepMilliseconds = TextureRuntimeDiagnostics.ElapsedMilliseconds(preparation.PrepStartTimestamp);
                    job.Preparation = null;
                    RecordState(
                        request,
                        VulkanTextureUploadGenerationState.PrepReady,
                        $"prepared upload token={pendingUpload.PublicationToken} prepMs={prepMilliseconds:F3} stagingMips={pendingUpload.StagingResources.Length}");
                    QueuePreparedImportedTextureUpload(renderer, pendingUpload, prepMilliseconds, workerPrepared: false);
                    return VulkanImportedTextureUploadPrepResult.Completed;
                }

                if (ShouldDeferPrepStep(drainStart, prepBudgetMilliseconds))
                    return VulkanImportedTextureUploadPrepResult.Deferred;
            }
        }
        catch (Exception ex)
        {
            RecordState(request, VulkanTextureUploadGenerationState.Failed, ex.Message);
            Interlocked.Increment(ref s_failedUploads);
            if (job.Preparation is not null)
            {
                job.Preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(job.Preparation);
                job.Preparation = null;
            }
            job.OnError?.Invoke(ex);
            return VulkanImportedTextureUploadPrepResult.Failed;
        }
    }

    private bool EnsureJobPreparation(
        VulkanRenderer renderer,
        VulkanImportedTextureUploadJob job,
        out VulkanImportedTextureUploadPreparation? preparation,
        out string? failureReason)
    {
        preparation = job.Preparation;
        failureReason = null;
        if (preparation is not null)
            return true;

        if (!job.Request.TryGetTexture(out XRTexture2D? texture) || texture is null)
        {
            failureReason = "texture was collected before upload preparation";
            return false;
        }

        if (renderer.GetOrCreateAPIRenderObject(texture, generateNow: false) is not VulkanRenderer.VkTexture2D vkTexture)
        {
            failureReason = "Vulkan texture wrapper could not be resolved for imported texture upload.";
            return false;
        }

        job.TextureWrapper = vkTexture;
        ulong publicationToken = job.PublicationToken.HasValue
            ? unchecked((ulong)job.PublicationToken.Value)
            : AllocateDescriptorPublicationToken();
        job.PublicationToken = unchecked((long)publicationToken);

        RecordState(
            job.Request,
            VulkanTextureUploadGenerationState.PrepRunning,
            $"preparing image/staging resources token={publicationToken} queueWaitMs={job.QueueWaitMilliseconds:F3}");

        if (!vkTexture.TryCreateSynchronizedImportedUploadPreparation(
                job.Request,
                job.ResidentData,
                job.IncludeMipChain,
                publicationToken,
                job.ShouldAcceptResult,
                job.OnFinished,
                job.OnCanceled,
                job.OnError,
                out preparation,
                out failureReason)
            || preparation is null)
        {
            return false;
        }

        job.Preparation = preparation;
        return true;
    }

    private VulkanImportedTextureUploadPrepResult TryDrainWorkerPreparation(
        VulkanRenderer renderer,
        VulkanImportedTextureUploadJob job,
        VulkanImportedTextureUploadPreparation preparation)
    {
        if (job.WorkerPrepTask is null)
        {
            if (job.Request.CancellationToken.IsCancellationRequested)
            {
                preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, "worker upload preparation was canceled before scheduling");
                Interlocked.Increment(ref s_canceledStaleUploads);
                job.OnCanceled?.Invoke();
                job.Preparation = null;
                return VulkanImportedTextureUploadPrepResult.Canceled;
            }

            job.WorkerPrepTask = Task.Run(() => RunWorkerPreparation(renderer, preparation));
            return VulkanImportedTextureUploadPrepResult.Deferred;
        }

        if (!job.WorkerPrepTask.IsCompleted)
            return VulkanImportedTextureUploadPrepResult.Deferred;

        VulkanImportedTextureUploadWorkerResult workerResult;
        try
        {
            workerResult = job.WorkerPrepTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, "worker upload preparation was canceled");
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
            job.Preparation = null;
            job.WorkerPrepTask = null;
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        job.WorkerPrepTask = null;
        job.Preparation = null;
        if (workerResult.Canceled)
        {
            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, workerResult.FailureReason ?? "worker upload preparation was canceled");
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        if (workerResult.Exception is not null || workerResult.PendingUpload is null)
        {
            string reason = workerResult.Exception?.Message ?? workerResult.FailureReason ?? "worker upload preparation failed";
            RecordState(job.Request, VulkanTextureUploadGenerationState.Failed, reason);
            Interlocked.Increment(ref s_failedUploads);
            job.OnError?.Invoke(workerResult.Exception ?? new InvalidOperationException(reason));
            return VulkanImportedTextureUploadPrepResult.Failed;
        }

        Volatile.Write(ref s_lastWorkerPrepMilliseconds, workerResult.PrepMilliseconds);
        TextureRuntimeDiagnostics.RecordUploadDuration(workerResult.PrepMilliseconds);
        RuntimeRenderingHostServices.Current.RecordRenderTextureUpload(job.Request.EstimatedBytes, TimeSpan.FromMilliseconds(workerResult.PrepMilliseconds));
        RecordState(
            job.Request,
            VulkanTextureUploadGenerationState.PrepReady,
            $"worker prepared upload token={workerResult.PendingUpload.PublicationToken} prepMs={workerResult.PrepMilliseconds:F3} stagingMips={workerResult.PendingUpload.StagingResources.Length}");
        QueuePreparedImportedTextureUpload(renderer, workerResult.PendingUpload, workerResult.PrepMilliseconds, workerPrepared: true);
        return VulkanImportedTextureUploadPrepResult.Completed;
    }

    private static VulkanImportedTextureUploadWorkerResult RunWorkerPreparation(
        VulkanRenderer renderer,
        VulkanImportedTextureUploadPreparation preparation)
    {
        long prepStart = TextureRuntimeDiagnostics.StartTiming();
        try
        {
            if (RuntimeRenderingHostServices.Current.IsRenderThread)
                throw new InvalidOperationException("Vulkan upload worker preparation must not run on the render thread or touch active frame command buffers.");

            lock (renderer.TextureUploadContextSync)
            {
                bool completed = false;
                VulkanImportedTexturePendingUpload? pendingUpload = null;
                string? failureReason = null;
                while (!completed)
                {
                    if (!preparation.ShouldAccept())
                    {
                        preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                        return new VulkanImportedTextureUploadWorkerResult(
                            null,
                            "request was canceled during worker upload preparation",
                            canceled: true,
                            TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                            null);
                    }

                    if (!preparation.Texture.TryAdvanceSynchronizedImportedUploadPreparation(
                            preparation,
                            out completed,
                            out pendingUpload,
                            out failureReason))
                    {
                        preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                        bool canceled = failureReason is not null
                            && failureReason.Contains("canceled", StringComparison.OrdinalIgnoreCase);
                        return new VulkanImportedTextureUploadWorkerResult(
                            null,
                            failureReason,
                            canceled,
                            TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                            null);
                    }
                }

                return new VulkanImportedTextureUploadWorkerResult(
                    pendingUpload,
                    null,
                    canceled: false,
                    TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                    null);
            }
        }
        catch (Exception ex)
        {
            preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
            return new VulkanImportedTextureUploadWorkerResult(
                null,
                ex.Message,
                canceled: false,
                TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                ex);
        }
    }

    private static bool ShouldDeferPrepStep(long drainStart, double prepBudgetMilliseconds)
        => prepBudgetMilliseconds > 0.0
            && TextureRuntimeDiagnostics.ElapsedMilliseconds(drainStart) >= prepBudgetMilliseconds;

    private void QueuePreparedImportedTextureUpload(
        VulkanRenderer renderer,
        VulkanImportedTexturePendingUpload pendingUpload,
        double prepMilliseconds,
        bool workerPrepared)
    {
        VulkanImportedTextureUploadRequest request = pendingUpload.Request;
        string? transferFailure = null;
        if (RenderDiagnosticsFlags.VkTextureUploadTransferQueue
            && renderer.TrySubmitImportedTextureUploadToTransferQueue(
                pendingUpload,
                out VulkanSubmittedImportedTextureUpload? submitted,
                out transferFailure)
            && submitted is not null)
        {
            lock (_transferQueueSync)
                _pendingTransferUploads.Add(submitted);

            Interlocked.Increment(ref s_pendingTransferSubmissions);
            Interlocked.Add(ref s_transferQueueBytesInFlight, submitted.BytesInFlight);
            RecordState(
                request,
                VulkanTextureUploadGenerationState.TransferSubmitted,
                $"submitted transfer-queue upload token={pendingUpload.PublicationToken} prepMs={prepMilliseconds:F3} workerPrep={workerPrepared}");
            EnsureTransferDrainScheduled(renderer);
            return;
        }

        if (RenderDiagnosticsFlags.VkTextureUploadTransferQueue
            && Interlocked.Exchange(ref _transferQueueCompatLogged, 1) == 0)
        {
            XREngine.Debug.Vulkan(
                "[Vulkan Compat] Imported texture upload '{0}' is using graphics-frame copy submission because transfer-queue submission was unavailable: {1}. Preferred Vulkan path is dedicated transfer queue copy plus graphics ownership acquire before descriptor publication.",
                request.TextureName ?? "<unnamed>",
                transferFailure ?? "unknown reason");
        }

        RecordState(
            request,
            VulkanTextureUploadGenerationState.GpuUploadPending,
            "queued graphics-frame texture upload op");
        renderer.EnqueueImportedTextureUpload(pendingUpload);
    }

    private void EnsureTransferDrainScheduled(VulkanRenderer renderer)
    {
        if (Interlocked.CompareExchange(ref _transferDrainScheduled, 1, 0) != 0)
            return;

        RuntimeRenderingHostServices.Current.EnqueueRenderThreadCoroutine(
            () => DrainSubmittedTextureTransfers(renderer),
            "VulkanTextureUploadService.DrainTransferUploads",
            RenderThreadJobKind.TextureUpload);
    }

    private bool DrainSubmittedTextureTransfers(VulkanRenderer renderer)
    {
        if (renderer.IsDeviceLost)
        {
            CancelSubmittedTransfers(renderer, "Vulkan device was lost while transfer uploads were pending");
            Interlocked.Exchange(ref _transferDrainScheduled, 0);
            return true;
        }

        while (TryPeekSubmittedTransfer(out VulkanSubmittedImportedTextureUpload? submitted) && submitted is not null)
        {
            if (!renderer.TryPollImportedTextureTransfer(submitted, out bool complete, out string? pollFailure))
            {
                RemoveSubmittedTransfer(submitted);
                submitted.Upload.Texture.ReleasePreparedImportedUploadResources(submitted.Upload);
                RecordState(submitted.Upload.Request, VulkanTextureUploadGenerationState.Failed, pollFailure ?? "transfer upload polling failed");
                Interlocked.Increment(ref s_failedUploads);
                InvokeTextureUploadError(submitted.Upload, new InvalidOperationException(pollFailure ?? "Transfer upload polling failed."));
                continue;
            }

            if (!complete)
                return false;

            RemoveSubmittedTransfer(submitted);
            Volatile.Write(ref s_lastTransferWaitMilliseconds, TextureRuntimeDiagnostics.ElapsedMilliseconds(submitted.SubmitTimestamp));
            RecordState(
                submitted.Upload.Request,
                VulkanTextureUploadGenerationState.TransferComplete,
                $"transfer upload fence signaled waitMs={Volatile.Read(ref s_lastTransferWaitMilliseconds):F3}");

            if (!renderer.CompleteSubmittedImportedTextureUpload(submitted, out string? completeFailure))
            {
                submitted.Upload.Texture.ReleasePreparedImportedUploadResources(submitted.Upload);
                RecordState(submitted.Upload.Request, VulkanTextureUploadGenerationState.Failed, completeFailure ?? "transfer upload completion failed");
                Interlocked.Increment(ref s_failedUploads);
                InvokeTextureUploadError(submitted.Upload, new InvalidOperationException(completeFailure ?? "Transfer upload completion failed."));
                continue;
            }

            PublishCompletedImportedTextureUpload(renderer, submitted.Upload, "transferQueue");
        }

        Interlocked.Exchange(ref _transferDrainScheduled, 0);
        lock (_transferQueueSync)
        {
            if (_pendingTransferUploads.Count == 0)
                return true;
        }

        return Interlocked.CompareExchange(ref _transferDrainScheduled, 1, 0) != 0
            ? true
            : false;
    }

    private bool TryPeekSubmittedTransfer(out VulkanSubmittedImportedTextureUpload? submitted)
    {
        lock (_transferQueueSync)
        {
            submitted = _pendingTransferUploads.Count == 0
                ? null
                : _pendingTransferUploads[0];
            return submitted is not null;
        }
    }

    private void RemoveSubmittedTransfer(VulkanSubmittedImportedTextureUpload submitted)
    {
        lock (_transferQueueSync)
            _pendingTransferUploads.Remove(submitted);

        int pending = Interlocked.Decrement(ref s_pendingTransferSubmissions);
        if (pending < 0)
            Interlocked.Exchange(ref s_pendingTransferSubmissions, 0);
        long bytes = Interlocked.Add(ref s_transferQueueBytesInFlight, -submitted.BytesInFlight);
        if (bytes < 0)
            Interlocked.Exchange(ref s_transferQueueBytesInFlight, 0);
    }

    private void CancelSubmittedTransfers(VulkanRenderer renderer, string reason)
    {
        VulkanSubmittedImportedTextureUpload[] submittedUploads;
        lock (_transferQueueSync)
        {
            submittedUploads = [.. _pendingTransferUploads];
            _pendingTransferUploads.Clear();
        }

        Volatile.Write(ref s_pendingTransferSubmissions, 0);
        Volatile.Write(ref s_transferQueueBytesInFlight, 0);
        for (int i = 0; i < submittedUploads.Length; i++)
        {
            VulkanSubmittedImportedTextureUpload submitted = submittedUploads[i];
            renderer.CompleteSubmittedImportedTextureUpload(submitted, out _);
            submitted.Upload.Texture.ReleasePreparedImportedUploadResources(submitted.Upload);
            RecordState(submitted.Upload.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            InvokeTextureUploadCanceled(submitted.Upload);
        }
    }

    private void PublishCompletedImportedTextureUpload(
        VulkanRenderer renderer,
        VulkanImportedTexturePendingUpload upload,
        string uploadSource)
    {
        VulkanImportedTextureUploadRequest request = upload.Request;
        if (!upload.ShouldPublish())
        {
            upload.Texture.ReleasePreparedImportedUploadResources(upload);
            RecordState(request, VulkanTextureUploadGenerationState.Canceled, $"request became stale before {uploadSource} descriptor publication");
            Interlocked.Increment(ref s_canceledStaleUploads);
            InvokeTextureUploadCanceled(upload);
            return;
        }

        RecordState(request, VulkanTextureUploadGenerationState.Uploaded, $"{uploadSource} upload completed");
        RecordState(
            request,
            VulkanTextureUploadGenerationState.DescriptorPublishPending,
            $"publicationToken={upload.PublicationToken}");

        Interlocked.Increment(ref s_pendingDescriptorPublications);
        long publicationStart = TextureRuntimeDiagnostics.StartTiming();
        upload.Texture.PublishSynchronizedImportedTextureUpload(upload);
        upload.MarkPublished();
        RetireTextureUploadStagingResources(renderer, upload);
        double publicationMilliseconds = TextureRuntimeDiagnostics.ElapsedMilliseconds(publicationStart);
        Volatile.Write(ref s_lastPublicationMilliseconds, publicationMilliseconds);
        int pending = Interlocked.Decrement(ref s_pendingDescriptorPublications);
        if (pending < 0)
            Interlocked.Exchange(ref s_pendingDescriptorPublications, 0);

        TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
            RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
            request.TextureName,
            request.SourcePath,
            request.StreamingGeneration,
            upload.PublicationToken,
            $"{uploadSource}DescriptorPublication",
            publicationMilliseconds);

        RecordState(
            request,
            VulkanTextureUploadGenerationState.Published,
            $"publicationToken={upload.PublicationToken}");
        RecordState(
            request,
            VulkanTextureUploadGenerationState.Retired,
            "old texture and staging resources enqueued for frame-slot retirement");
        InvokeTextureUploadFinished(upload);
    }

    private static void RetireTextureUploadStagingResources(
        VulkanRenderer renderer,
        VulkanImportedTexturePendingUpload upload)
    {
        for (int i = 0; i < upload.StagingResources.Length; i++)
        {
            VulkanImportedTextureUploadStagingResource staging = upload.StagingResources[i];
            renderer.RetireBuffer(staging.Buffer, staging.Memory);
        }
    }

    private static void InvokeTextureUploadFinished(VulkanImportedTexturePendingUpload upload)
    {
        if (!upload.TryGetTexture(out XRTexture2D? texture) || texture is null)
            return;

        try
        {
            upload.OnFinished?.Invoke(texture);
        }
        catch (Exception ex)
        {
            upload.OnError?.Invoke(ex);
        }
    }

    private static void InvokeTextureUploadCanceled(VulkanImportedTexturePendingUpload upload)
    {
        try
        {
            upload.OnCanceled?.Invoke();
        }
        catch (Exception ex)
        {
            upload.OnError?.Invoke(ex);
        }
    }

    private static void InvokeTextureUploadError(VulkanImportedTexturePendingUpload upload, Exception exception)
    {
        try
        {
            upload.OnError?.Invoke(exception);
        }
        catch
        {
            // Error callbacks are diagnostics-only; avoid recursive failure loops.
        }
    }

    private bool TryDequeueBestPrepJob(out VulkanImportedTextureUploadJob job)
    {
        lock (_prepQueueSync)
        {
            if (_pendingPrepJobs.Count == 0)
            {
                job = null!;
                RenderWorkBudgetCoordinator.RecordTextureQueue(0, 0.0);
                return false;
            }

            int bestIndex = 0;
            VulkanImportedTextureUploadJob best = _pendingPrepJobs[0];
            int bestRank = GetPriorityRank(best.Request.PriorityClass);
            for (int i = 1; i < _pendingPrepJobs.Count; i++)
            {
                VulkanImportedTextureUploadJob candidate = _pendingPrepJobs[i];
                int candidateRank = GetPriorityRank(candidate.Request.PriorityClass);
                if (candidateRank > bestRank
                    || (candidateRank == bestRank && candidate.Sequence < best.Sequence))
                {
                    bestIndex = i;
                    best = candidate;
                    bestRank = candidateRank;
                }
            }

            _pendingPrepJobs.RemoveAt(bestIndex);
            job = best;
            RenderWorkBudgetCoordinator.RecordTextureQueue(
                _pendingPrepJobs.Count,
                GetOldestQueueWaitMillisecondsNoLock());
            Volatile.Write(ref s_pendingVulkanPrepPackages, _pendingPrepJobs.Count);
            return true;
        }
    }

    private void RequeueUploadPreparation(VulkanImportedTextureUploadJob job)
    {
        lock (_prepQueueSync)
        {
            _pendingPrepJobs.Add(job);
            RenderWorkBudgetCoordinator.RecordTextureQueue(
                _pendingPrepJobs.Count,
                GetOldestQueueWaitMillisecondsNoLock());
            Volatile.Write(ref s_pendingVulkanPrepPackages, _pendingPrepJobs.Count);
        }
    }

    private void CancelQueuedPreparation(string reason)
    {
        VulkanImportedTextureUploadJob[] canceledJobs;
        lock (_prepQueueSync)
        {
            canceledJobs = [.. _pendingPrepJobs];
            _pendingPrepJobs.Clear();
            RenderWorkBudgetCoordinator.RecordTextureQueue(0, 0.0);
            Volatile.Write(ref s_pendingVulkanPrepPackages, 0);
        }

        for (int i = 0; i < canceledJobs.Length; i++)
        {
            VulkanImportedTextureUploadJob job = canceledJobs[i];
            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
        }
    }

    internal void CancelAllQueuedWork(VulkanRenderer renderer, string reason)
    {
        VulkanImportedTextureUploadJob[] canceledJobs;
        lock (_prepQueueSync)
        {
            canceledJobs = [.. _pendingPrepJobs];
            _pendingPrepJobs.Clear();
            RenderWorkBudgetCoordinator.RecordTextureQueue(0, 0.0);
            Volatile.Write(ref s_pendingVulkanPrepPackages, 0);
        }

        for (int i = 0; i < canceledJobs.Length; i++)
        {
            VulkanImportedTextureUploadJob job = canceledJobs[i];
            try
            {
                if (job.WorkerPrepTask is not null)
                    job.WorkerPrepTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            if (job.Preparation is not null)
            {
                job.Preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(job.Preparation);
                job.Preparation = null;
            }

            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
        }

        CancelSubmittedTransfers(renderer, reason);
        Interlocked.Exchange(ref _prepDrainScheduled, 0);
        Interlocked.Exchange(ref _transferDrainScheduled, 0);
    }

    private bool HasQueuedPrepWorkOrCompleteDrain()
    {
        int depth;
        double oldestWaitMilliseconds;
        lock (_prepQueueSync)
        {
            depth = _pendingPrepJobs.Count;
            oldestWaitMilliseconds = GetOldestQueueWaitMillisecondsNoLock();
        }

        RenderWorkBudgetCoordinator.RecordTextureQueue(depth, oldestWaitMilliseconds);
        Volatile.Write(ref s_pendingVulkanPrepPackages, depth);
        if (depth > 0)
            return false;

        Interlocked.Exchange(ref _prepDrainScheduled, 0);
        lock (_prepQueueSync)
        {
            if (_pendingPrepJobs.Count == 0)
                return true;
        }

        return Interlocked.CompareExchange(ref _prepDrainScheduled, 1, 0) != 0
            ? true
            : false;
    }

    private bool CanPrepareJobThisFrame(
        VulkanImportedTextureUploadJob job,
        int preparedThisDrain,
        long drainStart,
        double prepBudgetMilliseconds)
    {
        if (preparedThisDrain >= MaxPreparedUploadsPerDrain)
            return false;

        double estimate = EstimatePrepMilliseconds(job);
        if (!RenderWorkBudgetCoordinator.TryConsume(RenderWorkSubsystem.TextureUpload, estimate))
            return false;

        if (prepBudgetMilliseconds <= 0.0 || preparedThisDrain == 0)
            return true;

        return TextureRuntimeDiagnostics.ElapsedMilliseconds(drainStart) + estimate <= prepBudgetMilliseconds;
    }

    private static bool ShouldYieldAfterPreparation(
        int preparedThisDrain,
        long drainStart,
        double prepBudgetMilliseconds)
    {
        if (preparedThisDrain >= MaxPreparedUploadsPerDrain)
            return true;

        return prepBudgetMilliseconds > 0.0
            && TextureRuntimeDiagnostics.ElapsedMilliseconds(drainStart) >= prepBudgetMilliseconds;
    }

    private static double ResolvePrepBudgetMilliseconds()
    {
        double configured = RenderDiagnosticsFlags.VkTextureUploadPrepBudgetMilliseconds;
        if (configured <= 0.0)
            return 0.0;

        double frameBudget = RuntimeRenderingHostServices.Current.TextureUploadFrameBudgetMilliseconds;
        if (frameBudget <= 0.0)
            return configured;

        return Math.Min(configured, frameBudget);
    }

    private static double EstimatePrepMilliseconds(VulkanImportedTextureUploadJob job)
    {
        double bytesMiB = Math.Max(0L, job.Request.EstimatedBytes) / (1024.0 * 1024.0);
        return Math.Clamp(0.10 + bytesMiB * 0.08, 0.10, 4.0);
    }

    private double GetOldestQueueWaitMillisecondsNoLock()
    {
        if (_pendingPrepJobs.Count == 0)
            return 0.0;

        long oldest = long.MaxValue;
        for (int i = 0; i < _pendingPrepJobs.Count; i++)
            oldest = Math.Min(oldest, _pendingPrepJobs[i].QueueTimestamp);

        return oldest == long.MaxValue
            ? 0.0
            : TextureRuntimeDiagnostics.ElapsedMilliseconds(oldest);
    }

    private static int GetPriorityRank(TextureUploadPriorityClass priorityClass)
        => priorityClass switch
        {
            TextureUploadPriorityClass.VisibleNow => 4,
            TextureUploadPriorityClass.NearVisible => 3,
            TextureUploadPriorityClass.Background => 2,
            TextureUploadPriorityClass.Demotion => 1,
            _ => 0,
        };

    private void LogCompatibilityPathState(VulkanRenderer renderer)
    {
        if (RenderDiagnosticsFlags.VkTextureUploadPrepWorker
            && Interlocked.Exchange(ref _workerPrepCompatLogged, 1) == 0)
        {
            XREngine.Debug.Vulkan(
                "[Vulkan] XRE_VULKAN_TEXTURE_UPLOAD_PREP_WORKER requested; imported texture upload preparation will run on the Vulkan upload context lock and publish descriptors on the render thread.");
        }

        if (RenderDiagnosticsFlags.VkTextureUploadTransferQueue
            && !renderer.HasDedicatedTextureUploadTransferQueue
            && Interlocked.Exchange(ref _transferQueueCompatLogged, 1) == 0)
        {
            XREngine.Debug.Vulkan(
                "[Vulkan Compat] XRE_VULKAN_TEXTURE_UPLOAD_TRANSFER_QUEUE requested, but this device did not expose a dedicated transfer queue family; imported texture copies will submit through the graphics frame command buffer.");
        }

        if (!RenderDiagnosticsFlags.VkTextureUploadPrepWorker
            && Interlocked.Exchange(ref _renderThreadPrepCompatLogged, 1) == 0)
        {
            XREngine.Debug.Vulkan(
                "[Vulkan Compat] Imported texture upload preparation is budgeted on the render thread (budget {0:F3} ms). Preferred Vulkan path is worker-side preparation through a dedicated upload context.",
                ResolvePrepBudgetMilliseconds());
        }
    }
}
