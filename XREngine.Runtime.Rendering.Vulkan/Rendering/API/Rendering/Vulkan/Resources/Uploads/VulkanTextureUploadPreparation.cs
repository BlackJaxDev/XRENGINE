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
        if (!context.IsDeviceOperational)
            return true;

        // Generic render-job pumps do not guarantee an ambient window owner.
        // Bind this frozen request to its exact live renderer generation for
        // the duration of the render-thread-affine preparation slice.
        using var currentRendererScope =
            AbstractRenderer.EnterThreadCurrentScope(context.Owner);
        bool wasActive = context.Owner.Active;
        context.Owner.Active = true;
        try
        {
            return DrainQueuedUploadPreparationForOwner(
                context,
                foregroundRequired,
                requiredManifest);
        }
        finally
        {
            context.Owner.Active = wasActive;
        }
    }

    private bool DrainQueuedUploadPreparationForOwner(
        VulkanTextureUploadSchedulingContext context,
        bool foregroundRequired,
        VulkanTextureUploadManifest? requiredManifest)
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
            double prepBudgetMilliseconds = foregroundRequired ? 0.0 : ResolvePrepBudgetMilliseconds();
            long drainStart = TextureRuntimeDiagnostics.StartTiming();
            int preparedThisDrain = 0;

            // Reap completed workers independently of the selected manifest or
            // priority. This releases bounded worker admission without allowing
            // background results to submit during a foreground-only drain.
            ObserveCompletedPreparationWorkers();

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
                // A worker retains native preparation ownership while running.
                // Always observe it before stale/device pruning so its admission
                // slot and any prepared result cannot be stranded.
                if (!job.ShouldAccept() && job.WorkerPrepTask is null && job.WorkerPrepResult is null)
                {
                    const string reason =
                        "Request became stale before Vulkan upload preparation.";
                    ReleaseUnsubmittedJobPreparation(job);
                    requiredManifest?.Fail(job.Ticket, reason);
                    RecordState(
                        job.Request,
                        VulkanTextureUploadGenerationState.Canceled,
                        reason);
                    Interlocked.Increment(ref s_canceledStaleUploads);
                    job.InvokeCanceledOnce();
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

                RenderForegroundWorkCoordinator.BackgroundSlice backgroundSlice =
                    default;
                if (!foregroundRequired &&
                    !RenderForegroundWorkCoordinator.TryEnterBackgroundSlice(
                        out backgroundSlice))
                {
                    RequeueUploadPreparation(job);
                    return false;
                }

                VulkanImportedTextureUploadPrepResult prepResult;
                try
                {
                    prepResult = TryPrepareAndEnqueueImportedTextureUpload(
                        context,
                        job,
                        drainStart,
                        prepBudgetMilliseconds,
                        requiredManifest);
                }
                finally
                {
                    backgroundSlice.Dispose();
                }
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
        if (job.PendingUpload is not null)
            return TryDrainPendingChunkPreparation(context, job, requiredManifest);

        if (job.WorkerPrepTask is not null || job.WorkerPrepResult is not null)
        {
            if (job.Preparation is null)
                throw new InvalidOperationException("A Vulkan upload preparation worker lost its retained preparation ownership.");

            return TryDrainWorkerPreparation(context, job, job.Preparation, requiredManifest);
        }

        if (!context.IsDeviceOperational)
        {
            const string reason =
                "Vulkan device was lost before upload preparation.";
            ReleaseUnsubmittedJobPreparation(job);
            requiredManifest?.Fail(job.Ticket, reason);
            RecordState(request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.InvokeCanceledOnce();
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        if (!job.ShouldAccept())
        {
            const string reason = "Stale or canceled before upload preparation.";
            ReleaseUnsubmittedJobPreparation(job);
            requiredManifest?.Fail(job.Ticket, reason);
            RecordState(request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.InvokeCanceledOnce();
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

            // Imported uploads always advance native preparation on a worker. The
            // render owner only creates the lightweight wrapper/envelope and later
            // submits the completed pending upload.
            return TryDrainWorkerPreparation(context, job, preparation, requiredManifest);
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

        if (!context.IsOwnerCurrent)
        {
            failureReason =
                "The owning Vulkan renderer is not current for imported texture upload preparation.";
            return false;
        }

        // WrapperLookup intentionally observes only already-published wrappers.
        // This render-owner-only cold boundary creates/publishes the initial
        // identity wrapper before worker preparation begins; workers never call
        // this factory.
        using var creationOwner = GenericRenderObject.PushApiWrapperCreationOwner(context.Owner);
        if (context.Resources.CreateAPIRenderObject(texture) is not VkTexture2D vkTexture)
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
        if (!TryObserveCompletedPreparationWorker(job) && job.WorkerPrepTask is not null)
            return VulkanImportedTextureUploadPrepResult.Deferred;

        if (job.WorkerPrepResult is null)
        {
            if (job.Request.CancellationToken.IsCancellationRequested)
            {
                preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                const string reason =
                    "Worker upload preparation was canceled before scheduling.";
                requiredManifest?.Fail(job.Ticket, reason);
                RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
                Interlocked.Increment(ref s_canceledStaleUploads);
                job.InvokeCanceledOnce();
                job.Preparation = null;
                return VulkanImportedTextureUploadPrepResult.Canceled;
            }

            if (!TryStartPreparationWorker(context, job, preparation))
                return VulkanImportedTextureUploadPrepResult.Deferred;
            return VulkanImportedTextureUploadPrepResult.Deferred;
        }

        VulkanImportedTextureUploadWorkerResult workerResult = job.WorkerPrepResult;
        job.WorkerPrepResult = null;
        if (workerResult.Yielded)
        {
            // Initial preparation can finish destination creation before a
            // bounded staging lease is admitted.  In that case the worker
            // result owns the completed pending upload, while Complete has
            // already moved its image/view/memory handles out of
            // <paramref name="preparation"/>.  Retain that exact owner for
            // the next chunk worker; retrying the now-empty preparation would
            // construct a second pending upload with null native handles.
            if (workerResult.PendingUpload is { } pendingUpload)
            {
                job.Preparation = null;
                job.PendingUpload = AssignPreparedUploadOwner(job, pendingUpload);
            }
            Interlocked.Increment(ref s_workerPreparationYields);
            return VulkanImportedTextureUploadPrepResult.Deferred;
        }

        job.Preparation = null;
        if (workerResult.Canceled)
        {
            string reason = workerResult.FailureReason ??
                "Worker upload preparation was canceled.";
            requiredManifest?.Fail(job.Ticket, reason);
            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.InvokeCanceledOnce();
            Interlocked.Increment(ref s_workerPreparationCancels);
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
            job.InvokeErrorOnce(workerResult.Exception ?? new InvalidOperationException(reason));
            return VulkanImportedTextureUploadPrepResult.Failed;
        }

        if (Volatile.Read(ref _preparationRetirementStarted) != 0 ||
            !context.IsDeviceOperational ||
            !job.ShouldAccept())
        {
            const string reason = "Worker prepared upload became stale or the Vulkan device retired before transfer submission.";
            workerResult.PendingUpload.Texture.ReleasePreparedImportedUploadResources(workerResult.PendingUpload);
            requiredManifest?.Fail(job.Ticket, reason);
            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            Interlocked.Increment(ref s_workerPreparationCancels);
            job.InvokeCanceledOnce();
            return VulkanImportedTextureUploadPrepResult.Canceled;
        }

        Interlocked.Increment(ref s_workerPreparationCompletions);
        Interlocked.Increment(ref s_chunksPrepared);
        Interlocked.Add(ref s_chunkBytesPrepared, (long)workerResult.PendingUpload.StagingResources[0].SizeBytes);
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
            AssignPreparedUploadOwner(job, workerResult.PendingUpload),
            workerResult.PrepMilliseconds,
            workerPrepared: true,
            requiredManifest)
                ? VulkanImportedTextureUploadPrepResult.Completed
                : VulkanImportedTextureUploadPrepResult.Failed;
    }

    private static VulkanImportedTexturePendingUpload AssignPreparedUploadOwner(
        VulkanImportedTextureUploadJob job,
        VulkanImportedTexturePendingUpload upload)
    {
        upload.OwnerJob = job;
        return upload;
    }

    private VulkanImportedTextureUploadPrepResult TryDrainPendingChunkPreparation(
        VulkanTextureUploadSchedulingContext context,
        VulkanImportedTextureUploadJob job,
        VulkanTextureUploadManifest? requiredManifest)
    {
        VulkanImportedTexturePendingUpload upload = job.PendingUpload!;
        if (!TryObserveCompletedPreparationWorker(job) && job.WorkerPrepTask is not null)
            return VulkanImportedTextureUploadPrepResult.Deferred;

        if (job.WorkerPrepResult is null)
        {
            if (!job.ShouldAccept())
            {
                upload.Texture.ReleasePreparedImportedUploadResources(upload);
                job.PendingUpload = null;
                job.InvokeCanceledOnce();
                return VulkanImportedTextureUploadPrepResult.Canceled;
            }

            if (!TryStartPendingChunkWorker(context, job, upload))
                return VulkanImportedTextureUploadPrepResult.Deferred;
            return VulkanImportedTextureUploadPrepResult.Deferred;
        }

        VulkanImportedTextureUploadWorkerResult result = job.WorkerPrepResult;
        job.WorkerPrepResult = null;
        if (result.Yielded)
            return VulkanImportedTextureUploadPrepResult.Deferred;
        if (result.PendingUpload is null || result.Exception is not null || result.Canceled)
        {
            string reason = result.Exception?.Message ?? result.FailureReason ?? "worker texture chunk preparation failed";
            upload.Texture.ReleasePreparedImportedUploadResources(upload);
            job.PendingUpload = null;
            requiredManifest?.Fail(job.Ticket, reason);
            RecordState(job.Request, result.Canceled ? VulkanTextureUploadGenerationState.Canceled : VulkanTextureUploadGenerationState.Failed, reason);
            if (result.Canceled)
                job.InvokeCanceledOnce();
            else
                job.InvokeErrorOnce(result.Exception ?? new InvalidOperationException(reason));
            return result.Canceled ? VulkanImportedTextureUploadPrepResult.Canceled : VulkanImportedTextureUploadPrepResult.Failed;
        }

        job.PendingUpload = null;
        Interlocked.Increment(ref s_workerPreparationCompletions);
        Interlocked.Increment(ref s_chunksPrepared);
        Interlocked.Add(ref s_chunkBytesPrepared, (long)result.PendingUpload.StagingResources[0].SizeBytes);
        Volatile.Write(ref s_lastWorkerPrepMilliseconds, result.PrepMilliseconds);
        return QueuePreparedImportedTextureUpload(
            context,
            AssignPreparedUploadOwner(job, result.PendingUpload),
            result.PrepMilliseconds,
            workerPrepared: true,
            requiredManifest)
                ? VulkanImportedTextureUploadPrepResult.Completed
                : VulkanImportedTextureUploadPrepResult.Failed;
    }

    private void ObserveCompletedPreparationWorkers()
    {
        VulkanImportedTextureUploadJob? first = null;
        VulkanImportedTextureUploadJob? second = null;
        using (VulkanFrameLockScope.Enter(
                   _prepQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            if (_inFlightPreparationWorkers.Count > 0)
                first = _inFlightPreparationWorkers[0];
            if (_inFlightPreparationWorkers.Count > 1)
                second = _inFlightPreparationWorkers[1];
        }

        if (first is not null)
            _ = TryObserveCompletedPreparationWorker(first);
        if (second is not null)
            _ = TryObserveCompletedPreparationWorker(second);
    }

    private bool TryObserveCompletedPreparationWorker(VulkanImportedTextureUploadJob job)
    {
        if (job.WorkerPrepResult is not null)
            return true;

        Task<VulkanImportedTextureUploadWorkerResult>? task = job.WorkerPrepTask;
        if (task is null || !task.IsCompleted)
            return false;

        VulkanImportedTextureUploadWorkerResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            result = new VulkanImportedTextureUploadWorkerResult(
                null, "Worker upload preparation was canceled.",
                canceled: true, yielded: false, prepMilliseconds: 0.0, exception: null);
        }
        catch (Exception exception)
        {
            result = new VulkanImportedTextureUploadWorkerResult(
                null, exception.Message,
                canceled: false, yielded: false, prepMilliseconds: 0.0, exception);
        }

        job.WorkerPrepTask = null;
        job.WorkerPrepResult = result;
        RemoveInFlightPreparationWorker(job);
        return true;
    }

    private static void ReleaseUnsubmittedJobPreparation(VulkanImportedTextureUploadJob job)
    {
        // A resumed multi-chunk ticket owns its destination image through
        // PendingUpload, not through Preparation. This helper is only called
        // after the job has no worker task or submitted batch, so terminal
        // cleanup may release that ownership here.
        if (job.PendingUpload is { } pendingUpload)
        {
            job.PendingUpload = null;
            pendingUpload.Texture.ReleasePreparedImportedUploadResources(pendingUpload);
        }

        if (job.Preparation is { } preparation)
        {
            job.Preparation = null;
            preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
        }
    }

    private bool TryStartPreparationWorker(
        VulkanTextureUploadSchedulingContext context,
        VulkanImportedTextureUploadJob job,
        VulkanImportedTextureUploadPreparation preparation)
    {
        using (VulkanFrameLockScope.Enter(
                   _prepQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            if (Volatile.Read(ref _preparationRetirementStarted) != 0 ||
                _inFlightPreparationWorkers.Count >= MaxInFlightPreparationWorkers)
            {
                return false;
            }

            _inFlightPreparationWorkers.Add(job);
            Interlocked.Increment(ref s_ownedWorkerPreparationJobs);
            try
            {
                job.WorkerPrepTask = StartPreparationWorker(() =>
                {
                    Interlocked.Increment(ref s_activePrepPackages);
                    try
                    {
                        return RunWorkerPreparation(context, job, preparation);
                    }
                    finally
                    {
                        int active = Interlocked.Decrement(ref s_activePrepPackages);
                        if (active < 0)
                            Interlocked.Exchange(ref s_activePrepPackages, 0);
                    }
                });
                Interlocked.Increment(ref s_workerPreparationStarts);
                return true;
            }
            catch
            {
                _inFlightPreparationWorkers.Remove(job);
                Interlocked.Decrement(ref s_ownedWorkerPreparationJobs);
                throw;
            }
        }
    }

    private bool TryStartPendingChunkWorker(
        VulkanTextureUploadSchedulingContext context,
        VulkanImportedTextureUploadJob job,
        VulkanImportedTexturePendingUpload upload)
    {
        using (VulkanFrameLockScope.Enter(
                   _prepQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            if (Volatile.Read(ref _preparationRetirementStarted) != 0 ||
                _inFlightPreparationWorkers.Count >= MaxInFlightPreparationWorkers)
            {
                return false;
            }

            _inFlightPreparationWorkers.Add(job);
            Interlocked.Increment(ref s_ownedWorkerPreparationJobs);
            try
            {
                job.WorkerPrepTask = StartPreparationWorker(() =>
                {
                    Interlocked.Increment(ref s_activePrepPackages);
                    try
                    {
                        return RunWorkerChunkPreparation(context, job, upload);
                    }
                    finally
                    {
                        int active = Interlocked.Decrement(ref s_activePrepPackages);
                        if (active < 0)
                            Interlocked.Exchange(ref s_activePrepPackages, 0);
                    }
                });
                Interlocked.Increment(ref s_workerPreparationStarts);
                return true;
            }
            catch
            {
                _inFlightPreparationWorkers.Remove(job);
                Interlocked.Decrement(ref s_ownedWorkerPreparationJobs);
                throw;
            }
        }
    }

    /// <summary>
    /// Preparation workers use a dedicated, already-bounded lane so queued
    /// background tasks cannot reserve every admission slot while waiting for
    /// a throttled shared-pool thread. Background jobs promptly yield when
    /// PresentNow owns the foreground epoch, releasing a slot for visible work.
    /// </summary>
    private static Task<VulkanImportedTextureUploadWorkerResult> StartPreparationWorker(
        Func<VulkanImportedTextureUploadWorkerResult> worker)
        => Task.Factory.StartNew(
            worker,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private void RemoveInFlightPreparationWorker(VulkanImportedTextureUploadJob job)
    {
        using VulkanFrameLockScope scope = VulkanFrameLockScope.Enter(
            _prepQueueSync,
            EVulkanFrameWaitReason.UploadLock);
        if (_inFlightPreparationWorkers.Remove(job))
            Interlocked.Decrement(ref s_ownedWorkerPreparationJobs);
    }

    private VulkanImportedTextureUploadWorkerResult RunWorkerPreparation(
        VulkanTextureUploadSchedulingContext context,
        VulkanImportedTextureUploadJob job,
        VulkanImportedTextureUploadPreparation preparation)
    {
        long prepStart = TextureRuntimeDiagnostics.StartTiming();
        try
        {
            if (RuntimeRenderingHostServices.FrameTiming.IsRenderThread)
                throw new InvalidOperationException("Vulkan upload worker preparation must not run on the render thread or touch active frame command buffers.");

            bool completed = false;
            VulkanImportedTexturePendingUpload? pendingUpload = null;
            string? failureReason = null;
            while (!completed)
            {
                RenderForegroundWorkCoordinator.BackgroundSlice backgroundSlice =
                    default;
                if (Volatile.Read(ref _preparationRetirementStarted) != 0)
                {
                    preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                    return new VulkanImportedTextureUploadWorkerResult(
                        null, "Vulkan upload preparation admission closed for renderer retirement.",
                        canceled: true, yielded: false,
                        TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart), null);
                }

                if (!job.IsForegroundRequired &&
                    !RenderForegroundWorkCoordinator.TryEnterBackgroundSlice(
                        out backgroundSlice))
                {
                    return new VulkanImportedTextureUploadWorkerResult(
                        null, null, canceled: false, yielded: true,
                        TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart), null);
                }

                try
                {
                    using (VulkanFrameLockScope.Enter(
                               context.Resources.TextureUploadContextSync,
                               EVulkanFrameWaitReason.UploadLock))
                    {
                    if (Volatile.Read(ref _preparationRetirementStarted) != 0 ||
                        !context.IsDeviceOperational ||
                        !preparation.ShouldAccept())
                    {
                        preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
                        return new VulkanImportedTextureUploadWorkerResult(
                            null,
                            "request was canceled during worker upload preparation",
                            canceled: true, yielded: false,
                            TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                            null);
                    }

                    // Native timing inventory is cold worker preparation too;
                    // admitting the first upload must not allocate on the render thread.
                    EnsureTransferGpuTimingPools(context);
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
                            canceled, yielded: false,
                            TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                            null);
                    }
                    }
                }
                finally
                {
                    backgroundSlice.Dispose();
                }
            }

            if (pendingUpload is null)
                return new VulkanImportedTextureUploadWorkerResult(
                    null,
                    "worker preparation completed without an imported texture ticket",
                    canceled: false, yielded: false,
                    TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart), null);

            // Destination creation and source copying are both worker-only.  Do
            // not eagerly walk every mip here: the ticket retains decoded bytes
            // and this worker owns exactly one bounded staging chunk.
            using (VulkanFrameLockScope.Enter(
                       context.Resources.TextureUploadContextSync,
                       EVulkanFrameWaitReason.UploadLock))
            {
                EVulkanImportedTextureChunkPreparation chunkResult =
                    pendingUpload.Texture.TryPrepareNextSynchronizedImportedUploadChunk(
                        pendingUpload,
                        job.IsForegroundRequired,
                        out failureReason);
                if (chunkResult != EVulkanImportedTextureChunkPreparation.Prepared)
                {
                    if (chunkResult == EVulkanImportedTextureChunkPreparation.Deferred)
                    {
                        RecordStagingAdmissionDeferred();
                        return new VulkanImportedTextureUploadWorkerResult(
                            pendingUpload,
                            null,
                            canceled: false,
                            yielded: true,
                            TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                            null);
                    }
                    pendingUpload.Texture.ReleasePreparedImportedUploadResources(pendingUpload);
                    return new VulkanImportedTextureUploadWorkerResult(
                        null,
                        failureReason,
                        canceled: false, yielded: false,
                        TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart), null);
                }
            }

            return new VulkanImportedTextureUploadWorkerResult(
                pendingUpload,
                null,
                canceled: false, yielded: false,
                TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                null);
        }
        catch (Exception ex)
        {
            preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);
            return new VulkanImportedTextureUploadWorkerResult(
                null,
                ex.Message,
                canceled: false, yielded: false,
                TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                ex);
        }
    }

    private VulkanImportedTextureUploadWorkerResult RunWorkerChunkPreparation(
        VulkanTextureUploadSchedulingContext context,
        VulkanImportedTextureUploadJob job,
        VulkanImportedTexturePendingUpload upload)
    {
        long prepStart = TextureRuntimeDiagnostics.StartTiming();
        try
        {
            if (RuntimeRenderingHostServices.FrameTiming.IsRenderThread)
                throw new InvalidOperationException("Vulkan upload worker chunk preparation must not run on the render thread.");
            if (Volatile.Read(ref _preparationRetirementStarted) != 0 ||
                !context.IsDeviceOperational ||
                !upload.ShouldPublish())
            {
                return new VulkanImportedTextureUploadWorkerResult(
                    null, "request was canceled before worker chunk preparation",
                    canceled: true, yielded: false,
                    TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart), null);
            }

            RenderForegroundWorkCoordinator.BackgroundSlice backgroundSlice = default;
            if (!job.IsForegroundRequired &&
                !RenderForegroundWorkCoordinator.TryEnterBackgroundSlice(out backgroundSlice))
            {
                return new VulkanImportedTextureUploadWorkerResult(
                    null, null, canceled: false, yielded: true,
                    TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart), null);
            }

            try
            {
                using (VulkanFrameLockScope.Enter(
                           context.Resources.TextureUploadContextSync,
                           EVulkanFrameWaitReason.UploadLock))
                {
                    EVulkanImportedTextureChunkPreparation chunkResult =
                        upload.Texture.TryPrepareNextSynchronizedImportedUploadChunk(
                            upload, job.IsForegroundRequired, out string? failureReason);
                    if (chunkResult != EVulkanImportedTextureChunkPreparation.Prepared)
                    {
                        if (chunkResult == EVulkanImportedTextureChunkPreparation.Deferred)
                        {
                            RecordStagingAdmissionDeferred();
                            return new VulkanImportedTextureUploadWorkerResult(
                                upload,
                                null,
                                canceled: false,
                                yielded: true,
                                TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart),
                                null);
                        }
                        return new VulkanImportedTextureUploadWorkerResult(
                            null, failureReason, canceled: false, yielded: false,
                            TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart), null);
                    }
                }
            }
            finally
            {
                backgroundSlice.Dispose();
            }

            return new VulkanImportedTextureUploadWorkerResult(
                upload, null, canceled: false, yielded: false,
                TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart), null);
        }
        catch (Exception exception)
        {
            return new VulkanImportedTextureUploadWorkerResult(
                null, exception.Message, canceled: false, yielded: false,
                TextureRuntimeDiagnostics.ElapsedMilliseconds(prepStart), exception);
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
        using (VulkanFrameLockScope.Enter(_transferQueueSync, EVulkanFrameWaitReason.UploadLock))
            _readyTransferUploads.Add(pendingUpload);
        Interlocked.Increment(ref s_readyTransferChunks);
        Interlocked.Add(ref s_readyTransferBytes, (long)pendingUpload.StagingResources[0].SizeBytes);
        RecordState(request, VulkanTextureUploadGenerationState.PrepReady,
            $"chunk ready bytes={pendingUpload.StagingResources[0].SizeBytes} prepMs={prepMilliseconds:F3} workerPrep={workerPrepared}");
        EnsureTransferDrainScheduled(context);
        return true;
    }

}
