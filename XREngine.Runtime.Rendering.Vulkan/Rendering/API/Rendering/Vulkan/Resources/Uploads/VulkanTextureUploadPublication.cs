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

    private void PublishCompletedImportedTextureUpload(
        VulkanResourceRuntime resourceRuntime,
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
        RetireTextureUploadStagingResources(resourceRuntime, upload);
        double publicationMilliseconds = TextureRuntimeDiagnostics.ElapsedMilliseconds(publicationStart);
        Volatile.Write(ref s_lastPublicationMilliseconds, publicationMilliseconds);
        int pending = Interlocked.Decrement(ref s_pendingDescriptorPublications);
        if (pending < 0)
            Interlocked.Exchange(ref s_pendingDescriptorPublications, 0);

        TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
            RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
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

    private void PublishCompletedRecordedTextureUpload(
        VulkanResourceRuntime resourceRuntime,
        VulkanImportedTexturePendingUpload upload,
        string uploadSource)
    {
        VulkanImportedTextureUploadRequest request = upload.Request;
        if (!upload.ShouldPublish())
        {
            upload.Texture.ReleasePreparedImportedUploadResources(upload);
            RecordState(
                request,
                VulkanTextureUploadGenerationState.Canceled,
                $"request became stale before {uploadSource} descriptor publication");
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
        upload.Texture.PublishSynchronizedImportedTextureUpload(upload);
        upload.MarkPublished();
        RetireTextureUploadStagingResources(resourceRuntime, upload);
        TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
            RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
            request.TextureName,
            request.SourcePath,
            request.StreamingGeneration,
            upload.PublicationToken,
            "uploadRecordToDescriptorPublication",
            upload.RecordTimestamp == 0L ? 0.0 : TextureRuntimeDiagnostics.ElapsedMilliseconds(upload.RecordTimestamp));
        TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
            RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
            request.TextureName,
            request.SourcePath,
            request.StreamingGeneration,
            upload.PublicationToken,
            "publicationToOldResourceRetirementEnqueue",
            TextureRuntimeDiagnostics.ElapsedMilliseconds(publicationStart));

        RecordState(request, VulkanTextureUploadGenerationState.Published, $"publicationToken={upload.PublicationToken}");
        RecordState(
            request,
            VulkanTextureUploadGenerationState.Retired,
            "old texture and staging resources enqueued for frame-slot retirement");
        InvokeTextureUploadFinished(upload);
    }

    private static void RetireTextureUploadStagingResources(
        VulkanResourceRuntime resourceRuntime,
        VulkanImportedTexturePendingUpload upload)
    {
        for (int index = 0; index < upload.StagingResources.Length; index++)
        {
            VulkanImportedTextureUploadStagingResource staging = upload.StagingResources[index];
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

}
