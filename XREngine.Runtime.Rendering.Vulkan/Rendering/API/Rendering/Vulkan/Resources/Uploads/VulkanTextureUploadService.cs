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
    private int _pendingTransferReservations;
    private int _prepDrainScheduled;
    private int _transferDrainScheduled;
    private int _preparationRetirementStarted;
    private int _activePreparationDrainCount;
    private readonly ManualResetEventSlim _preparationDrainsIdle = new(initialState: true);
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

    internal static bool HasActiveUploadWork
        => HasActiveUploadWorkCore(
            Volatile.Read(ref s_pendingResidentDataPackages),
            Volatile.Read(ref s_pendingVulkanPrepPackages),
            Volatile.Read(ref s_activePrepPackages),
            Volatile.Read(ref s_pendingTransferSubmissions),
            Volatile.Read(ref s_pendingDescriptorPublications),
            Volatile.Read(ref s_transferQueueBytesInFlight));

    /// <summary>
    /// Advances the foreground upload readiness barrier. Only VisibleNow
    /// preparation is admitted by the uncapped preparation pass; background
    /// streaming remains on its normal scheduled budget. Transfer completion is
    /// still polled through the normal fence/timeline path.
    /// </summary>
    internal VulkanTextureUploadManifest CaptureRequiredTextureUploadManifest()
    {
        VulkanTextureUploadManifest manifest = new();
        CaptureRequiredTextureUploadManifest(manifest);
        return manifest;
    }

    internal void CaptureRequiredTextureUploadManifest(
        VulkanTextureUploadManifest manifest)
        => CaptureRequiredTextureUploadManifestCore(
            manifest,
            requiredTextures: default,
            requiredGenerations: default,
            requireExactDescriptorPublication: false,
            filterByRequiredTexture: false);

    /// <summary>
    /// Captures the exact generations frozen for texture owners in the sealed
    /// accepted frame. A background upload becomes foreground-required when
    /// that exact generation is referenced by the accepted frame.
    /// </summary>
    internal void CaptureRequiredTextureUploadManifest(
        VulkanTextureUploadManifest manifest,
        ReadOnlySpan<XRTexture?> requiredTextures,
        ReadOnlySpan<long> requiredGenerations,
        bool requireExactDescriptorPublication = true)
        => CaptureRequiredTextureUploadManifestCore(
            manifest,
            requiredTextures,
            requiredGenerations,
            requireExactDescriptorPublication,
            filterByRequiredTexture: true);

    private void CaptureRequiredTextureUploadManifestCore(
        VulkanTextureUploadManifest manifest,
        ReadOnlySpan<XRTexture?> requiredTextures,
        ReadOnlySpan<long> requiredGenerations,
        bool requireExactDescriptorPublication,
        bool filterByRequiredTexture)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (filterByRequiredTexture &&
            requiredTextures.Length != requiredGenerations.Length)
        {
            throw new ArgumentException(
                "Required texture owners and generations must have equal lengths.");
        }
        manifest.BeginCapture(requireExactDescriptorPublication);
        if (filterByRequiredTexture)
        {
            for (int index = 0; index < requiredTextures.Length; index++)
            {
                if (requiredTextures[index] is not XRTexture2D texture)
                    continue;
                long requiredGeneration = requiredGenerations[index];
                if (requiredGeneration > 0L)
                    CaptureRequiredUploadGeneration(
                        manifest,
                        texture,
                        requiredGeneration);
            }
        }

        lock (_prepQueueSync)
        {
            for (int index = 0; index < _pendingPrepJobs.Count; index++)
            {
                VulkanImportedTextureUploadJob job = _pendingPrepJobs[index];
                VulkanImportedTextureUploadRequest request = job.Request;
                if (request.PriorityClass == TextureUploadPriorityClass.VisibleNow &&
                    (!filterByRequiredTexture ||
                     IsRequiredTexture(in request, requiredTextures)))
                {
                    request.TryGetTexture(out XRTexture2D? texture);
                    manifest.Add(job.Ticket, texture);
                    _ = TryPinUploadGeneration(manifest, texture, job.Ticket);
                    manifest.ApplyDurableState(
                        job.Ticket,
                        MapDependencyState(
                            VulkanTextureUploadGenerationState.PrepQueued),
                        null);
                }
            }
        }

        lock (_transferQueueSync)
        {
            for (int index = 0; index < _pendingTransferUploads.Count; index++)
            {
                VulkanSubmittedImportedTextureUpload submitted = _pendingTransferUploads[index];
                VulkanImportedTextureUploadRequest request = submitted.Upload.Request;
                if (request.PriorityClass == TextureUploadPriorityClass.VisibleNow &&
                    (!filterByRequiredTexture ||
                     IsRequiredTexture(in request, requiredTextures)))
                {
                    request.TryGetTexture(out XRTexture2D? texture);
                    manifest.Add(submitted.Upload.Ticket, texture);
                    _ = TryPinUploadGeneration(
                        manifest,
                        texture,
                        submitted.Upload.Ticket);
                    _ = manifest.MarkCpuPrepared(submitted.Upload.Ticket);
                    _ = manifest.MarkGpuSubmitted(submitted.Upload.Ticket);
                    if (submitted.TryGetTerminalFailure(out string failureReason))
                        manifest.Fail(submitted.Upload.Ticket, failureReason);
                }
            }
        }
    }

    private static bool IsRequiredTexture(
        in VulkanImportedTextureUploadRequest request,
        ReadOnlySpan<XRTexture?> requiredTextures)
    {
        if (!request.TryGetTexture(out XRTexture2D? texture) || texture is null)
            return false;
        for (int index = 0; index < requiredTextures.Length; index++)
            if (ReferenceEquals(requiredTextures[index], texture))
                return true;
        return false;
    }

    internal bool DrainRequiredTextureUploads(
        VulkanTextureUploadSchedulingContext context,
        VulkanTextureUploadManifest manifest,
        out bool madeProgress)
    {
        ulong initialProgressVersion = manifest.ProgressVersion;
        RefreshRequiredUploadGenerations(manifest);
        context.Resources.Allocations.Staging.EnsureForegroundReserve(context.BackendObjects);
        bool preparationReady = DrainRequiredUploadPreparation(context, manifest);
        bool transfersReady = DrainRequiredTextureTransfers(context, manifest);
        RefreshRequiredUploadGenerations(manifest);
        madeProgress = manifest.ProgressVersion != initialProgressVersion;
        return preparationReady && transfersReady &&
            (manifest.AreAllReady || manifest.TryGetTerminalFailure(out _, out _));
    }

    internal static bool TryDescribeActiveUploadWork(out string reason)
    {
        int pendingResidentData = Volatile.Read(ref s_pendingResidentDataPackages);
        int pendingPrep = Volatile.Read(ref s_pendingVulkanPrepPackages);
        int activePrep = Volatile.Read(ref s_activePrepPackages);
        int pendingTransfers = Volatile.Read(ref s_pendingTransferSubmissions);
        int pendingPublications = Volatile.Read(ref s_pendingDescriptorPublications);
        long transferBytesInFlight = Volatile.Read(ref s_transferQueueBytesInFlight);

        if (!HasActiveUploadWorkCore(
                pendingResidentData,
                pendingPrep,
                activePrep,
                pendingTransfers,
                pendingPublications,
                transferBytesInFlight))
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
            VulkanTextureUploadTicket ticket,
            TextureStreamingResidentData residentData,
            bool includeMipChain,
            long sequence,
            Func<bool>? shouldAcceptResult,
            Action<XRTexture2D>? onFinished,
            Action? onCanceled,
            Action<Exception>? onError)
        {
            Request = request;
            Ticket = ticket;
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
        public VulkanTextureUploadTicket Ticket { get; }
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
        UpdateUploadGeneration(request, state, detail);
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
        if (Volatile.Read(ref _preparationRetirementStarted) != 0 ||
            !ShouldAcceptResult(request, currentStreamingGeneration))
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
        if (!context.IsDeviceOperational ||
            Volatile.Read(ref _preparationRetirementStarted) != 0)
        {
            Interlocked.Increment(ref s_canceledStaleUploads);
            onCanceled?.Invoke();
            return false;
        }

        long estimatedBytes = XRTexture2D.CalculateResidentUploadBytes(residentData);
        long sequence = Interlocked.Increment(ref _nextQueuedUploadSequence);
        VulkanTextureUploadTicket ticket = new(sequence, streamingGeneration);
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
            ticket,
            streamingGeneration,
            priorityClass,
            cancellationToken);

        if (!RegisterUploadGeneration(texture, request, out string? ledgerFailure))
        {
            InvalidOperationException failure = new(
                ledgerFailure ??
                "The Vulkan texture upload generation ledger rejected the request.");
            Interlocked.Increment(ref s_failedUploads);
            onError?.Invoke(failure);
            return false;
        }

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
            ticket,
            residentData,
            includeMipChain,
            sequence,
            shouldAcceptResult,
            onFinished,
            onCanceled,
            onError);
        LogCompatibilityPathState(context.Commands);
        if (!RenderDiagnosticsFlags.VkAsyncTextureUpload &&
            RuntimeRenderingHostServices.FrameTiming.IsRenderThread)
        {
            RecordState(request, VulkanTextureUploadGenerationState.PrepRunning, "async upload prep disabled; preparing immediately on render thread");
            while (true)
            {
                VulkanImportedTextureUploadPrepResult immediateResult = TryPrepareAndEnqueueImportedTextureUpload(
                    context,
                    job,
                    TextureRuntimeDiagnostics.StartTiming(),
                    0.0,
                    requiredManifest: null);
                if (immediateResult == VulkanImportedTextureUploadPrepResult.Deferred)
                {
                    return QueueUploadPreparation(context, job);
                }

                return immediateResult == VulkanImportedTextureUploadPrepResult.Completed;
            }
        }

        return QueueUploadPreparation(context, job);
    }

    private bool QueueUploadPreparation(VulkanTextureUploadSchedulingContext context, VulkanImportedTextureUploadJob job)
    {
        int depth = 0;
        double oldestWaitMilliseconds = 0.0;
        bool rejectedForRetirement;
        lock (_prepQueueSync)
        {
            rejectedForRetirement = Volatile.Read(ref _preparationRetirementStarted) != 0;
            if (!rejectedForRetirement)
            {
                _pendingPrepJobs.Add(job);
                depth = _pendingPrepJobs.Count;
                oldestWaitMilliseconds = GetOldestQueueWaitMillisecondsNoLock();
            }
        }

        if (rejectedForRetirement)
        {
            RecordState(
                job.Request,
                VulkanTextureUploadGenerationState.Canceled,
                "Vulkan upload preparation admission closed for renderer retirement");
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
            return false;
        }

        Volatile.Write(ref s_pendingVulkanPrepPackages, depth);
        RecordState(
            job.Request,
            VulkanTextureUploadGenerationState.PrepQueued,
            $"queued Vulkan upload prep depth={depth} oldestWaitMs={oldestWaitMilliseconds:F3}");
        RenderWorkBudgetCoordinator.RecordTextureQueue(depth, oldestWaitMilliseconds);
        EnsurePrepDrainScheduled(context);
        return true;
    }

    private static bool HasActiveUploadWorkCore(
        int pendingResidentData,
        int pendingPrep,
        int activePrep,
        int pendingTransfers,
        int pendingPublications,
        long transferBytesInFlight)
        => pendingResidentData > 0
            || pendingPrep > 0
            || activePrep > 0
            || pendingTransfers > 0
            || pendingPublications > 0
            || transferBytesInFlight > 0;

}
