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

internal sealed partial class VulkanTextureUploadService
{
    private void EnsureTransferDrainScheduled(VulkanTextureUploadSchedulingContext context)
    {
        if (Interlocked.CompareExchange(ref _transferDrainScheduled, 1, 0) != 0)
            return;

        RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadCoroutine(
            () => DrainSubmittedTextureTransfers(context),
            "VulkanTextureUploadService.DrainTransferUploads",
            RenderThreadJobKind.TextureUpload);
    }

    private bool DrainSubmittedTextureTransfers(
        VulkanTextureUploadSchedulingContext context,
        VulkanTextureUploadManifest? requiredManifest = null)
    {
        if (!context.IsDeviceOperational)
        {
            const string reason =
                "Vulkan device was lost while transfer uploads were pending.";
            requiredManifest?.FailUnresolved(reason);
            CancelSubmittedTransfers(context.Commands, reason);
            Interlocked.Exchange(ref _transferDrainScheduled, 0);
            return true;
        }

        DrainTerminalFailedTransferRetirement(context);

        if (TrySubmitReadyTransferBatch(context, requiredManifest, out bool gatherDeferred))
            return false;
        if (gatherDeferred)
            return true;

        while (TryPeekSubmittedTransfer(requiredManifest, out VulkanSubmittedImportedTextureUploadBatch? submitted) && submitted is not null)
        {
            if (!context.Commands.TryPollImportedTextureTransfer(submitted, out bool complete, out string? pollFailure))
            {
                string reason = pollFailure ??
                    "Required texture transfer upload polling failed.";
                if (submitted.TryMarkTerminalFailure(reason))
                {
                    for (int child = 0; child < submitted.Uploads.Length; child++)
                        ReportSubmittedUploadTerminalFailure(
                            submitted.Uploads[child], reason, requiredManifest);
                }
                continue;
            }

            if (!complete)
                return false;

            if (!TryReserveCompletedBatchBudget(submitted, requiredManifest))
                return false;

            Volatile.Write(ref s_lastTransferWaitMilliseconds, TextureRuntimeDiagnostics.ElapsedMilliseconds(submitted.SubmitTimestamp));
            if (!context.Commands.CompleteSubmittedImportedTextureUploadBatch(submitted, out string? completeFailure))
            {
                string reason = completeFailure ??
                    "Required texture transfer upload completion failed.";
                if (submitted.TryMarkTerminalFailure(reason))
                {
                    for (int child = 0; child < submitted.Uploads.Length; child++)
                        ReportSubmittedUploadTerminalFailure(
                            submitted.Uploads[child], reason, requiredManifest);
                }
                continue;
            }
            if (!RemoveSubmittedTransfer(submitted))
                return false;
            for (int child = 0; child < submitted.Uploads.Length; child++)
                CompleteSubmittedBatchChild(context, submitted.Uploads[child], requiredManifest);
            return HasSubmittedTransfersOrCompleteDrain(requiredManifest);
        }

        return HasSubmittedTransfersOrCompleteDrain(requiredManifest);
    }

    /// <summary>
    /// Completes one ordered transfer step for a foreground readiness barrier.
    /// The transfer queue remains asynchronous; callers must repeat until this
    /// returns true rather than forcing a device-wide idle.
    /// </summary>
    internal bool DrainRequiredTextureTransfers(
        VulkanTextureUploadSchedulingContext context,
        VulkanTextureUploadManifest manifest)
        => DrainSubmittedTextureTransfers(context, manifest);

    private bool HasSubmittedTransfersOrCompleteDrain(VulkanTextureUploadManifest? requiredManifest = null)
    {
        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            if (requiredManifest is not null)
            {
                for (int index = 0; index < _readyTransferUploads.Count; index++)
                    if (requiredManifest.Contains(_readyTransferUploads[index].Ticket)) return false;
                for (int index = 0; index < _pendingTransferUploads.Count; index++)
                {
                    VulkanImportedTexturePendingUpload[] uploads = _pendingTransferUploads[index].Uploads;
                    for (int child = 0; child < uploads.Length; child++)
                        if (requiredManifest.Contains(uploads[child].Ticket)) return false;
                }
                return true;
            }

            if (_readyTransferUploads.Count > 0 || _pendingTransferUploads.Count > 0)
                return false;
        }

