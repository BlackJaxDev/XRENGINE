using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
/// Vulkan imported texture upload service with worker-owned image and staging
/// preparation. The render owner observes prepared results without blocking and
/// publishes them through a frame-timeline operation, ordering descriptor swaps
/// with the copy.
/// </summary>
internal sealed partial class VulkanTextureUploadService
{
    internal const int MaxTransferBatchChunks = 4;
    internal const long MaxTransferBatchBytes = 16L * 1024L * 1024L;
    internal const int MaxTransferCompletionItemsPerFrame = 4;
    internal const long MaxTransferRetirementBytesPerFrame = 16L * 1024L * 1024L;
    internal VulkanTextureUploadPublicationState PublicationState { get; } = new();
    private const int MaxPreparedUploadsPerDrain = 1;
    private const int MaxInFlightPreparationWorkers = 2;
    private const double AllocationPressureRetryDelayMilliseconds = 500.0;

    private static int s_synchronizedImportedTextureStreamingAvailable = 1;
    private long _nextDescriptorPublicationToken;
    private long _nextQueuedUploadSequence;
    private readonly object _prepQueueSync = new();
    private readonly List<VulkanImportedTextureUploadJob> _pendingPrepJobs = [];
    // A job leaves the queue while its worker owns native preparation resources.
    // Keep that ownership visible until the render owner observes its terminal result.
    private readonly List<VulkanImportedTextureUploadJob> _inFlightPreparationWorkers = [];
    private readonly List<VulkanImportedTexturePendingUpload> _readyTransferUploads = [];
    private readonly List<VulkanSubmittedImportedTextureUploadBatch> _pendingTransferUploads = [];
    private readonly List<VulkanImportedTexturePendingUpload> _transferBatchScratch = new(4);
    private readonly object _transferQueueSync = new();
    private int _pendingTransferReservations;
    private int _prepDrainScheduled;
    private int _transferDrainScheduled;
    // A single render-scheduler boundary lets independently completed workers
    // contribute to the same bounded native batch. It is never a polling wait.
    private int _transferBatchGatherPending;
    // Render-owner-only accounting. A completed native batch stays fenced and
    // owned until its whole completion/publication cost fits this frame.
    private ulong _transferCompletionBudgetFrameId = ulong.MaxValue;
    private int _transferRetirementItemsThisFrame;
    private long _transferRetirementBytesThisFrame;
    private int _transferPublicationItemsThisFrame;
    private long _transferPublicationBytesThisFrame;
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
    // Prepared chunks are transfer-owned work even before a native batch exists.
    // Keep this separate from submitted work so host shutdown/readiness never
    // mistakes a gather boundary for an idle upload service.
    private static int s_readyTransferChunks;
    private static long s_readyTransferBytes;
    private static int s_pendingDescriptorPublications;
    private static long s_transferQueueBytesInFlight;
    private static long s_canceledStaleUploads;
    private static long s_failedUploads;
    private static double s_lastWorkerPrepMilliseconds;
    private static double s_lastTransferWaitMilliseconds;
    private static double s_lastPublicationMilliseconds;
    private static long s_workerPreparationStarts;
    private static long s_workerPreparationCompletions;
    private static long s_workerPreparationYields;
    private static long s_workerPreparationCancels;
    private static long s_ignoredWorkerPreparationDisableOverrides;
    private static int s_ownedWorkerPreparationJobs;
    private static long s_chunksPrepared;
    private static long s_chunksCompleted;
    private static long s_chunkBytesPrepared;
    private static long s_chunkBytesCompleted;
    private static long s_finalPublications;
    private static long s_coalescedTransferBatches;
    private static long s_coalescedTransferChunks;
    private static long s_transferAdmissionDeferrals;
    private static int s_maxTransferChunksInFlight;
    private static long s_maxTransferBytesInFlight;
    private static long s_transferPublicationBudgetDeferrals;
    private static long s_transferRetirementBudgetDeferrals;
    private static long s_transferRecordCpuCount;
    private static double s_lastTransferRecordCpuMilliseconds;
    private static long s_nativeAllocationCpuCount;
    private static double s_lastNativeAllocationCpuMilliseconds;
    private static long s_stagingCopyCpuCount;
    private static double s_lastStagingCopyCpuMilliseconds;
    private static long s_transferGpuTimingSamples;
    private static double s_lastTransferGpuMilliseconds;
    private static long s_transferGpuTimingUnavailable;

