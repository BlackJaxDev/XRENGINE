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
    private bool TryDequeueBestPrepJob(out VulkanImportedTextureUploadJob job)
    {
        lock (_prepQueueSync)
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
        lock (_prepQueueSync)
        {
            _pendingPrepJobs.Add(job);
            RenderWorkBudgetCoordinator.RecordTextureQueue(
                _pendingPrepJobs.Count,
                GetOldestQueueWaitMillisecondsNoLock());
            Volatile.Write(ref s_pendingVulkanPrepPackages, _pendingPrepJobs.Count);
        }
    }

    private void CancelQueuedPreparation(string reason)
    {
        VulkanImportedTextureUploadJob[] canceledJobs;
        lock (_prepQueueSync)
        {
            canceledJobs = [.. _pendingPrepJobs];
            _pendingPrepJobs.Clear();
            RenderWorkBudgetCoordinator.RecordTextureQueue(0, 0.0);
            Volatile.Write(ref s_pendingVulkanPrepPackages, 0);
        }

        for (int i = 0; i < canceledJobs.Length; i++)
        {
            VulkanImportedTextureUploadJob job = canceledJobs[i];
            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
        }
    }

    internal void CancelAllQueuedWork(VulkanRenderer renderer, string reason)
    {
        VulkanImportedTextureUploadJob[] canceledJobs;
        lock (_prepQueueSync)
        {
            canceledJobs = [.. _pendingPrepJobs];
            _pendingPrepJobs.Clear();
            RenderWorkBudgetCoordinator.RecordTextureQueue(0, 0.0);
            Volatile.Write(ref s_pendingVulkanPrepPackages, 0);
        }

        for (int i = 0; i < canceledJobs.Length; i++)
        {
            VulkanImportedTextureUploadJob job = canceledJobs[i];
            try
            {
                if (job.WorkerPrepTask is not null)
                    job.WorkerPrepTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            if (job.Preparation is not null)
            {
                job.Preparation.Texture.ReleaseSynchronizedImportedUploadPreparation(job.Preparation);
                job.Preparation = null;
            }

            RecordState(job.Request, VulkanTextureUploadGenerationState.Canceled, reason);
            Interlocked.Increment(ref s_canceledStaleUploads);
            job.OnCanceled?.Invoke();
        }

        CancelSubmittedTransfers(renderer, reason);
        Interlocked.Exchange(ref _prepDrainScheduled, 0);
        Interlocked.Exchange(ref _transferDrainScheduled, 0);
    }

    private bool HasQueuedPrepWorkOrCompleteDrain()
    {
        int depth;
        double oldestWaitMilliseconds;
        lock (_prepQueueSync)
        {
            depth = _pendingPrepJobs.Count;
            oldestWaitMilliseconds = GetOldestQueueWaitMillisecondsNoLock();
        }

        RenderWorkBudgetCoordinator.RecordTextureQueue(depth, oldestWaitMilliseconds);
        Volatile.Write(ref s_pendingVulkanPrepPackages, depth);
        if (depth > 0)
            return false;

        Interlocked.Exchange(ref _prepDrainScheduled, 0);
        lock (_prepQueueSync)
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
        double prepBudgetMilliseconds)
    {
        if (preparedThisDrain >= MaxPreparedUploadsPerDrain)
            return false;

        double estimate = EstimatePrepMilliseconds(job);
        if (!RenderWorkBudgetCoordinator.TryConsume(RenderWorkSubsystem.TextureUpload, estimate))
            return false;

        if (prepBudgetMilliseconds <= 0.0 || preparedThisDrain == 0)
            return true;

        return TextureRuntimeDiagnostics.ElapsedMilliseconds(drainStart) + estimate <= prepBudgetMilliseconds;
    }

    private static bool ShouldYieldAfterPreparation(
        int preparedThisDrain,
        long drainStart,
        double prepBudgetMilliseconds)
    {
        if (preparedThisDrain >= MaxPreparedUploadsPerDrain)
            return true;

        return prepBudgetMilliseconds > 0.0
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

    private void LogCompatibilityPathState(VulkanRenderer renderer)
    {
        if (RenderDiagnosticsFlags.VkTextureUploadPrepWorker
            && Interlocked.Exchange(ref _workerPrepCompatLogged, 1) == 0)
        {
            XREngine.Debug.Vulkan(
                "[Vulkan] XRE_VULKAN_TEXTURE_UPLOAD_PREP_WORKER requested; imported texture upload preparation will run on the Vulkan upload context lock and publish descriptors on the render thread.");
        }

        if (RenderDiagnosticsFlags.VkTextureUploadTransferQueue
            && !renderer.HasDedicatedTextureUploadTransferQueue
            && Interlocked.Exchange(ref _transferQueueCompatLogged, 1) == 0)
        {
            XREngine.Debug.Vulkan(
                "[Vulkan Compat] XRE_VULKAN_TEXTURE_UPLOAD_TRANSFER_QUEUE requested, but this device did not expose a dedicated transfer queue family; imported texture copies will submit through the graphics frame command buffer.");
        }

        if (!RenderDiagnosticsFlags.VkTextureUploadPrepWorker
            && Interlocked.Exchange(ref _renderThreadPrepCompatLogged, 1) == 0)
        {
            XREngine.Debug.Vulkan(
                "[Vulkan Compat] Imported texture upload preparation is budgeted on the render thread (budget {0:F3} ms). Preferred Vulkan path is worker-side preparation through a dedicated upload context.",
                ResolvePrepBudgetMilliseconds());
        }
    }
}
