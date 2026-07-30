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
    private void EnsurePrepDrainScheduled(VulkanRenderer renderer)
    {
        if (Interlocked.CompareExchange(ref _prepDrainScheduled, 1, 0) != 0)
            return;

        RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadCoroutine(
            () => DrainQueuedUploadPreparation(renderer),
            "VulkanTextureUploadService.DrainUploadPrepQueue",
            RenderThreadJobKind.TextureUpload);
    }

    private bool DrainQueuedUploadPreparation(VulkanRenderer renderer)
    {
        if (renderer.IsDeviceLost)
        {
            CancelQueuedPreparation("Vulkan device was lost before upload preparation");
            Interlocked.Exchange(ref _prepDrainScheduled, 0);
            return true;
        }

        double prepBudgetMilliseconds = ResolvePrepBudgetMilliseconds();
        long drainStart = TextureRuntimeDiagnostics.StartTiming();
        int preparedThisDrain = 0;

        if (renderer.ShouldDeferTextureUploadPreparationForOpenXrPriority(out string openXrResourceReason))
        {
            XREngine.Debug.VulkanWarningEvery(
                $"VulkanTextureUploadService.OpenXrPriorityDeferred.{renderer.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[VulkanTextureUploadService] Deferring imported texture upload preparation: {0}",
                openXrResourceReason);
            return HasQueuedPrepWorkOrCompleteDrain();
        }

        while (TryDequeueBestPrepJob(out VulkanImportedTextureUploadJob job))
        {
            if (!job.ShouldAccept())
            {
                RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, "request became stale before Vulkan upload prep");
                job.OnCanceled?.Invoke();
                continue;
            }

            if (!CanPrepareJobThisFrame(job, preparedThisDrain, drainStart, prepBudgetMilliseconds))
            {
                RequeueUploadPreparation(job);
                RecordState(
                    job.Request,
                    VulkanTextureUploadGenerationState.PrepDeferred,
                    $"budget deferred prep budgetMs={prepBudgetMilliseconds:F3} queueWaitMs={job.QueueWaitMilliseconds:F3}");
                return false;
            }

            VulkanImportedTextureUploadPrepResult prepResult = TryPrepareAndEnqueueImportedTextureUpload(
                renderer,
                job,
                drainStart,
                prepBudgetMilliseconds);
            if (prepResult == VulkanImportedTextureUploadPrepResult.Deferred)
            {
                RequeueUploadPreparation(job);
                RecordState(
                    job.Request,
                    VulkanTextureUploadGenerationState.PrepDeferred,
                    $"budget deferred prep budgetMs={prepBudgetMilliseconds:F3} queueWaitMs={job.QueueWaitMilliseconds:F3}");
                return false;
            }

            if (prepResult == VulkanImportedTextureUploadPrepResult.Completed)
                preparedThisDrain++;

            if (ShouldYieldAfterPreparation(preparedThisDrain, drainStart, prepBudgetMilliseconds))
                return HasQueuedPrepWorkOrCompleteDrain();
        }

        return HasQueuedPrepWorkOrCompleteDrain();
    }

    private VulkanImportedTextureUploadPrepResult TryPrepareAndEnqueueImportedTextureUpload(
        VulkanRenderer renderer,
        VulkanImportedTextureUploadJob job,
        long drainStart,
        double prepBudgetMilliseconds)
    {
        VulkanImportedTextureUploadRequest request = job.Request;
        if (renderer.IsDeviceLost)
        {
            RecordState(request, VulkanTextureUploadGenerationState.Canceled, "Vulkan device was lost before upload preparation");
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        if (!job.ShouldAccept())
        {
            RecordState(request, VulkanTextureUploadGenerationState.Canceled, "stale or canceled before upload preparation");
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        try
        {
            if (!EnsureJobPreparation(renderer, job, out VulkanImportedTextureUploadPreparation? preparation, out string? failureReason)
                || preparation is null)
            {
                if (IsRetryableVulkanAllocationPressure(failureReason))
                {
                    job.DeferPreparationRetry(AllocationPressureRetryDelayMilliseconds);
                    RecordState(
                        request,
                        VulkanTextureUploadGenerationState.PrepDeferred,
                        $"allocation pressure deferred initial prep retryMs={AllocationPressureRetryDelayMilliseconds:F0}: {failureReason}");
                    return VulkanImportedTextureUploadPrepResult.Deferred;
                }

                bool canceled = failureReason is not null
                    && (failureReason.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                        || failureReason.Contains("collected", StringComparison.OrdinalIgnoreCase));
                RecordState(
                    request,
                    canceled ? VulkanTextureUploadGenerationState.Canceled : VulkanTextureUploadGenerationState.Failed,
                    failureReason ?? "failed to initialize Vulkan upload preparation");
                if (canceled)
                {
                    Interlocked.Increment(ref s_canceledStaleUploads);
                    job.OnCanceled?.Invoke();
                    return VulkanImportedTextureUploadPrepResult.Canceled;
                }

                Interlocked.Increment(ref s_failedUploads);
                job.OnError?.Invoke(new InvalidOperationException(failureReason ?? "Failed to initialize Vulkan upload preparation."));
                return VulkanImportedTextureUploadPrepResult.Failed;
            }

            if (RenderDiagnosticsFlags.VkTextureUploadPrepWorker)
                return TryDrainWorkerPreparation(renderer, job, preparation);

            while (true)
            {
                if (!job.ShouldAccept())
                {
                    preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                    job.Preparation = null;
                    RecordState(request, VulkanTextureUploadGenerationState.Canceled, "stale or canceled during Vulkan upload preparation");
                    Interlocked.Increment(ref s_canceledStaleUploads);
                    job.OnCanceled?.Invoke();
                    return VulkanImportedTextureUploadPrepResult.Canceled;
                }

                long stepStart = TextureRuntimeDiagnostics.StartTiming();
                Interlocked.Increment(ref s_activePrepPackages);
                bool stepOk;
                bool completed;
                VulkanImportedTexturePendingUpload? pendingUpload;
                string? stepFailure;
                try
                {
                    stepOk = preparation.Texture.TryAdvanceSynchronizedImportedUploadPreparation(
                        preparation,
                        out completed,
                        out pendingUpload,
                        out stepFailure);
                }
                finally
                {
                    int active = Interlocked.Decrement(ref s_activePrepPackages);
                    if (active < 0)
                        Interlocked.Exchange(ref s_activePrepPackages, 0);
                }

                double stepMilliseconds = TextureRuntimeDiagnostics.ElapsedMilliseconds(stepStart);
                TextureRuntimeDiagnostics.RecordUploadDuration(stepMilliseconds);
                RenderWorkBudgetCoordinator.RecordCompleted(RenderWorkSubsystem.TextureUpload, stepMilliseconds);
                RuntimeRenderingHostServices.Statistics.RecordRenderTextureUpload(request.EstimatedBytes, TimeSpan.FromMilliseconds(stepMilliseconds));
                Volatile.Write(ref s_lastRenderThreadPrepMilliseconds, stepMilliseconds);

                if (!stepOk)
                {
                    preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                    job.Preparation = null;
                    if (IsRetryableVulkanAllocationPressure(stepFailure))
                    {
                        job.DeferPreparationRetry(AllocationPressureRetryDelayMilliseconds);
                        RecordState(
                            request,
                            VulkanTextureUploadGenerationState.PrepDeferred,
                            $"allocation pressure deferred prep retryMs={AllocationPressureRetryDelayMilliseconds:F0}: {stepFailure}");
                        return VulkanImportedTextureUploadPrepResult.Deferred;
                    }

                    bool canceled = stepFailure is not null
                        && stepFailure.Contains("canceled", StringComparison.OrdinalIgnoreCase);
                    RecordState(
                        request,
                        canceled ? VulkanTextureUploadGenerationState.Canceled : VulkanTextureUploadGenerationState.Failed,
                        stepFailure ?? "failed to prepare Vulkan upload resources");
                    if (canceled)
                    {
                        Interlocked.Increment(ref s_canceledStaleUploads);
                        job.OnCanceled?.Invoke();
                        return VulkanImportedTextureUploadPrepResult.Canceled;
                    }

                    Interlocked.Increment(ref s_failedUploads);
                    job.OnError?.Invoke(new InvalidOperationException(stepFailure ?? "Failed to prepare Vulkan upload resources."));
                    return VulkanImportedTextureUploadPrepResult.Failed;
                }

                if (completed && pendingUpload is not null)
                {
                    double prepMilliseconds = TextureRuntimeDiagnostics.ElapsedMilliseconds(preparation.PrepStartTimestamp);
                    job.Preparation = null;
                    RecordState(
                        request,
                        VulkanTextureUploadGenerationState.PrepReady,
                        $"prepared upload token={pendingUpload.PublicationToken} prepMs={prepMilliseconds:F3} stagingMips={pendingUpload.StagingResources.Length}");
                    QueuePreparedImportedTextureUpload(renderer, pendingUpload, prepMilliseconds, workerPrepared: false);
                    return VulkanImportedTextureUploadPrepResult.Completed;
                }

                if (ShouldDeferPrepStep(drainStart, prepBudgetMilliseconds))
                    return VulkanImportedTextureUploadPrepResult.Deferred;
            }
        }
        catch (Exception ex)
        {
            if (job.Preparation is not null)
            {
                job.Preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(job.Preparation);
                job.Preparation = null;
            }

            if (IsRetryableVulkanAllocationPressure(ex.Message))
            {
                job.DeferPreparationRetry(AllocationPressureRetryDelayMilliseconds);
                RecordState(
                    request,
                    VulkanTextureUploadGenerationState.PrepDeferred,
                    $"allocation pressure deferred prep exception retryMs={AllocationPressureRetryDelayMilliseconds:F0}: {ex.Message}");
                return VulkanImportedTextureUploadPrepResult.Deferred;
            }

            RecordState(request, VulkanTextureUploadGenerationState.Failed, ex.Message);
            Interlocked.Increment(ref s_failedUploads);
            job.OnError?.Invoke(ex);
            return VulkanImportedTextureUploadPrepResult.Failed;
        }
    }

    private bool EnsureJobPreparation(
        VulkanRenderer renderer,
        VulkanImportedTextureUploadJob job,
        out VulkanImportedTextureUploadPreparation? preparation,
        out string? failureReason)
    {
        preparation = job.Preparation;
        failureReason = null;
        if (preparation is not null)
            return true;

        if (!job.Request.TryGetTexture(out XRTexture2D? texture) || texture is null)
        {
            failureReason = "texture was collected before upload preparation";
            return false;
        }

        if (renderer.GetOrCreateAPIRenderObject(texture, generateNow: false) is not VkTexture2D vkTexture)
        {
            failureReason = "Vulkan texture wrapper could not be resolved for imported texture upload.";
            return false;
        }

        job.TextureWrapper = vkTexture;
        ulong publicationToken = job.PublicationToken.HasValue
            ? unchecked((ulong)job.PublicationToken.Value)
            : AllocateDescriptorPublicationToken();
        job.PublicationToken = unchecked((long)publicationToken);

        RecordState(
            job.Request,
            VulkanTextureUploadGenerationState.PrepRunning,
            $"preparing image/staging resources token={publicationToken} queueWaitMs={job.QueueWaitMilliseconds:F3}");

        if (!vkTexture.TryCreateSynchronizedImportedUploadPreparation(
                job.Request,
                job.ResidentData,
                job.IncludeMipChain,
                publicationToken,
                job.ShouldAcceptResult,
                job.OnFinished,
                job.OnCanceled,
                job.OnError,
                out preparation,
                out failureReason)
            || preparation is null)
        {
            return false;
        }

        job.Preparation = preparation;
        return true;
    }

    private VulkanImportedTextureUploadPrepResult TryDrainWorkerPreparation(
        VulkanRenderer renderer,
        VulkanImportedTextureUploadJob job,
        VulkanImportedTextureUploadPreparation preparation)
    {
        if (job.WorkerPrepTask is null)
        {
            if (job.Request.CancellationToken.IsCancellationRequested)
            {
                preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, "worker upload preparation was canceled before scheduling");
                Interlocked.Increment(ref s_canceledStaleUploads);
                job.OnCanceled?.Invoke();
                job.Preparation = null;
                return VulkanImportedTextureUploadPrepResult.Canceled;
            }

            job.WorkerPrepTask = Task.Run(() => RunWorkerPreparation(renderer, preparation));
            return VulkanImportedTextureUploadPrepResult.Deferred;
        }

        if (!job.WorkerPrepTask.IsCompleted)
            return VulkanImportedTextureUploadPrepResult.Deferred;

        VulkanImportedTextureUploadWorkerResult workerResult;
        try
        {
            workerResult = job.WorkerPrepTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, "worker upload preparation was canceled");
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
            job.Preparation = null;
            job.WorkerPrepTask = null;
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        job.WorkerPrepTask = null;
        job.Preparation = null;
        if (workerResult.Canceled)
        {
            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, workerResult.FailureReason ?? "worker upload preparation was canceled");
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        if (workerResult.Exception is not null || workerResult.PendingUpload is null)
        {
            string reason = workerResult.Exception?.Message ?? workerResult.FailureReason ?? "worker upload preparation failed";
            if (IsRetryableVulkanAllocationPressure(reason))
            {
                job.DeferPreparationRetry(AllocationPressureRetryDelayMilliseconds);
                RecordState(
                    job.Request,
                    VulkanTextureUploadGenerationState.PrepDeferred,
                    $"worker allocation pressure deferred prep retryMs={AllocationPressureRetryDelayMilliseconds:F0}: {reason}");
                return VulkanImportedTextureUploadPrepResult.Deferred;
            }

            RecordState(job.Request, VulkanTextureUploadGenerationState.Failed, reason);
            Interlocked.Increment(ref s_failedUploads);
            job.OnError?.Invoke(workerResult.Exception ?? new InvalidOperationException(reason));
            return VulkanImportedTextureUploadPrepResult.Failed;
        }

        Volatile.Write(ref s_lastWorkerPrepMilliseconds, workerResult.PrepMilliseconds);
        TextureRuntimeDiagnostics.RecordUploadDuration(workerResult.PrepMilliseconds);
        RuntimeRenderingHostServices.Statistics.RecordRenderTextureUpload(job.Request.EstimatedBytes, TimeSpan.FromMilliseconds(workerResult.PrepMilliseconds));
        RecordState(
            job.Request,
            VulkanTextureUploadGenerationState.PrepReady,
            $"worker prepared upload token={workerResult.PendingUpload.PublicationToken} prepMs={workerResult.PrepMilliseconds:F3} stagingMips={workerResult.PendingUpload.StagingResources.Length}");
        QueuePreparedImportedTextureUpload(renderer, workerResult.PendingUpload, workerResult.PrepMilliseconds, workerPrepared: true);
        return VulkanImportedTextureUploadPrepResult.Completed;
    }

    private static VulkanImportedTextureUploadWorkerResult RunWorkerPreparation(
        VulkanRenderer renderer,
        VulkanImportedTextureUploadPreparation preparation)
    {
        long prepStart = TextureRuntimeDiagnostics.StartTiming();
        try
        {
            if (RuntimeRenderingHostServices.FrameTiming.IsRenderThread)
                throw new InvalidOperationException("Vulkan upload worker preparation must not run on the render thread or touch active frame command buffers.");

            lock (renderer.TextureUploadContextSync)
            {
                bool completed = false;
                VulkanImportedTexturePendingUpload? pendingUpload = null;
                string? failureReason = null;
                while (!completed)
                {
                    if (!preparation.ShouldAccept())
                    {
                        preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                        return new VulkanImportedTextureUploadWorkerResult(
                            null,
                            "request was canceled during worker upload preparation",
                            canceled: true,
                            TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                            null);
                    }

                    if (!preparation.Texture.TryAdvanceSynchronizedImportedUploadPreparation(
                            preparation,
                            out completed,
                            out pendingUpload,
                            out failureReason))
                    {
                        preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                        bool canceled = failureReason is not null
                            && failureReason.Contains("canceled", StringComparison.OrdinalIgnoreCase);
                        return new VulkanImportedTextureUploadWorkerResult(
                            null,
                            failureReason,
                            canceled,
                            TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                            null);
                    }
                }

                return new VulkanImportedTextureUploadWorkerResult(
                    pendingUpload,
                    null,
                    canceled: false,
                    TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                    null);
            }
        }
        catch (Exception ex)
        {
            preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
            return new VulkanImportedTextureUploadWorkerResult(
                null,
                ex.Message,
                canceled: false,
                TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                ex);
        }
    }

    private static bool ShouldDeferPrepStep(long drainStart, double prepBudgetMilliseconds)
        => prepBudgetMilliseconds > 0.0
            && TextureRuntimeDiagnostics.ElapsedMilliseconds(drainStart) >= prepBudgetMilliseconds;

    private static bool IsRetryableVulkanAllocationPressure(string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason))
            return false;

        return failureReason.Contains("Vulkan image allocation failed", StringComparison.OrdinalIgnoreCase)
            || VulkanRenderer.IsExpectedVulkanImageAllocationDeferral(failureReason)
            || failureReason.Contains("out of device memory", StringComparison.OrdinalIgnoreCase)
            || failureReason.Contains("out-of-device", StringComparison.OrdinalIgnoreCase)
            || failureReason.Contains("ErrorOutOfDeviceMemory", StringComparison.OrdinalIgnoreCase);
    }

    private void QueuePreparedImportedTextureUpload(
        VulkanRenderer renderer,
        VulkanImportedTexturePendingUpload pendingUpload,
        double prepMilliseconds,
        bool workerPrepared)
    {
        VulkanImportedTextureUploadRequest request = pendingUpload.Request;
        string? transferFailure = null;
        if (RenderDiagnosticsFlags.VkTextureUploadTransferQueue
            && renderer.TrySubmitImportedTextureUploadToTransferQueue(
                pendingUpload,
                out VulkanSubmittedImportedTextureUpload? submitted,
                out transferFailure)
            && submitted is not null)
        {
            lock (_transferQueueSync)
                _pendingTransferUploads.Add(submitted);

            Interlocked.Increment(ref s_pendingTransferSubmissions);
            Interlocked.Add(ref s_transferQueueBytesInFlight, submitted.BytesInFlight);
            RecordState(
                request,
                VulkanTextureUploadGenerationState.TransferSubmitted,
                $"submitted transfer-queue upload token={pendingUpload.PublicationToken} prepMs={prepMilliseconds:F3} workerPrep={workerPrepared}");
            EnsureTransferDrainScheduled(renderer);
            return;
        }

        if (RenderDiagnosticsFlags.VkTextureUploadTransferQueue
            && Interlocked.Exchange(ref _transferQueueCompatLogged, 1) == 0)
        {
            XREngine.Debug.Vulkan(
                "[Vulkan Compat] Imported texture upload '{0}' is using graphics-frame copy submission because transfer-queue submission was unavailable: {1}. Preferred Vulkan path is dedicated transfer queue copy plus graphics ownership acquire before descriptor publication.",
                request.TextureName ?? "<unnamed>",
                transferFailure ?? "unknown reason");
        }

        RecordState(
            request,
            VulkanTextureUploadGenerationState.GpuUploadPending,
            "queued graphics-frame texture upload op");
        renderer.EnqueueImportedTextureUpload(pendingUpload);
    }

}