    public static bool IsSynchronizedImportedTextureStreamingAvailable
        => Volatile.Read(ref s_synchronizedImportedTextureStreamingAvailable) != 0;

    internal static bool HasActiveUploadWork
        => HasActiveUploadWorkCore(
            Volatile.Read(ref s_pendingResidentDataPackages),
            Volatile.Read(ref s_pendingVulkanPrepPackages),
            Volatile.Read(ref s_activePrepPackages),
            Volatile.Read(ref s_readyTransferChunks),
            Volatile.Read(ref s_pendingTransferSubmissions),
            Volatile.Read(ref s_pendingDescriptorPublications),
            Volatile.Read(ref s_transferQueueBytesInFlight));

    internal VulkanTextureStreamingDiagnosticSnapshot CaptureDiagnosticSnapshot()
    {
        int pendingPreparation;
        double oldestQueueAge;
        using (VulkanFrameLockScope.Enter(_prepQueueSync, EVulkanFrameWaitReason.UploadLock))
        {
            pendingPreparation = _pendingPrepJobs.Count;
            oldestQueueAge = GetOldestQueueWaitMillisecondsNoLock();
        }

        return new VulkanTextureStreamingDiagnosticSnapshot(
            pendingPreparation,
            Volatile.Read(ref s_ownedWorkerPreparationJobs),
            Volatile.Read(ref s_readyTransferChunks),
            Volatile.Read(ref s_readyTransferBytes),
            Volatile.Read(ref s_pendingTransferSubmissions),
            Volatile.Read(ref s_transferQueueBytesInFlight),
            Volatile.Read(ref s_workerPreparationStarts),
            Volatile.Read(ref s_workerPreparationCompletions),
            Volatile.Read(ref s_workerPreparationYields),
            Volatile.Read(ref s_workerPreparationCancels),
            Volatile.Read(ref s_chunksPrepared),
            Volatile.Read(ref s_chunksCompleted),
            Volatile.Read(ref s_chunkBytesPrepared),
            Volatile.Read(ref s_chunkBytesCompleted),
            Volatile.Read(ref s_finalPublications),
            Volatile.Read(ref s_canceledStaleUploads),
            Volatile.Read(ref s_failedUploads),
            Volatile.Read(ref s_coalescedTransferBatches),
            Volatile.Read(ref s_coalescedTransferChunks),
            Volatile.Read(ref s_transferAdmissionDeferrals),
            4,
            VulkanStagingManager.ImportedBackgroundBufferCapacity,
            MaxTransferBatchChunks,
            MaxTransferBatchBytes,
            Volatile.Read(ref s_maxTransferChunksInFlight),
            Volatile.Read(ref s_maxTransferBytesInFlight),
            _transferPublicationItemsThisFrame,
            _transferPublicationBytesThisFrame,
            _transferRetirementItemsThisFrame,
            _transferRetirementBytesThisFrame,
            Volatile.Read(ref s_transferPublicationBudgetDeferrals),
            Volatile.Read(ref s_transferRetirementBudgetDeferrals),
            Volatile.Read(ref s_transferRecordCpuCount),
            Volatile.Read(ref s_lastTransferRecordCpuMilliseconds),
            Volatile.Read(ref s_nativeAllocationCpuCount),
            Volatile.Read(ref s_lastNativeAllocationCpuMilliseconds),
            Volatile.Read(ref s_stagingCopyCpuCount),
            Volatile.Read(ref s_lastStagingCopyCpuMilliseconds),
            oldestQueueAge,
            Volatile.Read(ref s_lastWorkerPrepMilliseconds),
            Volatile.Read(ref s_lastTransferWaitMilliseconds),
            Volatile.Read(ref s_lastPublicationMilliseconds),
            Volatile.Read(ref s_transferGpuTimingSamples),
            Volatile.Read(ref s_lastTransferGpuMilliseconds),
            Volatile.Read(ref s_transferGpuTimingUnavailable));
    }

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

