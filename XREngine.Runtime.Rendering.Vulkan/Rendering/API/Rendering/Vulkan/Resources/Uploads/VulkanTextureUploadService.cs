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

/// <summary>
/// Synchronized Vulkan imported texture upload service. The service prepares
/// staging/new image resources on the render thread, then publishes them through
/// a frame-timeline operation so descriptor swaps are ordered with the copy.
/// </summary>
internal sealed partial class VulkanTextureUploadService
{
    internal VulkanTextureUploadPublicationState PublicationState { get; } = new();
    private const int MaxPreparedUploadsPerDrain = 1;
    private const double AllocationPressureRetryDelayMilliseconds = 500.0;

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

    internal static bool TryDescribeActiveUploadWork(out string reason)
    {
        int pendingResidentData = Volatile.Read(ref s_pendingResidentDataPackages);
        int pendingPrep = Volatile.Read(ref s_pendingVulkanPrepPackages);
        int activePrep = Volatile.Read(ref s_activePrepPackages);
        int pendingTransfers = Volatile.Read(ref s_pendingTransferSubmissions);
        int pendingPublications = Volatile.Read(ref s_pendingDescriptorPublications);
        long transferBytesInFlight = Volatile.Read(ref s_transferQueueBytesInFlight);

        if (pendingResidentData <= 0 &&
            pendingPrep <= 0 &&
            activePrep <= 0 &&
            pendingTransfers <= 0 &&
            pendingPublications <= 0 &&
            transferBytesInFlight <= 0)
        {
            reason = string.Empty;
            return false;
        }

        reason =
            $"Vulkan texture uploads are still active (residentData={pendingResidentData}, prepQueued={pendingPrep}, prepActive={activePrep}, transfers={pendingTransfers}, transferBytes={transferBytesInFlight}, descriptorPublications={pendingPublications})";
        return true;
    }

    internal static bool TryDescribeBlockingOpenXrEyeUploadWork(out string reason)
    {
        int activePrep = Volatile.Read(ref s_activePrepPackages);
        int pendingTransfers = Volatile.Read(ref s_pendingTransferSubmissions);
        int pendingPublications = Volatile.Read(ref s_pendingDescriptorPublications);
        long transferBytesInFlight = Volatile.Read(ref s_transferQueueBytesInFlight);

        if (activePrep <= 0 &&
            pendingTransfers <= 0 &&
            pendingPublications <= 0 &&
            transferBytesInFlight <= 0)
        {
            reason = string.Empty;
            return false;
        }

        reason =
            $"Vulkan texture uploads have render-blocking work (prepActive={activePrep}, transfers={pendingTransfers}, transferBytes={transferBytesInFlight}, descriptorPublications={pendingPublications})";
        return true;
    }

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
        public long NotBeforeTimestamp { get; private set; }
        public VkTexture2D? TextureWrapper { get; set; }
        public VulkanImportedTextureUploadPreparation? Preparation { get; set; }
        public Task<VulkanImportedTextureUploadWorkerResult>? WorkerPrepTask { get; set; }
        public long? PublicationToken { get; set; }

        public bool ShouldAccept()
            => !Request.CancellationToken.IsCancellationRequested
                && (ShouldAcceptResult is null || ShouldAcceptResult());

        public double QueueWaitMilliseconds
            => TextureRuntimeDiagnostics.ElapsedMilliseconds(QueueTimestamp);

        public void DeferPreparationRetry(double delayMilliseconds)
        {
            double clampedDelay = Math.Clamp(delayMilliseconds, 1.0, 5000.0);
            long delayTicks = (long)Math.Ceiling(clampedDelay * Stopwatch.Frequency / 1000.0);
            NotBeforeTimestamp = Stopwatch.GetTimestamp() + Math.Max(1L, delayTicks);
        }
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
            RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
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
            RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
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
        VulkanTextureUploadSchedulingContext context,
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
        if (!context.IsDeviceOperational)
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

        LogCompatibilityPathState(context.Commands);
        if (!RenderDiagnosticsFlags.VkAsyncTextureUpload)
        {
            RecordState(request, VulkanTextureUploadGenerationState.PrepRunning, "async upload prep disabled; preparing immediately on render thread");
            while (true)
            {
                VulkanImportedTextureUploadPrepResult immediateResult = TryPrepareAndEnqueueImportedTextureUpload(
                    context,
                    job,
                    TextureRuntimeDiagnostics.StartTiming(),
                    0.0);
                if (immediateResult == VulkanImportedTextureUploadPrepResult.Deferred)
                {
                    QueueUploadPreparation(context, job);
                    return true;
                }

                return immediateResult == VulkanImportedTextureUploadPrepResult.Completed;
            }
        }

        QueueUploadPreparation(context, job);
        return true;
    }

    private void QueueUploadPreparation(VulkanTextureUploadSchedulingContext context, VulkanImportedTextureUploadJob job)
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
        EnsurePrepDrainScheduled(context);
    }

}
