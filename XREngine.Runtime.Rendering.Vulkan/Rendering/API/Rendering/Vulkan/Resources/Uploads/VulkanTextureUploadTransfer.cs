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
    private void EnsureTransferDrainScheduled(VulkanRenderer renderer)
    {
        if (Interlocked.CompareExchange(ref _transferDrainScheduled, 1, 0) != 0)
            return;

        RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadCoroutine(
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

}