        using (VulkanFrameLockScope.Enter(
                   _prepQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            for (int index = 0; index < _pendingPrepJobs.Count; index++)
            {
                VulkanImportedTextureUploadJob job = _pendingPrepJobs[index];
                VulkanImportedTextureUploadRequest request = job.Request;
                if (request.PriorityClass == TextureUploadPriorityClass.VisibleNow &&
                    (!filterByRequiredTexture ||
                     IsRequiredTextureGeneration(
                         in request,
                         requiredTextures,
                         requiredGenerations)))
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

        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            for (int index = 0; index < _readyTransferUploads.Count; index++)
            {
                VulkanImportedTexturePendingUpload submitted = _readyTransferUploads[index];
                VulkanImportedTextureUploadRequest request = submitted.Request;
                if (request.PriorityClass == TextureUploadPriorityClass.VisibleNow &&
                    (!filterByRequiredTexture ||
                     IsRequiredTextureGeneration(
                         in request,
                         requiredTextures,
                         requiredGenerations)))
                {
                    request.TryGetTexture(out XRTexture2D? texture);
                    manifest.Add(submitted.Ticket, texture);
                    _ = TryPinUploadGeneration(
                        manifest,
                        texture,
                        submitted.Ticket);
                    _ = manifest.MarkCpuPrepared(submitted.Ticket);
                }
            }
            for (int index = 0; index < _pendingTransferUploads.Count; index++)
            {
                VulkanSubmittedImportedTextureUploadBatch batch = _pendingTransferUploads[index];
                for (int child = 0; child < batch.Uploads.Length; child++)
                {
                    VulkanImportedTexturePendingUpload submitted = batch.Uploads[child];
                    VulkanImportedTextureUploadRequest request = submitted.Request;
                    if (request.PriorityClass != TextureUploadPriorityClass.VisibleNow ||
                        (filterByRequiredTexture &&
                         !IsRequiredTextureGeneration(
                             in request,
                             requiredTextures,
                             requiredGenerations))) continue;
                    request.TryGetTexture(out XRTexture2D? texture);
                    manifest.Add(submitted.Ticket, texture);
                    _ = TryPinUploadGeneration(manifest, texture, submitted.Ticket);
                    _ = manifest.MarkCpuPrepared(submitted.Ticket);
                    _ = manifest.MarkGpuSubmitted(submitted.Ticket);
                }
            }
        }
    }

    private static bool IsRequiredTextureGeneration(
        in VulkanImportedTextureUploadRequest request,
        ReadOnlySpan<XRTexture?> requiredTextures,
        ReadOnlySpan<long> requiredGenerations)
    {
        if (!request.TryGetTexture(out XRTexture2D? texture) || texture is null)
            return false;
        for (int index = 0; index < requiredTextures.Length; index++)
            if (ReferenceEquals(requiredTextures[index], texture) &&
                requiredGenerations[index] == request.StreamingGeneration)
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
            (manifest.AreAllReady ||
             manifest.TryGetTerminalFailure(out _, out _, out _));
    }

    /// <summary>
    /// Detects a declared exact-generation ticket whose renderer-affine
    /// registration callback has not yet placed work in the Vulkan prep queue.
    /// PresentNow must yield the frame boundary so that callback can run.
    /// </summary>
    internal bool HasRequiredUploadRegistrationPending(
        VulkanTextureUploadManifest manifest)
    {
        using VulkanFrameLockScope scope = VulkanFrameLockScope.Enter(
            _prepQueueSync,
            EVulkanFrameWaitReason.UploadLock);
        for (int requiredIndex = 0; requiredIndex < manifest.Count; requiredIndex++)
        {
            ref readonly VulkanTextureUploadTicket ticket =
                ref manifest.GetTicket(requiredIndex);
            if (!manifest.TryGetState(
                    ticket,
                    out EVulkanFrameDependencyState state,
                    out _,
                    out _) ||
                state != EVulkanFrameDependencyState.Declared)
            {
                continue;
            }

            bool queued = false;
            for (int queueIndex = 0; queueIndex < _pendingPrepJobs.Count; queueIndex++)
            {
                if (_pendingPrepJobs[queueIndex].Ticket != ticket)
                    continue;
                queued = true;
                break;
            }
            if (!queued)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Advances one bounded slice of renderer-owned upload work before frame
    /// acceptance. This is the direct consumer used by the renderer frame hook;
    /// the scheduled coroutines remain as the inter-frame continuation path.
    /// </summary>
    internal void ProcessPendingUploads(
        VulkanTextureUploadSchedulingContext context)
    {
        if (!context.IsDeviceOperational)
            return;

        context.Resources.Allocations.Staging.EnsureForegroundReserve(
            context.BackendObjects);
        _ = DrainQueuedUploadPreparation(context);
        _ = DrainSubmittedTextureTransfers(context);
    }

    internal static bool TryDescribeActiveUploadWork(out string reason)
    {
        int pendingResidentData = Volatile.Read(ref s_pendingResidentDataPackages);
        int pendingPrep = Volatile.Read(ref s_pendingVulkanPrepPackages);
        int activePrep = Volatile.Read(ref s_activePrepPackages);
        int readyTransfers = Volatile.Read(ref s_readyTransferChunks);
        long readyBytes = Volatile.Read(ref s_readyTransferBytes);
        int pendingTransfers = Volatile.Read(ref s_pendingTransferSubmissions);
        int pendingPublications = Volatile.Read(ref s_pendingDescriptorPublications);
        long transferBytesInFlight = Volatile.Read(ref s_transferQueueBytesInFlight);

        if (!HasActiveUploadWorkCore(
                pendingResidentData,
                pendingPrep,
                activePrep,
                readyTransfers,
                pendingTransfers,
                pendingPublications,
                transferBytesInFlight))
        {
            reason = string.Empty;
            return false;
        }

        reason =
            $"Vulkan texture uploads are still active (residentData={pendingResidentData}, prepQueued={pendingPrep}, prepActive={activePrep}, readyTransfers={readyTransfers}, readyBytes={readyBytes}, transfers={pendingTransfers}, transferBytes={transferBytesInFlight}, descriptorPublications={pendingPublications})";
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
        builder.Append("VulkanTextureUploadReadyTransferChunks: ").Append(Volatile.Read(ref s_readyTransferChunks)).AppendLine();
        builder.Append("VulkanTextureUploadReadyTransferBytes: ").Append(Volatile.Read(ref s_readyTransferBytes)).AppendLine();
        builder.Append("VulkanTextureUploadPendingTransfers: ").Append(Volatile.Read(ref s_pendingTransferSubmissions)).AppendLine();
        builder.Append("VulkanTextureUploadTransferBytesInFlight: ").Append(Volatile.Read(ref s_transferQueueBytesInFlight)).AppendLine();
        builder.Append("VulkanTextureUploadPendingDescriptorPublications: ").Append(Volatile.Read(ref s_pendingDescriptorPublications)).AppendLine();
        builder.Append("VulkanTextureUploadCanceledStale: ").Append(Volatile.Read(ref s_canceledStaleUploads)).AppendLine();
        builder.Append("VulkanTextureUploadFailed: ").Append(Volatile.Read(ref s_failedUploads)).AppendLine();
        builder.Append("VulkanTextureUploadRenderThreadPrepMs: 0.000").AppendLine();
        builder.Append("VulkanTextureUploadWorkerPrepMs: ").Append(Volatile.Read(ref s_lastWorkerPrepMilliseconds).ToString("F3")).AppendLine();
        builder.Append("VulkanTextureUploadWorkerPrepStarts: ").Append(Volatile.Read(ref s_workerPreparationStarts)).AppendLine();
        builder.Append("VulkanTextureUploadWorkerPrepCompletions: ").Append(Volatile.Read(ref s_workerPreparationCompletions)).AppendLine();
        builder.Append("VulkanTextureUploadWorkerPrepYields: ").Append(Volatile.Read(ref s_workerPreparationYields)).AppendLine();
        builder.Append("VulkanTextureUploadWorkerPrepCancels: ").Append(Volatile.Read(ref s_workerPreparationCancels)).AppendLine();
        builder.Append("VulkanTextureUploadWorkerPrepOwned: ").Append(Volatile.Read(ref s_ownedWorkerPreparationJobs)).AppendLine();
        builder.Append("VulkanTextureUploadWorkerOnly: 1").AppendLine();
        builder.Append("VulkanTextureUploadRequestedAsync: ").Append(RenderDiagnosticsFlags.VkAsyncTextureUpload ? 1 : 0).AppendLine();
        builder.Append("VulkanTextureUploadRequestedPrepWorker: ").Append(RenderDiagnosticsFlags.VkTextureUploadPrepWorker ? 1 : 0).AppendLine();
        builder.Append("VulkanTextureUploadIgnoredWorkerDisableOverrides: ").Append(Volatile.Read(ref s_ignoredWorkerPreparationDisableOverrides)).AppendLine();
        builder.Append("VulkanTextureUploadTransferWaitMs: ").Append(Volatile.Read(ref s_lastTransferWaitMilliseconds).ToString("F3")).AppendLine();
        builder.Append("VulkanTextureUploadPublicationMs: ").Append(Volatile.Read(ref s_lastPublicationMilliseconds).ToString("F3")).AppendLine();
        builder.Append("VulkanTextureUploadChunksPrepared: ").Append(Volatile.Read(ref s_chunksPrepared)).AppendLine();
        builder.Append("VulkanTextureUploadChunksCompleted: ").Append(Volatile.Read(ref s_chunksCompleted)).AppendLine();
        builder.Append("VulkanTextureUploadChunkBytesPrepared: ").Append(Volatile.Read(ref s_chunkBytesPrepared)).AppendLine();
        builder.Append("VulkanTextureUploadChunkBytesCompleted: ").Append(Volatile.Read(ref s_chunkBytesCompleted)).AppendLine();
        builder.Append("VulkanTextureUploadFinalPublications: ").Append(Volatile.Read(ref s_finalPublications)).AppendLine();
        builder.Append("VulkanTextureUploadCoalescedBatches: ").Append(Volatile.Read(ref s_coalescedTransferBatches)).AppendLine();
        builder.Append("VulkanTextureUploadCoalescedChunks: ").Append(Volatile.Read(ref s_coalescedTransferChunks)).AppendLine();
        builder.Append("VulkanTextureUploadAdmissionDeferrals: ").Append(Volatile.Read(ref s_transferAdmissionDeferrals)).AppendLine();
        builder.Append("VulkanTextureUploadPublicationBudgetDeferrals: ").Append(Volatile.Read(ref s_transferPublicationBudgetDeferrals)).AppendLine();
        builder.Append("VulkanTextureUploadRetirementBudgetDeferrals: ").Append(Volatile.Read(ref s_transferRetirementBudgetDeferrals)).AppendLine();
        builder.Append("VulkanTextureUploadTransferRecordCpuCount: ").Append(Volatile.Read(ref s_transferRecordCpuCount)).AppendLine();
        builder.Append("VulkanTextureUploadTransferRecordCpuMs: ").Append(Volatile.Read(ref s_lastTransferRecordCpuMilliseconds).ToString("F3")).AppendLine();
        builder.Append("VulkanTextureUploadNativeAllocationCpuCount: ").Append(Volatile.Read(ref s_nativeAllocationCpuCount)).AppendLine();
        builder.Append("VulkanTextureUploadNativeAllocationCpuMs: ").Append(Volatile.Read(ref s_lastNativeAllocationCpuMilliseconds).ToString("F3")).AppendLine();
        builder.Append("VulkanTextureUploadStagingCopyCpuCount: ").Append(Volatile.Read(ref s_stagingCopyCpuCount)).AppendLine();
        builder.Append("VulkanTextureUploadStagingCopyCpuMs: ").Append(Volatile.Read(ref s_lastStagingCopyCpuMilliseconds).ToString("F3")).AppendLine();
        builder.Append("VulkanTextureUploadTransferGpuSamples: ").Append(Volatile.Read(ref s_transferGpuTimingSamples)).AppendLine();
        builder.Append("VulkanTextureUploadTransferGpuMs: ").Append(Volatile.Read(ref s_lastTransferGpuMilliseconds).ToString("F3")).AppendLine();
        builder.Append("VulkanTextureUploadTransferGpuUnavailable: ").Append(Volatile.Read(ref s_transferGpuTimingUnavailable)).AppendLine();
    }

    internal sealed class VulkanImportedTextureUploadJob
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
        /// <summary>Retained destination ticket between individually fenced staging chunks.</summary>
        public VulkanImportedTexturePendingUpload? PendingUpload { get; set; }
        public Task<VulkanImportedTextureUploadWorkerResult>? WorkerPrepTask { get; set; }
        public VulkanImportedTextureUploadWorkerResult? WorkerPrepResult { get; set; }
        public long? PublicationToken { get; set; }
        private int _foregroundRequired;
        private int _terminalCallbackInvoked;

        public bool IsForegroundRequired =>
            Request.PriorityClass == TextureUploadPriorityClass.VisibleNow ||
            Volatile.Read(ref _foregroundRequired) != 0;

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

        public void PromoteToForeground()
            => Volatile.Write(ref _foregroundRequired, 1);

        public void InvokeCanceledOnce()
        {
            if (Interlocked.Exchange(ref _terminalCallbackInvoked, 1) == 0)
                OnCanceled?.Invoke();
        }

        public void InvokeFinishedOnce(XRTexture2D texture)
        {
            if (Interlocked.Exchange(ref _terminalCallbackInvoked, 1) == 0)
                OnFinished?.Invoke(texture);
        }

        public void InvokeErrorOnce(Exception exception)
        {
            if (Interlocked.Exchange(ref _terminalCallbackInvoked, 1) == 0)
                OnError?.Invoke(exception);
        }
    }

    internal static void RecordStagingAdmissionDeferred()
        => Interlocked.Increment(ref s_transferAdmissionDeferrals);

    private static void UpdateMaximum(ref int target, int value)
    {
        int observed;
        while (value > (observed = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, observed) != observed)
        {
        }
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        long observed;
        while (value > (observed = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, observed) != observed)
        {
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
        CancellationToken cancellationToken,
        out VulkanTextureUploadTicket ticket)
    {
        ticket = default;
        if (!context.IsDeviceOperational ||
            Volatile.Read(ref _preparationRetirementStarted) != 0)
        {
            Interlocked.Increment(ref s_canceledStaleUploads);
            onCanceled?.Invoke();
            return false;
        }

        long estimatedBytes = XRTexture2D.CalculateResidentUploadBytes(residentData);
        long sequence = Interlocked.Increment(ref _nextQueuedUploadSequence);
        VulkanTextureUploadTicket createdTicket = new(sequence, streamingGeneration);
        ticket = createdTicket;
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
            createdTicket,
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
            createdTicket,
            residentData,
            includeMipChain,
            sequence,
            shouldAcceptResult,
            onFinished,
            onCanceled,
            onError);
        LogCompatibilityPathState(context.Commands);

        return QueueUploadPreparation(context, job);
    }

    private bool QueueUploadPreparation(VulkanTextureUploadSchedulingContext context, VulkanImportedTextureUploadJob job)
    {
        int depth = 0;
        double oldestWaitMilliseconds = 0.0;
        bool rejectedForRetirement;
        using (VulkanFrameLockScope.Enter(
                   _prepQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
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
        int readyTransfers,
        int pendingTransfers,
        int pendingPublications,
        long transferBytesInFlight)
        => pendingResidentData > 0
            || pendingPrep > 0
            || activePrep > 0
            || readyTransfers > 0
            || pendingTransfers > 0
            || pendingPublications > 0
            || transferBytesInFlight > 0;


}
