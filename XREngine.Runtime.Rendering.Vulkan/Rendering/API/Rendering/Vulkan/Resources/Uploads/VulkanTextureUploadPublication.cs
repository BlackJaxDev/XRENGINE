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
    /// <summary>
    /// Publishes recorded uploads only after their graphics timeline receipt has
    /// completed.  The explicit device, command, and resource authorities keep
    /// descriptor publication and staging retirement independent of the renderer.
    /// </summary>
    internal void DrainCompletedRecordedTextureUploadPublications(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanCommandRuntime commandRuntime,
        VulkanResourceRuntime resourceRuntime,
        bool deviceLost)
    {
        List<PendingRecordedTextureUploadPublication> pendingPublications =
            PublicationState.PendingTimelinePublications;
        if (pendingPublications.Count == 0 || deviceLost)
            return;

        Silk.NET.Vulkan.Semaphore timelineSemaphore = commandRuntime.Synchronization._graphicsTimelineSemaphore;
        for (int index = pendingPublications.Count - 1; index >= 0; index--)
        {
            PendingRecordedTextureUploadPublication pending = pendingPublications[index];
            if (pending.TimelineValue == ulong.MaxValue)
                throw new InvalidOperationException(
                    "Refusing to query the invalid Vulkan timeline value ulong.MaxValue for a texture upload publication.");

            Result result = commandRuntime.Synchronization.QueryTimelineCompletion(
                api,
                deviceContext,
                resourceRuntime.Lifetime.Tracker,
                timelineSemaphore,
                pending.TimelineValue,
                out bool completed);
            if (result != Result.Success)
                return;
            if (!completed)
                continue;

            pendingPublications.RemoveAt(index);
            PublishCompletedRecordedTextureUpload(
                resourceRuntime,
                pending.Upload,
                pending.UploadSource);
        }
    }

    internal void CancelRecordedSubmitBatch(bool deviceLost, string reason)
    {
        List<VulkanImportedTexturePendingUpload> recorded =
            PublicationState.RecordedForSubmit;
        for (int index = 0; index < recorded.Count; index++)
        {
            VulkanImportedTexturePendingUpload upload = recorded[index];
            RecordState(
                upload.Request,
                VulkanTextureUploadGenerationState.Canceled,
                reason);
            if (!deviceLost)
                upload.Texture.ReleasePreparedImportedUploadResources(upload);
            InvokeTextureUploadCanceled(upload);
        }

        recorded.Clear();
    }

    private bool PublishCompletedImportedTextureUpload(
        VulkanResourceRuntime resourceRuntime,
        VulkanImportedTexturePendingUpload upload,
        string uploadSource,
        bool requireExactDescriptorPublication,
        out string? failureDetail,
        out EVulkanPresentNowFailureDisposition failureDisposition)
    {
        failureDisposition = EVulkanPresentNowFailureDisposition.RendererTerminal;
        VulkanImportedTextureUploadRequest request = upload.Request;
        if (!TryAcquireTexturePublicationAuthority(
                upload,
                uploadSource,
                out IDisposable? acquiredAuthority,
                out failureDetail))
        {
            failureDisposition = EVulkanPresentNowFailureDisposition.RetryFrame;
            upload.Texture.ReleasePreparedImportedUploadResources(upload);
            RecordState(
                request,
                VulkanTextureUploadGenerationState.Canceled,
                failureDetail);
            Interlocked.Increment(ref s_canceledStaleUploads);
            InvokeTextureUploadCanceled(upload);
            return false;
        }
        using IDisposable publicationAuthority = acquiredAuthority!;
        if (!upload.ShouldPublish())
        {
            failureDisposition = EVulkanPresentNowFailureDisposition.RetryFrame;
            failureDetail =
                $"Request became stale while acquiring {uploadSource} descriptor publication authority.";
            upload.Texture.ReleasePreparedImportedUploadResources(upload);
            RecordState(
                request,
                VulkanTextureUploadGenerationState.Canceled,
                failureDetail);
            Interlocked.Increment(ref s_canceledStaleUploads);
            InvokeTextureUploadCanceled(upload);
            return false;
        }

        RecordState(request, VulkanTextureUploadGenerationState.Uploaded, $"{uploadSource} upload completed");
        RecordState(
            request,
            VulkanTextureUploadGenerationState.DescriptorPublishPending,
            $"publicationToken={upload.PublicationToken}");

        Interlocked.Increment(ref s_pendingDescriptorPublications);
        try
        {
            long publicationStart = TextureRuntimeDiagnostics.StartTiming();
            EVulkanTextureDescriptorPublicationDisposition disposition;
            try
            {
                disposition =
                    upload.Texture.PublishSynchronizedImportedTextureUpload(
                        upload,
                        requireExactDescriptorPublication,
                        out failureDetail);
            }
            catch (Exception exception)
            {
                if (!upload.ShouldPublish())
                    failureDisposition = EVulkanPresentNowFailureDisposition.RetryFrame;
                failureDetail =
                    "Texture wrapper/descriptor publication raised " +
                    $"{exception.GetType().Name}: {exception.Message}";
                // This is safe both before and after a native descriptor commit:
                // the wrapper atomically marks transferred ownership before any
                // post-commit work that could report an exception.
                upload.Texture.ReleasePreparedImportedUploadResources(upload);
                RecordState(
                    request,
                    VulkanTextureUploadGenerationState.Failed,
                    failureDetail);
                Interlocked.Increment(ref s_failedUploads);
                InvokeTextureUploadError(upload, exception);
                return false;
            }
            if (disposition == EVulkanTextureDescriptorPublicationDisposition.StaleBeforePublication)
            {
                failureDisposition = RetryStaleImportedTextureUpload(upload, failureDetail);
                return false;
            }
            if (disposition ==
                    EVulkanTextureDescriptorPublicationDisposition.Failed ||
                requireExactDescriptorPublication && disposition !=
                    EVulkanTextureDescriptorPublicationDisposition.ExactPublished)
            {
                if (!upload.ShouldPublish())
                    failureDisposition = EVulkanPresentNowFailureDisposition.RetryFrame;
                failureDetail ??=
                    $"Descriptor publication ended with {disposition}.";
                upload.Texture.ReleasePreparedImportedUploadResources(upload);
                RecordState(
                    request,
                    VulkanTextureUploadGenerationState.Failed,
                    failureDetail);
                Interlocked.Increment(ref s_failedUploads);
                InvokeTextureUploadError(
                    upload,
                    new InvalidOperationException(failureDetail));
                return false;
            }
            CompleteCommittedTextureUploadPublicationNoThrow(
                resourceRuntime,
                upload,
                uploadSource,
                publicationStart,
                recordedUpload: false);
        }
        finally
        {
            int pending = Interlocked.Decrement(ref s_pendingDescriptorPublications);
            if (pending < 0)
                Interlocked.Exchange(ref s_pendingDescriptorPublications, 0);
        }

        failureDetail = null;
        return true;
    }

    internal void PublishCompletedRecordedTextureUpload(
        VulkanResourceRuntime resourceRuntime,
        VulkanImportedTexturePendingUpload upload,
        string uploadSource)
    {
        VulkanImportedTextureUploadRequest request = upload.Request;
        if (!TryAcquireTexturePublicationAuthority(
                upload,
                uploadSource,
                out IDisposable? acquiredAuthority,
                out string? authorityFailure))
        {
            upload.Texture.ReleasePreparedImportedUploadResources(upload);
            RecordState(
                request,
                VulkanTextureUploadGenerationState.Canceled,
                authorityFailure);
            Interlocked.Increment(ref s_canceledStaleUploads);
            InvokeTextureUploadCanceled(upload);
            return;
        }
        using IDisposable publicationAuthority = acquiredAuthority!;
        if (!upload.ShouldPublish())
        {
            string failure =
                $"Request became stale while acquiring {uploadSource} descriptor publication authority.";
            upload.Texture.ReleasePreparedImportedUploadResources(upload);
            RecordState(
                request,
                VulkanTextureUploadGenerationState.Canceled,
                failure);
            Interlocked.Increment(ref s_canceledStaleUploads);
            InvokeTextureUploadCanceled(upload);
            return;
        }

        RecordState(
            request,
            VulkanTextureUploadGenerationState.Uploaded,
            $"{uploadSource} recorded upload completed");
        RecordState(
            request,
            VulkanTextureUploadGenerationState.DescriptorPublishPending,
            $"publicationToken={upload.PublicationToken}");

        long publicationStart = TextureRuntimeDiagnostics.StartTiming();
        EVulkanTextureDescriptorPublicationDisposition disposition;
        string? publicationFailure;
        try
        {
            disposition = upload.Texture.PublishSynchronizedImportedTextureUpload(
                upload,
                requireExactDescriptorPublication: false,
                out publicationFailure);
        }
        catch (Exception exception)
        {
            publicationFailure =
                "Recorded texture wrapper/descriptor publication raised " +
                $"{exception.GetType().Name}: {exception.Message}";
            upload.Texture.ReleasePreparedImportedUploadResources(upload);
            RecordState(
                request,
                VulkanTextureUploadGenerationState.Failed,
                publicationFailure);
            Interlocked.Increment(ref s_failedUploads);
            InvokeTextureUploadError(upload, exception);
            return;
        }
        if (disposition == EVulkanTextureDescriptorPublicationDisposition.StaleBeforePublication)
        {
            _ = RetryStaleImportedTextureUpload(upload, publicationFailure);
            return;
        }
        if (disposition == EVulkanTextureDescriptorPublicationDisposition.Failed)
        {
            publicationFailure ??=
                "Recorded texture descriptor publication failed.";
            upload.Texture.ReleasePreparedImportedUploadResources(upload);
            RecordState(
                request,
                VulkanTextureUploadGenerationState.Failed,
                publicationFailure);
            Interlocked.Increment(ref s_failedUploads);
            InvokeTextureUploadError(
                upload,
                new InvalidOperationException(publicationFailure));
            return;
        }
        CompleteCommittedTextureUploadPublicationNoThrow(
            resourceRuntime,
            upload,
            uploadSource,
            publicationStart,
            recordedUpload: true);
    }

    /// <summary>
    /// Finishes diagnostics, staging retirement, and callbacks after wrapper
    /// and descriptor ownership have committed. Nothing in this tail may turn
    /// the published generation into a false failure or release its handles.
    /// </summary>
    private void CompleteCommittedTextureUploadPublicationNoThrow(
        VulkanResourceRuntime resourceRuntime,
        VulkanImportedTexturePendingUpload upload,
        string uploadSource,
        long publicationStart,
        bool recordedUpload)
    {
        VulkanImportedTextureUploadRequest request = upload.Request;
        try
        {
            upload.MarkPublished();
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning(
                "[Vulkan] Texture generation {0} committed through {1}, but publication timestamping failed: {2}",
                request.StreamingGeneration,
                uploadSource,
                exception.Message);
        }

        try
        {
            RetireTextureUploadStagingResources(resourceRuntime, upload);
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning(
                "[Vulkan] Texture generation {0} committed through {1}, but staging retirement could not be queued: {2}",
                request.StreamingGeneration,
                uploadSource,
                exception.Message);
        }

        try
        {
            if (recordedUpload)
            {
                TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
                    RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
                    request.TextureName,
                    request.SourcePath,
                    request.StreamingGeneration,
                    upload.PublicationToken,
                    "uploadRecordToDescriptorPublication",
                    upload.RecordTimestamp == 0L
                        ? 0.0
                        : TextureRuntimeDiagnostics.ElapsedMilliseconds(upload.RecordTimestamp));
                TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
                    RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
                    request.TextureName,
                    request.SourcePath,
                    request.StreamingGeneration,
                    upload.PublicationToken,
                    "publicationToOldResourceRetirementEnqueue",
                    TextureRuntimeDiagnostics.ElapsedMilliseconds(publicationStart));
            }
            else
            {
                double publicationMilliseconds =
                    TextureRuntimeDiagnostics.ElapsedMilliseconds(publicationStart);
                Volatile.Write(
                    ref s_lastPublicationMilliseconds,
                    publicationMilliseconds);
                TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
                    RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
                    request.TextureName,
                    request.SourcePath,
                    request.StreamingGeneration,
                    upload.PublicationToken,
                    $"{uploadSource}DescriptorPublication",
                    publicationMilliseconds);
            }
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning(
                "[Vulkan] Texture generation {0} committed through {1}, but latency diagnostics failed: {2}",
                request.StreamingGeneration,
                uploadSource,
                exception.Message);
        }

        try
        {
            RecordState(
                request,
                VulkanTextureUploadGenerationState.Published,
                $"publicationToken={upload.PublicationToken}");
            RecordState(
                request,
                VulkanTextureUploadGenerationState.Retired,
                "old texture and staging resources enqueued for frame-slot retirement");
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning(
                "[Vulkan] Texture generation {0} committed through {1}, but state telemetry failed: {2}",
                request.StreamingGeneration,
                uploadSource,
                exception.Message);
        }

        Interlocked.Increment(ref s_finalPublications);

        InvokeTextureUploadFinished(upload);
    }

    private static void RetireTextureUploadStagingResources(
        VulkanResourceRuntime resourceRuntime,
        VulkanImportedTexturePendingUpload upload)
    {
        // Normal chunk completion retires staging before descriptor publication.
        // This tail only owns an unrecorded terminal chunk from an exceptional
        // recorded path, so detaching remains exact-once without keeping all
        // texture mips resident in staging until publication.
        VulkanImportedTextureUploadStagingResource[] stagingResources = upload.DetachPreparedChunk();
        for (int index = 0; index < stagingResources.Length; index++)
        {
            VulkanImportedTextureUploadStagingResource staging = stagingResources[index];
            if (!staging.Slice.IsValid)
                resourceRuntime.Buffers.Retire(
                    staging.Buffer,
                    staging.Memory,
                    "VulkanTextureUploadService.RecordedPublication");
        }
    }

    private static void InvokeTextureUploadFinished(VulkanImportedTexturePendingUpload upload)
    {
        if (!upload.TryGetTexture(out XRTexture2D? texture) || texture is null)
            return;

        try
        {
            if (upload.OwnerJob is { } ownerJob)
            {
                ownerJob.InvokeFinishedOnce(texture);
                return;
            }
            upload.OnFinished?.Invoke(texture);
        }
        catch (Exception ex)
        {
            try
            {
                upload.OnError?.Invoke(ex);
            }
            catch
            {
                // Completion callbacks are diagnostics-only after commit.
            }
        }
    }

    private static void InvokeTextureUploadCanceled(VulkanImportedTexturePendingUpload upload)
    {
        try
        {
            if (upload.OwnerJob is { } ownerJob)
            {
                ownerJob.InvokeCanceledOnce();
                return;
            }
            upload.OnCanceled?.Invoke();
        }
        catch (Exception ex)
        {
            try
            {
                upload.OnError?.Invoke(ex);
            }
            catch
            {
                // Cancellation callbacks must not cause recursive failures.
            }
        }
    }

    private static void InvokeTextureUploadError(VulkanImportedTexturePendingUpload upload, Exception exception)
    {
        try
        {
            if (upload.OwnerJob is { } ownerJob)
            {
                ownerJob.InvokeErrorOnce(exception);
                return;
            }
            upload.OnError?.Invoke(exception);
        }
        catch
        {
            // Error callbacks are diagnostics-only; avoid recursive failure loops.
        }
    }

    private static bool TryAcquireTexturePublicationAuthority(
        VulkanImportedTexturePendingUpload upload,
        string uploadSource,
        out IDisposable? authority,
        out string failureDetail)
    {
        authority = null;
        if (!upload.TryGetTexture(out XRTexture2D? texture) || texture is null)
        {
            failureDetail =
                $"Texture owner was collected before {uploadSource} descriptor publication.";
            return false;
        }

        if (!ImportedTextureStreamingManager.Instance.TryAcquirePublicationAuthority(
                texture,
                upload.Request.StreamingGeneration,
                out authority) ||
            authority is null)
        {
            failureDetail =
                $"Texture generation {upload.Request.StreamingGeneration} was canceled, " +
                $"superseded, or already published before {uploadSource} descriptor publication.";
            return false;
        }

        failureDetail = string.Empty;
        return true;
    }

    /// <summary>
    /// Retires a stale native candidate and prepares the current authored state
    /// with a new ticket. The original job owns the terminal callback.
    /// </summary>
    private EVulkanPresentNowFailureDisposition RetryStaleImportedTextureUpload(
        VulkanImportedTexturePendingUpload upload,
        string? reason)
    {
        upload.Texture.ReleasePreparedImportedUploadResources(upload);

        if (upload.OwnerJob is not { } job)
        {
            RecordState(upload.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            InvokeTextureUploadCanceled(upload);
            return EVulkanPresentNowFailureDisposition.RetryFrame;
        }

        VulkanImportedTextureUploadJob callbackOwner = job.CallbackOwner;
        if (!upload.ShouldPublish()
            || !upload.TryGetTexture(out XRTexture2D? texture)
            || texture is null)
        {
            RecordState(upload.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            callbackOwner.InvokeCanceledOnce();
            return EVulkanPresentNowFailureDisposition.RetryFrame;
        }

        VulkanTextureUploadTicket replacementTicket = default;
        try
        {
            bool scheduled = TryScheduleImportedTextureUpload(
                job.SchedulingContext,
                texture,
                upload.ResidentData,
                upload.IncludeMipChain,
                upload.Request.TargetResidentMaxDimension,
                upload.Request.StreamingGeneration,
                upload.Request.PriorityClass,
                upload.ShouldAcceptResult,
                callbackOwner.InvokeFinishedOnce,
                callbackOwner.InvokeCanceledOnce,
                callbackOwner.InvokeErrorOnce,
                upload.Request.CancellationToken,
                out replacementTicket,
                callbackOwner);

            bool replacementRegistered = replacementTicket.IsValid &&
                TryGetUploadGeneration(texture, replacementTicket, out _, out _, out _);
            if (!scheduled && !replacementRegistered && replacementTicket.IsValid)
            {
                RecordState(
                    upload.Request,
                    VulkanTextureUploadGenerationState.Failed,
                    "Replacement upload admission failed before ticket registration.");
                return EVulkanPresentNowFailureDisposition.RendererTerminal;
            }

            RecordState(upload.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            return EVulkanPresentNowFailureDisposition.RetryFrame;
        }
        catch (Exception exception)
        {
            bool replacementRegistered = replacementTicket.IsValid &&
                TryGetUploadGeneration(texture, replacementTicket, out _, out _, out _);
            RecordState(
                upload.Request,
                replacementRegistered
                    ? VulkanTextureUploadGenerationState.Canceled
                    : VulkanTextureUploadGenerationState.Failed,
                replacementRegistered
                    ? reason
                    : "Replacement upload scheduling failed before ticket registration.");
            callbackOwner.InvokeErrorOnce(exception);
            return replacementRegistered
                ? EVulkanPresentNowFailureDisposition.RetryFrame
                : EVulkanPresentNowFailureDisposition.RendererTerminal;
        }
    }

}
