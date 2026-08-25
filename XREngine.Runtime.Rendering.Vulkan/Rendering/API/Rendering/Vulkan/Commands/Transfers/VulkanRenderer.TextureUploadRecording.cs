using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanCommandRuntime
    {
        private VulkanTextureUploadPublicationState _textureUploadPublicationState
            => ResourceRuntime.Uploads.PublicationState;

        private bool TryRecordTextureUploadCommandBuffer(
            uint imageIndex,
            FrameOperationSequence textureUploadOps,
            out CommandBuffer commandBuffer,
            out CommandPool commandPool)
        {
            commandBuffer = default;
            commandPool = default;
            if (textureUploadOps.Length == 0)
                return false;

            bool commandBufferBegun = false;
            try
            {
                commandPool = GetThreadCommandPool();
                commandBuffer = AllocateCommandBuffer(
                    CommandBufferLevel.Primary,
                    "texture upload command buffer",
                    commandPool);
                RegisterCommandBufferImageIndex(commandBuffer, imageIndex);

                CommandBufferBeginInfo beginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };

                ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.TextureUploadRecording");
                if (BeginTrackedCommandBuffer(
                        commandBuffer,
                        ref beginInfo,
                        "TextureUploadRecording") != Result.Success)
                    throw new Exception("Failed to begin texture upload command buffer.");

                commandBufferBegun = true;

                bool uploadBatchLabelActive = _deviceContext.CmdBeginLabel(commandBuffer, "TextureUploads");
                int queuedBefore = _textureUploadPublicationState.RecordedForSubmit.Count;
                try
                {
                    for (int i = 0; i < textureUploadOps.Length; i++)
                    {
                        bool uploadLabelActive = _deviceContext.CmdBeginLabel(commandBuffer, "TextureUpload");
                        try
                        {
                            RecordTextureUploadOp(
                                commandBuffer,
                                textureUploadOps.GetTextureUpload(i).Upload);
                        }
                        finally
                        {
                            if (uploadLabelActive)
                                _deviceContext.CmdEndLabel(commandBuffer);
                        }
                    }
                }
                finally
                {
                    if (uploadBatchLabelActive)
                        _deviceContext.CmdEndLabel(commandBuffer);
                }

                if (EndCommandBufferTracked(commandBuffer) != Result.Success)
                    throw new Exception("Failed to end texture upload command buffer.");

                if (_textureUploadPublicationState.RecordedForSubmit.Count == queuedBefore)
                {
                    FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "TextureUpload.RecordFailure");
                    RemoveCommandBufferBindState(commandBuffer);
                    commandBuffer = default;
                    commandPool = default;
                    return false;
                }

                return true;
            }
            catch
            {
                CancelRecordedTextureUploadSubmitBatch("texture upload command buffer recording failed");

                if (commandBuffer.Handle != 0 && commandPool.Handle != 0 && !_deviceLost)
                {
                    if (!commandBufferBegun)
                    {
                        FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "TextureUpload.RecordException");
                    }
                    else
                    {
                        FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "TextureUpload.RecordDeviceLoss");
                    }
                }

                if (commandBuffer.Handle != 0)
                    RemoveCommandBufferBindState(commandBuffer);

                commandBuffer = default;
                commandPool = default;
                throw;
            }
        }

        internal unsafe void RecordTextureUploadOp(CommandBuffer commandBuffer, VulkanImportedTexturePendingUpload upload)
        {
            VulkanImportedTextureUploadRequest request = upload.Request;
            if (!upload.ShouldPublish())
            {
                ResourceRuntime.Uploads.RecordState(
                    request,
                    VulkanTextureUploadGenerationState.Canceled,
                    "request became stale or canceled before command recording");
                upload.Texture.ReleasePreparedImportedUploadResources(upload);
                InvokeTextureUploadCanceled(upload);
                return;
            }

            ResourceRuntime.Uploads.RecordState(
                request,
                VulkanTextureUploadGenerationState.UploadRecording,
                $"recording {upload.StagingResources.Length} mip copies token={upload.PublicationToken}");

            if (!upload.TryValidateCopyRegions(out string? validationFailure))
            {
                ResourceRuntime.Uploads.RecordState(
                    request,
                    VulkanTextureUploadGenerationState.Failed,
                    validationFailure);
                upload.Texture.ReleasePreparedImportedUploadResources(upload);
                InvokeTextureUploadError(upload, new InvalidOperationException(validationFailure ?? "Invalid Vulkan imported texture upload copy regions."));
                return;
            }

            upload.MarkRecordStarted();
            TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
                RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
                request.TextureName,
                request.SourcePath,
                request.StreamingGeneration,
                upload.PublicationToken,
                "decodeCompleteToUploadRecord",
                TextureRuntimeDiagnostics.ElapsedMilliseconds(upload.PreparedTimestamp));

            ImageSubresourceRange range = new()
            {
                AspectMask = upload.AspectMask,
                BaseMipLevel = 0,
                LevelCount = upload.MipLevels,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            ImageMemoryBarrier uploadBeginBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = upload.Image,
                SubresourceRange = range,
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &uploadBeginBarrier);

            for (int i = 0; i < upload.StagingResources.Length; i++)
            {
                VulkanImportedTextureUploadStagingResource staging = upload.StagingResources[i];
                BufferImageCopy copyRegion = staging.CopyRegion;
                CopyPreparedUploadBufferToImage(
                    commandBuffer,
                    staging.Buffer,
                    upload.Image,
                    ImageLayout.TransferDstOptimal,
                    ref copyRegion);
            }

            ImageMemoryBarrier uploadEndBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = upload.Image,
                SubresourceRange = range,
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &uploadEndBarrier);

            ResourceRuntime.Uploads.RecordState(
                request,
                VulkanTextureUploadGenerationState.Uploaded,
                $"recorded {upload.StagingResources.Length} mip copies");
            ResourceRuntime.Uploads.RecordState(
                request,
                VulkanTextureUploadGenerationState.DescriptorPublishPending,
                $"publicationToken={upload.PublicationToken}; waiting for recorded command buffer completion");

            QueueRecordedTextureUploadForSubmit(upload);
        }

        private void BeginRecordedTextureUploadSubmitBatch()
            => _textureUploadPublicationState.RecordedForSubmit.Clear();

        private void QueueRecordedTextureUploadForSubmit(VulkanImportedTexturePendingUpload upload)
            => _textureUploadPublicationState.RecordedForSubmit.Add(upload);

        private void QueueRecordedTextureUploadsForTimeline(ulong timelineValue, string uploadSource)
        {
            if (_textureUploadPublicationState.RecordedForSubmit.Count == 0)
                return;

            for (int i = 0; i < _textureUploadPublicationState.RecordedForSubmit.Count; i++)
            {
                _textureUploadPublicationState.PendingTimelinePublications.Add(
                    new PendingRecordedTextureUploadPublication(
                        _textureUploadPublicationState.RecordedForSubmit[i],
                        timelineValue,
                        uploadSource));
            }

            _textureUploadPublicationState.RecordedForSubmit.Clear();
        }

        private void PublishRecordedTextureUploadsAfterCompletedSubmit(string uploadSource)
        {
            if (_textureUploadPublicationState.RecordedForSubmit.Count == 0)
                return;

            for (int i = 0; i < _textureUploadPublicationState.RecordedForSubmit.Count; i++)
                PublishRecordedTextureUploadAfterGpuCompletion(_textureUploadPublicationState.RecordedForSubmit[i], uploadSource);

            _textureUploadPublicationState.RecordedForSubmit.Clear();
        }

        private void MoveRecordedTextureUploadsForSubmitTo(List<VulkanImportedTexturePendingUpload> destination)
        {
            if (_textureUploadPublicationState.RecordedForSubmit.Count == 0)
                return;

            destination.AddRange(_textureUploadPublicationState.RecordedForSubmit);
            _textureUploadPublicationState.RecordedForSubmit.Clear();
        }

        private void PublishRecordedTextureUploadsAfterCompletedSubmit(
            List<VulkanImportedTexturePendingUpload> uploads,
            string uploadSource)
        {
            if (uploads.Count == 0)
                return;

            for (int i = 0; i < uploads.Count; i++)
                PublishRecordedTextureUploadAfterGpuCompletion(uploads[i], uploadSource);

            uploads.Clear();
        }

        private void CancelRecordedTextureUploadSubmitBatch(string reason)
        {
            if (_textureUploadPublicationState.RecordedForSubmit.Count == 0)
                return;

            for (int i = 0; i < _textureUploadPublicationState.RecordedForSubmit.Count; i++)
                CancelRecordedTextureUpload(_textureUploadPublicationState.RecordedForSubmit[i], reason);

            _textureUploadPublicationState.RecordedForSubmit.Clear();
        }

        private void CancelRecordedTextureUploads(
            List<VulkanImportedTexturePendingUpload> uploads,
            string reason)
        {
            if (uploads.Count == 0)
                return;

            // Recorded primary/secondary command buffers may contain copies from
            // these staging resources. They cannot remain reusable after the
            // canceled upload retires those buffers, images, or descriptors.
            _ = InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease();
            MarkCommandBuffersDirty(reason);

            for (int i = 0; i < uploads.Count; i++)
                CancelRecordedTextureUpload(uploads[i], reason);

            uploads.Clear();
        }

        internal void CancelRecordedTextureUploadPublications(string reason)
        {
            CancelRecordedTextureUploadSubmitBatch(reason);

            if (_textureUploadPublicationState.PendingTimelinePublications.Count == 0)
                return;

            for (int i = 0; i < _textureUploadPublicationState.PendingTimelinePublications.Count; i++)
                CancelRecordedTextureUpload(_textureUploadPublicationState.PendingTimelinePublications[i].Upload, reason);

            _textureUploadPublicationState.PendingTimelinePublications.Clear();
        }

        private void CancelRecordedTextureUpload(
            VulkanImportedTexturePendingUpload upload,
            string reason)
        {
            VulkanImportedTextureUploadRequest request = upload.Request;
            ResourceRuntime.Uploads.RecordState(
                request,
                VulkanTextureUploadGenerationState.Canceled,
                reason);

            if (!_deviceLost)
                upload.Texture.ReleasePreparedImportedUploadResources(upload);

            InvokeTextureUploadCanceled(upload);
        }

        private void PublishRecordedTextureUploadAfterGpuCompletion(
            VulkanImportedTexturePendingUpload upload,
            string uploadSource)
        {
            VulkanImportedTextureUploadRequest request = upload.Request;
            if (!upload.ShouldPublish())
            {
                upload.Texture.ReleasePreparedImportedUploadResources(upload);
                ResourceRuntime.Uploads.RecordState(
                    request,
                    VulkanTextureUploadGenerationState.Canceled,
                    $"request became stale before {uploadSource} descriptor publication");
                InvokeTextureUploadCanceled(upload);
                return;
            }

            ResourceRuntime.Uploads.RecordState(
                request,
                VulkanTextureUploadGenerationState.Uploaded,
                $"{uploadSource} recorded upload completed");
            ResourceRuntime.Uploads.RecordState(
                request,
                VulkanTextureUploadGenerationState.DescriptorPublishPending,
                $"publicationToken={upload.PublicationToken}");

            long publicationStart = TextureRuntimeDiagnostics.StartTiming();
            upload.Texture.PublishSynchronizedImportedTextureUpload(upload);
            upload.MarkPublished();
            RetireTextureUploadStagingResources(upload);
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

            ResourceRuntime.Uploads.RecordState(
                request,
                VulkanTextureUploadGenerationState.Published,
                $"publicationToken={upload.PublicationToken}");
            ResourceRuntime.Uploads.RecordState(
                request,
                VulkanTextureUploadGenerationState.Retired,
                "old texture and staging resources enqueued for frame-slot retirement");
            InvokeTextureUploadFinished(upload);
        }

        private void RetireTextureUploadStagingResources(VulkanImportedTexturePendingUpload upload)
        {
            if (!upload.TryMarkStagingResourcesReleased())
                return;

            for (int i = 0; i < upload.StagingResources.Length; i++)
            {
                VulkanImportedTextureUploadStagingResource staging = upload.StagingResources[i];
                if (!staging.Slice.IsValid)
                    RetireUploadBuffer(
                        staging.Buffer,
                        staging.Memory,
                        "TextureUpload.Staging");
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
}
