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
            CancelSubmittedTransfers(context.Commands, "Vulkan device was lost while transfer uploads were pending");
            Interlocked.Exchange(ref _transferDrainScheduled, 0);
            return true;
        }

        while (TryPeekSubmittedTransfer(requiredManifest, out VulkanSubmittedImportedTextureUpload? submitted) && submitted is not null)
        {
            if (!context.Commands.TryPollImportedTextureTransfer(submitted, out bool complete, out string? pollFailure))
            {
                if (!RemoveSubmittedTransfer(submitted))
                    continue;

                submitted.Upload.Texture.ReleasePreparedImportedUploadResources(submitted.Upload);
                RecordState(submitted.Upload.Request, VulkanTextureUploadGenerationState.Failed, pollFailure ?? "transfer upload polling failed");
                Interlocked.Increment(ref s_failedUploads);
                InvokeTextureUploadError(submitted.Upload, new InvalidOperationException(pollFailure ?? "Transfer upload polling failed."));
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
                RecordState(submitted.Upload.Request, VulkanTextureUploadGenerationState.Failed, completeFailure ?? "transfer upload completion failed");
                Interlocked.Increment(ref s_failedUploads);
                InvokeTextureUploadError(submitted.Upload, new InvalidOperationException(completeFailure ?? "Transfer upload completion failed."));
                continue;
            }

            PublishCompletedImportedTextureUpload(context.Resources, submitted.Upload, "_deviceContext.TransferQueue");
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
        lock (_transferQueueSync)
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
        lock (_transferQueueSync)
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
        lock (_transferQueueSync)
        {
            submitted = null;
            for (int index = 0; index < _pendingTransferUploads.Count; index++)
            {
                VulkanSubmittedImportedTextureUpload candidate = _pendingTransferUploads[index];
                if (requiredManifest is null || requiredManifest.Contains(candidate.Upload.Ticket))
                {
                    submitted = candidate;
                    break;
                }
            }
            return submitted is not null;
        }
    }

    private bool RemoveSubmittedTransfer(VulkanSubmittedImportedTextureUpload submitted)
    {
        bool removed;
        lock (_transferQueueSync)
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
            commandRuntime.CompleteSubmittedImportedTextureUpload(submitted, out _);
            submitted.Upload.Texture.ReleasePreparedImportedUploadResources(submitted.Upload);
            RecordState(submitted.Upload.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            InvokeTextureUploadCanceled(submitted.Upload);
        }
    }

}
