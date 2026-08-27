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
    private void EnsurePrepDrainScheduled(VulkanTextureUploadSchedulingContext context)
    {
        if (Interlocked.CompareExchange(ref _prepDrainScheduled, 1, 0) != 0)
            return;

        RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadCoroutine(
            () => DrainQueuedUploadPreparation(context),
            "VulkanTextureUploadService.DrainUploadPrepQueue",
            RenderThreadJobKind.TextureUpload);
    }

    private bool DrainQueuedUploadPreparation(VulkanTextureUploadSchedulingContext context)
        => DrainQueuedUploadPreparation(context, foregroundRequired: false);

    /// <summary>
    /// Drains visible-now texture preparation without applying the background
    /// streaming count/time caps. This is a CPU readiness barrier only: transfer
    /// submission and completion remain ordered through the normal Vulkan timeline.
    /// </summary>
    internal bool DrainRequiredUploadPreparation(
        VulkanTextureUploadSchedulingContext context,
        VulkanTextureUploadManifest manifest)
        => DrainQueuedUploadPreparation(context, foregroundRequired: true, manifest);

    private bool DrainQueuedUploadPreparation(
        VulkanTextureUploadSchedulingContext context,
        bool foregroundRequired,
        VulkanTextureUploadManifest? requiredManifest = null)
    {
        if (Interlocked.Increment(ref _activePreparationDrainCount) == 1)
            _preparationDrainsIdle.Reset();
        try
        {
            if (Volatile.Read(ref _preparationRetirementStarted) != 0)
            {
                requiredManifest?.FailUnresolved(
                    "Vulkan upload preparation admission closed for renderer retirement.");
                Interlocked.Exchange(ref _prepDrainScheduled, 0);
                return true;
            }
            if (!context.IsDeviceOperational)
            {
                const string reason =
                    "Vulkan device was lost before upload preparation.";
                requiredManifest?.FailUnresolved(reason);
                CancelQueuedPreparation(reason);
                Interlocked.Exchange(ref _prepDrainScheduled, 0);
                return true;
            }

            double prepBudgetMilliseconds = foregroundRequired ? 0.0 : ResolvePrepBudgetMilliseconds();
            long drainStart = TextureRuntimeDiagnostics.StartTiming();
            int preparedThisDrain = 0;

            while (TryDequeueBestPrepJob(out VulkanImportedTextureUploadJob job, foregroundRequired, requiredManifest))
            {
                if (Volatile.Read(ref _preparationRetirementStarted) != 0)
                {
                    requiredManifest?.Fail(
                        job.Ticket,
                        "Vulkan upload preparation admission closed for renderer retirement.");
                    RequeueUploadPreparation(job);
                    Interlocked.Exchange(ref _prepDrainScheduled, 0);
                    return true;
                }
                if (!job.ShouldAccept())
                {
                    const string reason =
                        "Request became stale before Vulkan upload preparation.";
                    requiredManifest?.Fail(job.Ticket, reason);
                    RecordState(
                        job.Request,
                        VulkanTextureUploadGenerationState.Canceled,
                        reason);
                    Interlocked.Increment(ref s_canceledStaleUploads);
                    job.OnCanceled?.Invoke();
                    continue;
                }

                if (!CanPrepareJobThisFrame(job, preparedThisDrain, drainStart, prepBudgetMilliseconds, foregroundRequired))
                {
                    RequeueUploadPreparation(job);
                    RecordState(
                        job.Request,
                        VulkanTextureUploadGenerationState.PrepDeferred,
                        $"budget deferred prep budgetMs={prepBudgetMilliseconds:F3} queueWaitMs={job.QueueWaitMilliseconds:F3}");
                    return false;
                }

                VulkanImportedTextureUploadPrepResult prepResult = TryPrepareAndEnqueueImportedTextureUpload(
                    context,
                    job,
                    drainStart,
                    prepBudgetMilliseconds,
                    requiredManifest);
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

                if (ShouldYieldAfterPreparation(preparedThisDrain, drainStart, prepBudgetMilliseconds, foregroundRequired))
                    return HasQueuedPrepWorkOrCompleteDrain(requiredManifest);
            }

            return HasQueuedPrepWorkOrCompleteDrain(requiredManifest);
        }
        finally
        {
            if (Interlocked.Decrement(ref _activePreparationDrainCount) == 0)
                _preparationDrainsIdle.Set();
        }
    }

    private VulkanImportedTextureUploadPrepResult TryPrepareAndEnqueueImportedTextureUpload(
        VulkanTextureUploadSchedulingContext context,
        VulkanImportedTextureUploadJob job,
        long drainStart,
        double prepBudgetMilliseconds,
        VulkanTextureUploadManifest? requiredManifest)
    {
        VulkanImportedTextureUploadRequest request = job.Request;
        if (!context.IsDeviceOperational)
        {
            const string reason =
                "Vulkan device was lost before upload preparation.";
            requiredManifest?.Fail(job.Ticket, reason);
            RecordState(request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        if (!job.ShouldAccept())
        {
            const string reason = "Stale or canceled before upload preparation.";
            requiredManifest?.Fail(job.Ticket, reason);
            RecordState(request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        try
        {
            if (!EnsureJobPreparation(context, job, out VulkanImportedTextureUploadPreparation? preparation, out string? failureReason)
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
                string terminalReason = failureReason ??
                    "Failed to initialize Vulkan upload preparation.";
                requiredManifest?.Fail(job.Ticket, terminalReason);
                RecordState(
                    request,
                    canceled ? VulkanTextureUploadGenerationState.Canceled : VulkanTextureUploadGenerationState.Failed,
                    terminalReason);
                if (canceled)
                {
                    Interlocked.Increment(ref s_canceledStaleUploads);
                    job.OnCanceled?.Invoke();
                    return VulkanImportedTextureUploadPrepResult.Canceled;
                }

                Interlocked.Increment(ref s_failedUploads);
                job.OnError?.Invoke(new InvalidOperationException(terminalReason));
                return VulkanImportedTextureUploadPrepResult.Failed;
            }

            if (RenderDiagnosticsFlags.VkTextureUploadPrepWorker)
                return TryDrainWorkerPreparation(
                    context,
                    job,
                    preparation,
                    requiredManifest);

            while (true)
            {
                if (!job.ShouldAccept())
                {
                    preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                    job.Preparation = null;
                    const string reason =
                        "Stale or canceled during Vulkan upload preparation.";
                    requiredManifest?.Fail(job.Ticket, reason);
                    RecordState(request, VulkanTextureUploadGenerationState.Canceled, reason);
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
                    string terminalReason = stepFailure ??
                        "Failed to prepare Vulkan upload resources.";
                    requiredManifest?.Fail(job.Ticket, terminalReason);
                    RecordState(
                        request,
                        canceled ? VulkanTextureUploadGenerationState.Canceled : VulkanTextureUploadGenerationState.Failed,
                        terminalReason);
                    if (canceled)
                    {
                        Interlocked.Increment(ref s_canceledStaleUploads);
                        job.OnCanceled?.Invoke();
                        return VulkanImportedTextureUploadPrepResult.Canceled;
                    }

                    Interlocked.Increment(ref s_failedUploads);
                    job.OnError?.Invoke(new InvalidOperationException(terminalReason));
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
                    _ = requiredManifest?.MarkCpuPrepared(job.Ticket);
                    return QueuePreparedImportedTextureUpload(
                        context,
                        pendingUpload,
                        prepMilliseconds,
                        workerPrepared: false,
                        requiredManifest)
                            ? VulkanImportedTextureUploadPrepResult.Completed
                            : VulkanImportedTextureUploadPrepResult.Failed;
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
            requiredManifest?.Fail(job.Ticket, ex.Message);
            Interlocked.Increment(ref s_failedUploads);
            job.OnError?.Invoke(ex);
            return VulkanImportedTextureUploadPrepResult.Failed;
        }
    }

    private bool EnsureJobPreparation(
        VulkanTextureUploadSchedulingContext context,
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

        if (context.Resources.WrapperLookup.GetOrCreate(texture, generateNow: false) is not VkTexture2D vkTexture)
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
                job.Ticket,
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
        VulkanTextureUploadSchedulingContext context,
        VulkanImportedTextureUploadJob job,
        VulkanImportedTextureUploadPreparation preparation,
        VulkanTextureUploadManifest? requiredManifest)
    {
        if (job.WorkerPrepTask is null)
        {
            if (job.Request.CancellationToken.IsCancellationRequested)
            {
                preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                const string reason =
                    "Worker upload preparation was canceled before scheduling.";
                requiredManifest?.Fail(job.Ticket, reason);
                RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
                Interlocked.Increment(ref s_canceledStaleUploads);
                job.OnCanceled?.Invoke();
                job.Preparation = null;
                return VulkanImportedTextureUploadPrepResult.Canceled;
            }

            job.WorkerPrepTask = Task.Run(() => RunWorkerPreparation(context, preparation));
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
            const string reason = "Worker upload preparation was canceled.";
            requiredManifest?.Fail(job.Ticket, reason);
            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
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
            string reason = workerResult.FailureReason ??
                "Worker upload preparation was canceled.";
            requiredManifest?.Fail(job.Ticket, reason);
            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
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
            requiredManifest?.Fail(job.Ticket, reason);
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
        _ = requiredManifest?.MarkCpuPrepared(job.Ticket);
        return QueuePreparedImportedTextureUpload(
            context,
            workerResult.PendingUpload,
            workerResult.PrepMilliseconds,
            workerPrepared: true,
            requiredManifest)
                ? VulkanImportedTextureUploadPrepResult.Completed
                : VulkanImportedTextureUploadPrepResult.Failed;
    }

    private static VulkanImportedTextureUploadWorkerResult RunWorkerPreparation(
        VulkanTextureUploadSchedulingContext context,
        VulkanImportedTextureUploadPreparation preparation)
    {
        long prepStart = TextureRuntimeDiagnostics.StartTiming();
        try
        {
            if (RuntimeRenderingHostServices.FrameTiming.IsRenderThread)
                throw new InvalidOperationException("Vulkan upload worker preparation must not run on the render thread or touch active frame command buffers.");

            lock (context.Resources.TextureUploadContextSync)
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
            || failureReason.Contains("out of device memory", StringComparison.OrdinalIgnoreCase)
            || failureReason.Contains("out-of-device", StringComparison.OrdinalIgnoreCase)
            || failureReason.Contains("ErrorOutOfDeviceMemory", StringComparison.OrdinalIgnoreCase);
    }

    private bool QueuePreparedImportedTextureUpload(
        VulkanTextureUploadSchedulingContext context,
        VulkanImportedTexturePendingUpload pendingUpload,
        double prepMilliseconds,
        bool workerPrepared,
        VulkanTextureUploadManifest? requiredManifest)
    {
        VulkanImportedTextureUploadRequest request = pendingUpload.Request;
        string? transferFailure = null;
        VulkanSubmittedImportedTextureUpload? submitted = null;
        bool foregroundRequired =
            request.PriorityClass == TextureUploadPriorityClass.VisibleNow ||
            requiredManifest?.Contains(pendingUpload.Ticket) == true;
        // Every prepared streaming generation receives a real queue submission
        // immediately. Parking it as a future frame operation creates a cycle:
        // PresentNow can discover and wait on the generation before the frame
        // drain that would ever record that operation.
        {
            lock (_transferQueueSync)
            {
                _pendingTransferUploads.EnsureCapacity(
                    _pendingTransferUploads.Count +
                    _pendingTransferReservations + 1);
                _pendingTransferReservations++;
            }
            try
            {
                _ = context.Commands.TrySubmitImportedTextureUploadToGraphicsQueue(
                    pendingUpload,
                    out submitted,
                    out transferFailure);
            }
            finally
            {
                lock (_transferQueueSync)
                {
                    _pendingTransferReservations--;
                    if (submitted is not null)
                        _pendingTransferUploads.Add(submitted);
                }
            }
        }

        if (submitted is not null)
        {
            _ = requiredManifest?.MarkGpuSubmitted(pendingUpload.Ticket);
            Interlocked.Increment(ref s_pendingTransferSubmissions);
            Interlocked.Add(ref s_transferQueueBytesInFlight, submitted.BytesInFlight);
            RecordState(
                request,
                VulkanTextureUploadGenerationState.TransferSubmitted,
                $"submitted transfer-queue upload token={pendingUpload.PublicationToken} prepMs={prepMilliseconds:F3} workerPrep={workerPrepared}");
            EnsureTransferDrainScheduled(context);
            return true;
        }

        string reason = foregroundRequired
                ? "Required accepted-frame texture upload could not obtain a " +
              "graphics-queue foreground submission: " +
              (transferFailure ?? "unknown submission failure")
            : "Background texture upload could not obtain an immediate " +
              "graphics-queue submission: " +
              (transferFailure ?? "unknown submission failure");
        requiredManifest?.Fail(pendingUpload.Ticket, reason);
        pendingUpload.Texture.ReleasePreparedImportedUploadResources(
            pendingUpload);
        RecordState(
            request,
            VulkanTextureUploadGenerationState.Failed,
            reason);
        Interlocked.Increment(ref s_failedUploads);
        InvokeTextureUploadError(
            pendingUpload,
            new InvalidOperationException(reason));
        return false;
    }

}
