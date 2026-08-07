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

}
