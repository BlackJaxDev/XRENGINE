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

        while (TryPeekSubmittedTransfer(requiredManifest, out VulkanSubmittedImportedTextureUpload? submitted) && submitted is not null)
        {
            _ = requiredManifest?.MarkGpuSubmitted(submitted.Upload.Ticket);
            if (!context.Commands.TryPollImportedTextureTransfer(submitted, out bool complete, out string? pollFailure))
            {
                string reason = pollFailure ??
                    "Required texture transfer upload polling failed.";
                requiredManifest?.Fail(submitted.Upload.Ticket, reason);
                if (submitted.TryMarkTerminalFailure(reason))
                {
                    RecordState(
                        submitted.Upload.Request,
                        VulkanTextureUploadGenerationState.Failed,
                        reason);
                    Interlocked.Increment(ref s_failedUploads);
                    InvokeTextureUploadError(
                        submitted.Upload,
                        new InvalidOperationException(reason));
                }
                continue;
            }

            if (!complete)
                return false;

            if (!RemoveSubmittedTransfer(submitted))
                continue;

            Volatile.Write(ref s_lastTransferWaitMilliseconds, TextureRuntimeDiagnostics.ElapsedMilliseconds(submitted.SubmitTimestamp));
            RecordState(
                submitted.Upload.Request,
                VulkanTextureUploadGenerationState.TransferComplete,
                $"transfer upload fence signaled waitMs={Volatile.Read(ref s_lastTransferWaitMilliseconds):F3}");

            if (!context.Commands.CompleteSubmittedImportedTextureUpload(submitted, out string? completeFailure))
            {
                submitted.Upload.Texture.ReleasePreparedImportedUploadResources(submitted.Upload);
                string reason = completeFailure ??
                    "Required texture transfer upload completion failed.";
                requiredManifest?.Fail(submitted.Upload.Ticket, reason);
                RecordState(
                    submitted.Upload.Request,
                    VulkanTextureUploadGenerationState.Failed,
                    reason);
                Interlocked.Increment(ref s_failedUploads);
                InvokeTextureUploadError(
                    submitted.Upload,
                    new InvalidOperationException(reason));
                continue;
            }

            bool published;
            string? publicationFailure;
            try
            {
                published = PublishCompletedImportedTextureUpload(
                    context.Resources,
                    submitted.Upload,
                    "_deviceContext.TransferQueue",
                    requireExactDescriptorPublication:
                        requiredManifest?.RequiresExactDescriptorPublication == true,
                    out publicationFailure);
            }
            catch (Exception exception) when (requiredManifest is not null)
            {
                publicationFailure =
                    $"Required texture descriptor publication failed: " +
                    exception.Message;
                requiredManifest.Fail(
                    submitted.Upload.Ticket,
                    publicationFailure);
                RecordState(
                    submitted.Upload.Request,
                    VulkanTextureUploadGenerationState.Failed,
                    publicationFailure);
                Interlocked.Increment(ref s_failedUploads);
                InvokeTextureUploadError(submitted.Upload, exception);
                return true;
            }

            if (published)
            {
                _ = requiredManifest?.MarkReady(submitted.Upload.Ticket);
            }
            else
            {
                requiredManifest?.Fail(
                    submitted.Upload.Ticket,
                    publicationFailure ??
                        "Required texture descriptor publication failed.");
            }
            // Completion releases staging resources and publishes descriptors. Keep
            // that non-preemptible Vulkan work to one texture per render iteration;
            // draining a whole completed avatar batch here previously produced
            // triple-digit millisecond render-thread jobs.
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
                for (int index = 0; index < _pendingTransferUploads.Count; index++)
                {
                    if (requiredManifest.Contains(_pendingTransferUploads[index].Upload.Ticket))
                        return false;
                }
                return true;
            }

            if (_pendingTransferUploads.Count > 0)
                return false;
        }

        Interlocked.Exchange(ref _transferDrainScheduled, 0);
        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            if (_pendingTransferUploads.Count == 0)
                return true;
        }

        return Interlocked.CompareExchange(ref _transferDrainScheduled, 1, 0) != 0
            ? true
            : false;
    }

    private bool TryPeekSubmittedTransfer(
        VulkanTextureUploadManifest? requiredManifest,
        out VulkanSubmittedImportedTextureUpload? submitted)
    {
        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            submitted = null;
            for (int index = 0; index < _pendingTransferUploads.Count; index++)
            {
                VulkanSubmittedImportedTextureUpload candidate = _pendingTransferUploads[index];
                if (!candidate.HasTerminalFailure &&
                    (requiredManifest is null ||
                     requiredManifest.Contains(candidate.Upload.Ticket)))
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
        VulkanSubmittedImportedTextureUpload? submitted = null;
        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            for (int index = 0; index < _pendingTransferUploads.Count; index++)
            {
                VulkanSubmittedImportedTextureUpload candidate =
                    _pendingTransferUploads[index];
                if (!candidate.HasTerminalFailure)
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
            !complete ||
            !context.Commands.CompleteSubmittedImportedTextureUpload(
                submitted,
                out _))
        {
            return;
        }

        if (!RemoveSubmittedTransfer(submitted))
            return;

        submitted.Upload.Texture.ReleasePreparedImportedUploadResources(
            submitted.Upload);
    }

    private bool RemoveSubmittedTransfer(VulkanSubmittedImportedTextureUpload submitted)
    {
        bool removed;
        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
            removed = _pendingTransferUploads.Remove(submitted);

        if (!removed)
            return false;

        int pending = Interlocked.Decrement(ref s_pendingTransferSubmissions);
        if (pending < 0)
            Interlocked.Exchange(ref s_pendingTransferSubmissions, 0);
        long bytes = Interlocked.Add(ref s_transferQueueBytesInFlight, -submitted.BytesInFlight);
        if (bytes < 0)
            Interlocked.Exchange(ref s_transferQueueBytesInFlight, 0);
        return true;
    }

    private void CancelSubmittedTransfers(VulkanCommandRuntime commandRuntime, string reason)
    {
        VulkanSubmittedImportedTextureUpload[] submittedUploads;
        using (VulkanFrameLockScope.Enter(
                   _transferQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            submittedUploads = [.. _pendingTransferUploads];
            _pendingTransferUploads.Clear();
        }

        Volatile.Write(ref s_pendingTransferSubmissions, 0);
        Volatile.Write(ref s_transferQueueBytesInFlight, 0);
        for (int i = 0; i < submittedUploads.Length; i++)
        {
            VulkanSubmittedImportedTextureUpload submitted = submittedUploads[i];
            bool retired = commandRuntime.CompleteSubmittedImportedTextureUpload(
                submitted,
                out _);
            if (retired)
            {
                submitted.Upload.Texture.ReleasePreparedImportedUploadResources(
                    submitted.Upload);
            }
            RecordState(submitted.Upload.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            InvokeTextureUploadCanceled(submitted.Upload);
        }
    }

}
