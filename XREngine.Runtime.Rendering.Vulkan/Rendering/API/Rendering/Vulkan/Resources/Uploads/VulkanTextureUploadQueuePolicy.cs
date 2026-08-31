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
    private bool TryDequeueBestPrepJob(
        out VulkanImportedTextureUploadJob job,
        bool requiredOnly = false,
        VulkanTextureUploadManifest? requiredManifest = null)
    {
        using (VulkanFrameLockScope.Enter(
                   _prepQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            if (_pendingPrepJobs.Count == 0)
            {
                job = null!;
                RenderWorkBudgetCoordinator.RecordTextureQueue(0, 0.0);
                return false;
            }

            long now = Stopwatch.GetTimestamp();
            int bestIndex = -1;
            VulkanImportedTextureUploadJob? best = null;
            int bestRank = int.MinValue;
            for (int i = 0; i < _pendingPrepJobs.Count; i++)
            {
                VulkanImportedTextureUploadJob candidate = _pendingPrepJobs[i];
                if (requiredOnly)
                {
                    bool belongsToRequiredClosure = requiredManifest is not null
                        ? requiredManifest.Contains(candidate.Ticket)
                        : candidate.Request.PriorityClass ==
                            TextureUploadPriorityClass.VisibleNow;
                    if (!belongsToRequiredClosure)
                        continue;
                }
                if (candidate.NotBeforeTimestamp > now)
                    continue;

                int candidateRank = GetPriorityRank(candidate.Request.PriorityClass);
                if (best is null
                    || candidateRank > bestRank
                    || (candidateRank == bestRank && candidate.Sequence < best.Sequence))
                {
                    bestIndex = i;
                    best = candidate;
                    bestRank = candidateRank;
                }
            }

            if (best is null || bestIndex < 0)
            {
                job = null!;
                RenderWorkBudgetCoordinator.RecordTextureQueue(
                    _pendingPrepJobs.Count,
                    GetOldestQueueWaitMillisecondsNoLock());
                Volatile.Write(ref s_pendingVulkanPrepPackages, _pendingPrepJobs.Count);
                return false;
            }

            _pendingPrepJobs.RemoveAt(bestIndex);
            if (requiredOnly)
                best.PromoteToForeground();
            job = best;
            RenderWorkBudgetCoordinator.RecordTextureQueue(
                _pendingPrepJobs.Count,
                GetOldestQueueWaitMillisecondsNoLock());
            Volatile.Write(ref s_pendingVulkanPrepPackages, _pendingPrepJobs.Count);
            return true;
        }
    }

    private void RequeueUploadPreparation(VulkanImportedTextureUploadJob job)
    {
        using (VulkanFrameLockScope.Enter(
                   _prepQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            _pendingPrepJobs.Add(job);
            RenderWorkBudgetCoordinator.RecordTextureQueue(
                _pendingPrepJobs.Count,
                GetOldestQueueWaitMillisecondsNoLock());
            Volatile.Write(ref s_pendingVulkanPrepPackages, _pendingPrepJobs.Count);
        }
    }

    /// <summary>
    /// Closes CPU preparation admission and proves that no preparation worker can
    /// access Vulkan staging resources after this method returns. Submitted GPU
    /// transfers deliberately remain owned until device idle is established.
    /// </summary>
    internal void QuiescePreparationForRetirement(string reason, TimeSpan timeout)
    {
        Interlocked.Exchange(ref _preparationRetirementStarted, 1);
        if (!_preparationDrainsIdle.Wait(timeout))
            throw new TimeoutException("Timed out waiting for Vulkan texture preparation drains during backend retirement.");

        VulkanImportedTextureUploadJob[] queuedJobs;
        VulkanImportedTextureUploadJob[] workerJobs;
        using (VulkanFrameLockScope.Enter(
                   _prepQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            queuedJobs = [.. _pendingPrepJobs];
            workerJobs = [.. _inFlightPreparationWorkers];
        }
        HashSet<VulkanImportedTextureUploadJob> workerJobSet = [.. workerJobs];

        long deadline = Stopwatch.GetTimestamp() +
            (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        for (int index = 0; index < workerJobs.Length; index++)
        {
            Task? workerTask = workerJobs[index].WorkerPrepTask;
            if (workerTask is null || workerTask.IsCompleted)
                continue;

            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            bool completed = false;
            if (remainingTicks > 0)
            {
                try
                {
                    completed = workerTask.Wait(
                        TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency));
                }
                catch (AggregateException) when (workerTask.IsCompleted)
                {
                    // The worker owns failure reporting through its queued job.
                    // A faulted task is nevertheless no longer accessing staging.
                    completed = true;
                }
            }

            if (!completed)
            {
                throw new TimeoutException(
                    $"Timed out waiting for Vulkan texture preparation worker {index + 1}/{workerJobs.Length} during backend retirement.");
            }
        }

        using (VulkanFrameLockScope.Enter(
                   _prepQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            _pendingPrepJobs.Clear();
            _inFlightPreparationWorkers.Clear();
            Interlocked.Exchange(ref s_ownedWorkerPreparationJobs, 0);
        }
        for (int index = 0; index < queuedJobs.Length; index++)
        {
            VulkanImportedTextureUploadJob job = queuedJobs[index];
            if (workerJobSet.Contains(job))
                continue;
            if (job.PendingUpload is { } pendingUpload)
            {
                job.PendingUpload = null;
                pendingUpload.Texture.ReleasePreparedImportedUploadResources(pendingUpload);
            }
            else if (job.WorkerPrepResult?.PendingUpload is { } workerPendingUpload)
            {
                workerPendingUpload.Texture.ReleasePreparedImportedUploadResources(workerPendingUpload);
                job.WorkerPrepResult = null;
            }
            else if (job.Preparation is not null)
            {
                job.Preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(job.Preparation);
                job.Preparation = null;
            }

            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.InvokeCanceledOnce();
        }

        for (int index = 0; index < workerJobs.Length; index++)
            ReleaseRetiredWorkerJob(workerJobs[index], reason);

        RenderWorkBudgetCoordinator.RecordTextureQueue(0, 0.0);
        Volatile.Write(ref s_pendingVulkanPrepPackages, 0);
        Interlocked.Exchange(ref _prepDrainScheduled, 0);
    }

    internal void CancelAllQueuedWork(VulkanCommandRuntime commandRuntime, string reason)
    {
        // Lifecycle establishes the worker-retirement boundary before calling
        // this method. This method only owns submitted GPU transfers.
        CancelSubmittedTransfers(commandRuntime, reason);
        // Registered query pools use the ordinary resource-retirement route.
        // Incomplete/quarantined batches retain their lease and keep the pools
        // registered until a later proven completion or device teardown.
        TryRetireTransferGpuTimingPools(commandRuntime.ResourceRuntime);
        Interlocked.Exchange(ref _prepDrainScheduled, 0);
        Interlocked.Exchange(ref _transferDrainScheduled, 0);
    }

    private void ReleaseRetiredWorkerJob(VulkanImportedTextureUploadJob job, string reason)
    {
        VulkanImportedTextureUploadWorkerResult? result = null;
        try
        {
            result = job.WorkerPrepTask?.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            job.InvokeErrorOnce(exception);
        }

        if (result?.PendingUpload is { } pendingUpload)
            pendingUpload.Texture.ReleasePreparedImportedUploadResources(pendingUpload);
        else if (job.PendingUpload is { } retainedPendingUpload)
        {
            job.PendingUpload = null;
            retainedPendingUpload.Texture.ReleasePreparedImportedUploadResources(retainedPendingUpload);
        }
        else if (job.Preparation is { } preparation)
            preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(preparation);

        job.Preparation = null;
        job.WorkerPrepTask = null;
        RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
        Interlocked.Increment(ref s_canceledStaleUploads);
        Interlocked.Increment(ref s_workerPreparationCancels);
        job.InvokeCanceledOnce();
    }

    private bool HasQueuedPrepWorkOrCompleteDrain(VulkanTextureUploadManifest? requiredManifest = null)
    {
        int depth;
        double oldestWaitMilliseconds;
        using (VulkanFrameLockScope.Enter(
                   _prepQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            depth = _pendingPrepJobs.Count;
            oldestWaitMilliseconds = GetOldestQueueWaitMillisecondsNoLock();
        }

        RenderWorkBudgetCoordinator.RecordTextureQueue(depth, oldestWaitMilliseconds);
        Volatile.Write(ref s_pendingVulkanPrepPackages, depth);
        if (requiredManifest is not null)
        {
            using (VulkanFrameLockScope.Enter(
                       _prepQueueSync,
                       EVulkanFrameWaitReason.UploadLock))
            {
                for (int index = 0; index < _pendingPrepJobs.Count; index++)
                {
                    if (requiredManifest.Contains(_pendingPrepJobs[index].Ticket))
                        return false;
                }
            }
            return true;
        }

        if (depth > 0)
            return false;

        Interlocked.Exchange(ref _prepDrainScheduled, 0);
        using (VulkanFrameLockScope.Enter(
                   _prepQueueSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            if (_pendingPrepJobs.Count == 0)
                return true;
        }

        return Interlocked.CompareExchange(ref _prepDrainScheduled, 1, 0) != 0
            ? true
            : false;
    }

    private bool CanPrepareJobThisFrame(
        VulkanImportedTextureUploadJob job,
        int preparedThisDrain,
        long drainStart,
        double prepBudgetMilliseconds,
        bool foregroundRequired = false)
    {
        if (!foregroundRequired && preparedThisDrain >= MaxPreparedUploadsPerDrain)
            return false;

        double estimate = EstimatePrepMilliseconds(job);
        if (!foregroundRequired
            && !RenderWorkBudgetCoordinator.TryConsume(RenderWorkSubsystem.TextureUpload, estimate))
            return false;

        if (foregroundRequired || prepBudgetMilliseconds <= 0.0 || preparedThisDrain == 0)
            return true;

        return TextureRuntimeDiagnostics.ElapsedMilliseconds(drainStart) + estimate <= prepBudgetMilliseconds;
    }

    private static bool ShouldYieldAfterPreparation(
        int preparedThisDrain,
        long drainStart,
        double prepBudgetMilliseconds,
        bool foregroundRequired = false)
    {
        if (!foregroundRequired && preparedThisDrain >= MaxPreparedUploadsPerDrain)
            return true;

        return !foregroundRequired && prepBudgetMilliseconds > 0.0
            && TextureRuntimeDiagnostics.ElapsedMilliseconds(drainStart) >= prepBudgetMilliseconds;
    }

    private static double ResolvePrepBudgetMilliseconds()
    {
        double configured = RenderDiagnosticsFlags.VkTextureUploadPrepBudgetMilliseconds;
        if (configured <= 0.0)
            return 0.0;

        double frameBudget = RuntimeRenderingHostServices.Settings.TextureUploadFrameBudgetMilliseconds;
        if (frameBudget <= 0.0)
            return configured;

        return Math.Min(configured, frameBudget);
    }

    private static double EstimatePrepMilliseconds(VulkanImportedTextureUploadJob job)
    {
        double bytesMiB = Math.Max(0L, job.Request.EstimatedBytes) / (1024.0 * 1024.0);
        return Math.Clamp(0.10 + bytesMiB * 0.08, 0.10, 4.0);
    }

    private double GetOldestQueueWaitMillisecondsNoLock()
    {
        if (_pendingPrepJobs.Count == 0)
            return 0.0;

        long oldest = long.MaxValue;
        for (int i = 0; i < _pendingPrepJobs.Count; i++)
            oldest = Math.Min(oldest, _pendingPrepJobs[i].QueueTimestamp);

        return oldest == long.MaxValue
            ? 0.0
            : TextureRuntimeDiagnostics.ElapsedMilliseconds(oldest);
    }

    private static int GetPriorityRank(TextureUploadPriorityClass priorityClass)
        => priorityClass switch
        {
            TextureUploadPriorityClass.VisibleNow => 4,
            TextureUploadPriorityClass.NearVisible => 3,
            TextureUploadPriorityClass.Background => 2,
            TextureUploadPriorityClass.Demotion => 1,
            _ => 0,
        };

    private void LogCompatibilityPathState(VulkanCommandRuntime commandRuntime)
    {
        if (RenderDiagnosticsFlags.VkTextureUploadPrepWorker
            && Interlocked.Exchange(ref _workerPrepCompatLogged, 1) == 0)
        {
            XREngine.Debug.Vulkan(
                "[Vulkan] Imported texture upload preparation is worker-only; descriptors publish on the render thread.");
        }

        if (RenderDiagnosticsFlags.VkTextureUploadTransferQueue
            && Interlocked.Exchange(ref _transferQueueCompatLogged, 1) == 0)
        {
            XREngine.Debug.Vulkan(
                "[Vulkan Compat] XRE_VULKAN_TEXTURE_UPLOAD_TRANSFER_QUEUE requested, but imported texture uploads remain on the graphics queue until the dedicated transfer path has an explicit semaphore release/acquire chain.");
        }

        if ((!RenderDiagnosticsFlags.VkTextureUploadPrepWorker || !RenderDiagnosticsFlags.VkAsyncTextureUpload)
            && Interlocked.Exchange(ref _renderThreadPrepCompatLogged, 1) == 0)
        {
            Interlocked.Increment(ref s_ignoredWorkerPreparationDisableOverrides);
            XREngine.Debug.Vulkan(
                "[Vulkan Compat] XRE_VULKAN_TEXTURE_UPLOAD_PREP_WORKER=false or XRE_VULKAN_ASYNC_TEXTURE_UPLOAD=false is ignored for imported texture uploads; preparation remains worker-only.");
        }
    }
}