        Interlocked.Exchange(ref _transferDrainScheduled, 0);
        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            if (_readyTransferUploads.Count == 0 && _pendingTransferUploads.Count == 0)
                return true;
        }

        return Interlocked.CompareExchange(ref _transferDrainScheduled, 1, 0) != 0
            ? true
            : false;
    }

    private bool TryPeekSubmittedTransfer(
        VulkanTextureUploadManifest? requiredManifest,
        out VulkanSubmittedImportedTextureUploadBatch? submitted)
    {
        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            submitted = null;
            for (int index = 0; index < _pendingTransferUploads.Count; index++)
            {
                VulkanSubmittedImportedTextureUploadBatch candidate = _pendingTransferUploads[index];
                if (!candidate.HasTerminalFailure &&
                    (requiredManifest is null || BatchContains(candidate, requiredManifest)))
                {
                    submitted = candidate;
                    break;
                }
            }
            return submitted is not null;
        }
    }

    /// <summary>
    /// Retires one terminal-failed submission only after its fence proves that
    /// the GPU no longer owns its command buffer, staging buffers, or image.
    /// Poll failures quarantine the native owner instead of freeing in-flight
    /// resources; device-loss teardown destroys the enclosing device objects.
    /// </summary>
    private void DrainTerminalFailedTransferRetirement(
        VulkanTextureUploadSchedulingContext context)
    {
        VulkanSubmittedImportedTextureUploadBatch? submitted = null;
        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            for (int index = 0; index < _pendingTransferUploads.Count; index++)
            {
                VulkanSubmittedImportedTextureUploadBatch candidate =
                    _pendingTransferUploads[index];
                if (!candidate.HasTerminalFailure ||
                    candidate.IsNativeCompletionFaulted ||
                    candidate.IsNativeCompletionInProgress)
                    continue;

                submitted = candidate;
                break;
            }
        }

        if (submitted is null ||
            !context.Commands.TryPollImportedTextureTransfer(
                submitted,
                out bool complete,
                out _) ||
            !complete)
        {
            return;
        }

        if (!TryReserveCompletedBatchBudget(submitted, requiredManifest: null))
            return;

        if (!context.Commands.CompleteSubmittedImportedTextureUploadBatch(
                submitted,
                out _))
        {
            return;
        }

        if (!RemoveSubmittedTransfer(submitted))
            return;

        for (int child = 0; child < submitted.Uploads.Length; child++)
        {
            VulkanImportedTexturePendingUpload upload = submitted.Uploads[child];
            if (submitted.IsCancellationRequested)
                ReleaseCanceledSubmittedUpload(upload);
            else
                FailSubmittedUpload(
                    upload,
                    submitted.TerminalFailureReason ??
                        "Texture upload batch retired after terminal submission failure.",
                    null);
        }

    }

    private bool RemoveSubmittedTransfer(VulkanSubmittedImportedTextureUploadBatch submitted)
    {
        bool removed;
        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
            removed = _pendingTransferUploads.Remove(submitted);

        if (!removed)
            return false;

        int pending = Interlocked.Add(ref s_pendingTransferSubmissions, -submitted.Uploads.Length);
        if (pending < 0)
            Interlocked.Exchange(ref s_pendingTransferSubmissions, 0);
        long bytes = Interlocked.Add(ref s_transferQueueBytesInFlight, -submitted.BytesInFlight);
        if (bytes < 0)
            Interlocked.Exchange(ref s_transferQueueBytesInFlight, 0);
        return true;
    }

    private static long RetireCompletedTextureUploadChunk(
        VulkanResourceRuntime resourceRuntime,
        VulkanImportedTexturePendingUpload upload)
    {
        VulkanImportedTextureUploadStagingResource[] staging = upload.DetachPreparedChunk();
        long bytes = 0;
        for (int index = 0; index < staging.Length; index++)
        {
            VulkanImportedTextureUploadStagingResource item = staging[index];
            bytes = checked(bytes + (long)item.SizeBytes);
            if (!item.Slice.IsValid)
                resourceRuntime.Buffers.Retire(
                    item.Buffer,
                    item.Memory,
                    "VulkanTextureUploadService.ChunkFenceComplete");
        }
        return bytes;
    }

    private static bool BatchContains(
        VulkanSubmittedImportedTextureUploadBatch batch,
        VulkanTextureUploadManifest manifest)
    {
        for (int child = 0; child < batch.Uploads.Length; child++)
            if (manifest.Contains(batch.Uploads[child].Ticket)) return true;
        return false;
    }

    private bool TrySubmitReadyTransferBatch(
        VulkanTextureUploadSchedulingContext context,
        VulkanTextureUploadManifest? requiredManifest,
        out bool gatherDeferred)
    {
        gatherDeferred = false;
        bool hasEligibleReadyUpload = false;
        using (VulkanFrameLockScope.Enter(_transferQueueSync, EVulkanFrameWaitReason.UploadLock))
        {
            for (int index = 0; index < _readyTransferUploads.Count; index++)
            {
                if (requiredManifest is null ||
                    requiredManifest.Contains(_readyTransferUploads[index].Ticket))
                {
                    hasEligibleReadyUpload = true;
                    break;
                }
            }
        }

        if (!hasEligibleReadyUpload)
        {
            // A foreground manifest is only a filtered view of the queue. An
            // empty/disjoint view must not repeatedly reset the ordinary
            // gather continuation and starve unrelated background transfers.
            if (requiredManifest is null)
                Interlocked.Exchange(ref _transferBatchGatherPending, 0);
            return false;
        }

        // Defer ordinary work exactly once so independently completed workers
        // can join one native batch. A required manifest is a readiness barrier
        // and therefore selects immediately. The continuation is separately
        // queued because a true coroutine result means completion.
        if (requiredManifest is null &&
            Interlocked.CompareExchange(ref _transferBatchGatherPending, 1, 0) == 0)
        {
            ScheduleTransferDrainContinuation(context);
            gatherDeferred = true;
            return false;
        }

        _transferBatchScratch.Clear();
        long bytes = 0;
        using (VulkanFrameLockScope.Enter(_transferQueueSync, EVulkanFrameWaitReason.UploadLock))
        {
            for (int index = 0; index < _readyTransferUploads.Count && _transferBatchScratch.Count < MaxTransferBatchChunks;)
            {
                VulkanImportedTexturePendingUpload candidate = _readyTransferUploads[index];
                if (requiredManifest is not null && !requiredManifest.Contains(candidate.Ticket)) { index++; continue; }
                long candidateBytes = (long)candidate.StagingResources[0].SizeBytes;
                if (_transferBatchScratch.Count > 0 && bytes + candidateBytes > MaxTransferBatchBytes) break;
                _transferBatchScratch.Add(candidate);
                bytes += candidateBytes;
                _readyTransferUploads.RemoveAt(index);
                int remainingReady = Interlocked.Decrement(ref s_readyTransferChunks);
                if (remainingReady < 0)
                    Interlocked.Exchange(ref s_readyTransferChunks, 0);
                long remainingReadyBytes = Interlocked.Add(ref s_readyTransferBytes, -candidateBytes);
                if (remainingReadyBytes < 0)
                    Interlocked.Exchange(ref s_readyTransferBytes, 0);
            }
        }
        if (_transferBatchScratch.Count == 0) return false;
        long recordStart = TextureRuntimeDiagnostics.StartTiming();
        bool submitted = context.Commands.TrySubmitImportedTextureUploadBatchToGraphicsQueue(
            _transferBatchScratch, out VulkanSubmittedImportedTextureUploadBatch? batch, out string? failure);
        RecordImportedTextureTransferRecordCpu(TextureRuntimeDiagnostics.ElapsedMilliseconds(recordStart));
        if (!submitted || batch is null)
        {
            for (int child = 0; child < _transferBatchScratch.Count; child++)
                FailSubmittedUpload(_transferBatchScratch[child], failure ?? "texture upload batch submission failed", requiredManifest);
            _transferBatchScratch.Clear();
            Interlocked.Exchange(ref _transferBatchGatherPending, 0);
            return true;
        }
        using (VulkanFrameLockScope.Enter(_transferQueueSync, EVulkanFrameWaitReason.UploadLock))
            _pendingTransferUploads.Add(batch);
        int chunksInFlight = Interlocked.Add(ref s_pendingTransferSubmissions, batch.Uploads.Length);
        long bytesInFlight = Interlocked.Add(ref s_transferQueueBytesInFlight, batch.BytesInFlight);
        UpdateMaximum(ref s_maxTransferChunksInFlight, chunksInFlight);
        UpdateMaximum(ref s_maxTransferBytesInFlight, bytesInFlight);
        Interlocked.Increment(ref s_coalescedTransferBatches);
        Interlocked.Add(ref s_coalescedTransferChunks, batch.Uploads.Length);
        for (int child = 0; child < batch.Uploads.Length; child++)
        {
            RecordUploadChunkProgress(batch.Uploads[child].Request, submitted: true);
            _ = requiredManifest?.MarkGpuSubmitted(batch.Uploads[child].Ticket);
            RecordState(batch.Uploads[child].Request, VulkanTextureUploadGenerationState.TransferSubmitted,
                $"batch submitted chunks={batch.Uploads.Length} bytes={batch.BytesInFlight}");
        }
        _transferBatchScratch.Clear();
        Interlocked.Exchange(ref _transferBatchGatherPending, 0);
        return true;
    }

    private void ScheduleTransferDrainContinuation(VulkanTextureUploadSchedulingContext context)
        => RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadCoroutine(
            () => DrainSubmittedTextureTransfers(context),
            "VulkanTextureUploadService.GatherTransferUploads",
            RenderThreadJobKind.TextureUpload);

    private void CompleteSubmittedBatchChild(
        VulkanTextureUploadSchedulingContext context,
        VulkanImportedTexturePendingUpload upload,
        VulkanTextureUploadManifest? manifest)
    {
        long completedBytes = RetireCompletedTextureUploadChunk(context.Resources, upload);
        // The batch fence and command-buffer cleanup have already completed.
        // Recycle only retirement-ready staging leases so the protected ring can
        // admit the next foreground chunk without a synthetic frame advance.
        _ = context.Resources.DrainCompletedStagingBuffers(maxItems: 4);
        Interlocked.Increment(ref s_chunksCompleted);
        Interlocked.Add(ref s_chunkBytesCompleted, completedBytes);
        RecordUploadChunkProgress(upload.Request, submitted: false);
        if (!upload.ShouldPublish())
        {
            const string reason =
                "Texture upload was canceled after its submitted chunk completed.";
            upload.Texture.ReleasePreparedImportedUploadResources(upload);
            manifest?.Fail(
                upload.Ticket,
                reason,
                EVulkanPresentNowFailureDisposition.RetryFrame);
            RecordState(upload.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            InvokeTextureUploadCanceled(upload);
            return;
        }
        if (!upload.CurrentChunkIsFinal)
        {
            if (!ResumeNextImportedTextureChunk(context, upload))
                FailSubmittedUpload(upload, "texture chunk could not resume after completion", manifest);
            return;
        }
        if (PublishCompletedImportedTextureUpload(context.Resources, upload, "graphics upload batch",
                manifest is not null && manifest.Contains(upload.Ticket) &&
                manifest.RequiresExactDescriptorPublication, out string? failure))
            _ = manifest?.MarkReady(upload.Ticket);
        else
            manifest?.Fail(
                upload.Ticket,
                failure ?? "final texture upload publication failed",
                upload.ShouldPublish()
                    ? EVulkanPresentNowFailureDisposition.RendererTerminal
                    : EVulkanPresentNowFailureDisposition.RetryFrame);
    }

    private void FailSubmittedUpload(
        VulkanImportedTexturePendingUpload upload,
        string reason,
        VulkanTextureUploadManifest? manifest)
    {
        upload.Texture.ReleasePreparedImportedUploadResources(upload);
        manifest?.Fail(upload.Ticket, reason);
        RecordState(upload.Request, VulkanTextureUploadGenerationState.Failed, reason);
        Interlocked.Increment(ref s_failedUploads);
        InvokeTextureUploadError(upload, new InvalidOperationException(reason));
    }

    private void ReportSubmittedUploadTerminalFailure(
        VulkanImportedTexturePendingUpload upload,
        string reason,
        VulkanTextureUploadManifest? manifest)
    {
        manifest?.Fail(upload.Ticket, reason);
        RecordState(upload.Request, VulkanTextureUploadGenerationState.Failed, reason);
        Interlocked.Increment(ref s_failedUploads);
        InvokeTextureUploadError(upload, new InvalidOperationException(reason));
    }

    private void ReleaseCanceledSubmittedUpload(VulkanImportedTexturePendingUpload upload)
        => upload.Texture.ReleasePreparedImportedUploadResources(upload);

    private bool ResumeNextImportedTextureChunk(
        VulkanTextureUploadSchedulingContext context,
        VulkanImportedTexturePendingUpload upload)
    {
        VulkanImportedTextureUploadJob? job = upload.OwnerJob;
        if (job is null || !job.ShouldAccept() ||
            Volatile.Read(ref _preparationRetirementStarted) != 0 ||
            !context.IsDeviceOperational)
        {
            return false;
        }

        job.PendingUpload = upload;
        return QueueUploadPreparation(context, job);
    }

    private void CancelSubmittedTransfers(VulkanCommandRuntime commandRuntime, string reason)
    {
        VulkanSubmittedImportedTextureUploadBatch[] submittedUploads;
        VulkanImportedTexturePendingUpload[] readyUploads;
        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            submittedUploads = [.. _pendingTransferUploads];
            readyUploads = [.. _readyTransferUploads];
            _readyTransferUploads.Clear();
        }

        Volatile.Write(ref s_readyTransferChunks, 0);
        Volatile.Write(ref s_readyTransferBytes, 0);
        for (int i = 0; i < submittedUploads.Length; i++)
        {
            VulkanSubmittedImportedTextureUploadBatch submitted = submittedUploads[i];
            bool completed = commandRuntime.TryPollImportedTextureTransfer(
                submitted,
                out bool fenceComplete,
                out _) && fenceComplete;
            if (completed &&
                commandRuntime.CompleteSubmittedImportedTextureUploadBatch(submitted, out _) &&
                RemoveSubmittedTransfer(submitted))
            {
                for (int child = 0; child < submitted.Uploads.Length; child++)
                    ReleaseCanceledSubmittedUpload(submitted.Uploads[child]);
            }
            else
            {
                _ = submitted.TryMarkCancellationRequested(reason);
                _ = submitted.TryMarkTerminalFailure(reason);
            }
            for (int child = 0; child < submitted.Uploads.Length; child++)
            {
                RecordState(submitted.Uploads[child].Request, VulkanTextureUploadGenerationState.Canceled, reason);
                Interlocked.Increment(ref s_canceledStaleUploads);
                InvokeTextureUploadCanceled(submitted.Uploads[child]);
            }
        }
        for (int i = 0; i < readyUploads.Length; i++)
        {
            VulkanImportedTexturePendingUpload upload = readyUploads[i];
            ReleaseCanceledSubmittedUpload(upload);
            RecordState(upload.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            InvokeTextureUploadCanceled(upload);
        }
    }

}
