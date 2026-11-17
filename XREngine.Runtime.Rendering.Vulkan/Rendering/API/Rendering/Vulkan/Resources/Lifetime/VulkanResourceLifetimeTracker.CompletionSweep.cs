using System;
using System.Diagnostics;
using System.Globalization;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceLifetimeTracker
{
    /// <summary>Upper bound of unobserved submissions that one sweep checks.</summary>
    internal const int MaximumSweptSubmissions = 64;

    /// <summary>
    /// Marks every unobserved submission whose native work is already complete, so the per-domain
    /// completion frontier (<see cref="CompletedGraphicsSequence"/> and the others) follows the GPU
    /// and not the code paths that report completion. Each submission owner reports its own
    /// completion only at certain points (a desktop frame slot when it is reused, a GPU-statistics
    /// readback when it is read, an overlap slot when it is reused), and the frontier stops at the
    /// oldest unobserved submission of its domain, so one late owner made every newer buffer look
    /// in use.
    /// </summary>
    /// <remarks>
    /// Only submissions that signal a registered timeline semaphore
    /// (<see cref="RegisterSweepableTimeline"/>) are queried. Those semaphores live as long as the
    /// frame loop and are unregistered before they are destroyed, so the sweep never queries a
    /// destroyed handle. Fence submissions are not queried: many owners destroy or reset their
    /// fences on their own schedule. A fence submission is still marked when a later timeline
    /// submission on the same queue completes, because a signal covers every command submitted to
    /// its queue before it.
    /// <para>
    /// The sweep copies at most <see cref="MaximumSweptSubmissions"/> unobserved submissions under
    /// <see cref="SyncRoot"/>, releases the lock, queries each one through
    /// <typeparamref name="TStatus"/> (no wait), and then marks the completed ones under the lock
    /// with <see cref="MarkQueueSequenceCompletedNoLock"/>, the same call that the owners use. Native
    /// queries never run under the lock, so the lock order of the existing completion callers does
    /// not change. Entries stay in the list (marked observed) until their owner removes them or
    /// <see cref="ReapObservedSubmissionsNoLock"/> compacts them. Allocation-free.
    /// </para>
    /// </remarks>
    /// <returns>The number of submissions that this sweep marked complete.</returns>
    internal int SweepCompletions<TStatus>(ref TStatus status)
        where TStatus : struct, IVulkanLifetimeSubmissionStatus
    {
        // Lock order: SweepGate, then SyncRoot. Only this method takes SweepGate, and no native
        // query runs while SyncRoot is held.
        lock (SweepGate)
        {
            VulkanLifetimeSubmission[] buffer = _sweepBuffer;
            int count = 0;
            using (VulkanFrameLockScope.Enter(SyncRoot, EVulkanFrameWaitReason.ResourceLifetimeLock))
            {
                if (DeviceLost)
                    return 0;
                for (int index = 0; index < LifetimeSubmissions.Count && count < buffer.Length; ++index)
                {
                    VulkanLifetimeSubmission submission = LifetimeSubmissions[index];
                    if (!submission.CompletionObserved &&
                        submission.TimelineValue != 0UL &&
                        IsSweepableTimelineNoLock(submission.TimelineSemaphoreHandle))
                        buffer[count++] = submission;
                }
            }
            if (count == 0)
                return 0;

            int completed = 0;
            for (int index = 0; index < count; ++index)
            {
                if (status.IsComplete(in buffer[index]))
                    buffer[completed++] = buffer[index];
            }
            if (completed != 0)
            {
                using (VulkanFrameLockScope.Enter(SyncRoot, EVulkanFrameWaitReason.ResourceLifetimeLock))
                {
                    for (int index = 0; index < completed; ++index)
                        MarkQueueSequenceCompletedNoLock(buffer[index].QueueDomain, buffer[index].QueueSequence);
                }
            }
            return completed;
        }
    }

    // Timeline semaphores that the sweep may query. Guarded by SyncRoot.
    private readonly ulong[] _sweepableTimelines = new ulong[4];

    /// <summary>
    /// Lets <see cref="SweepCompletions{TStatus}"/> query submissions that signal
    /// <paramref name="semaphoreHandle"/>. The owner must call
    /// <see cref="UnregisterSweepableTimeline"/> before it destroys the semaphore.
    /// </summary>
    internal void RegisterSweepableTimeline(ulong semaphoreHandle)
    {
        if (semaphoreHandle == 0UL)
            return;
        using (VulkanFrameLockScope.Enter(SyncRoot, EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            int free = -1;
            for (int index = 0; index < _sweepableTimelines.Length; ++index)
            {
                if (_sweepableTimelines[index] == semaphoreHandle)
                    return;
                if (free < 0 && _sweepableTimelines[index] == 0UL)
                    free = index;
            }
            if (free >= 0)
                _sweepableTimelines[free] = semaphoreHandle;
        }
    }

    /// <summary>Stops the sweep from querying <paramref name="semaphoreHandle"/>. Call before destroying it.</summary>
    internal void UnregisterSweepableTimeline(ulong semaphoreHandle)
    {
        if (semaphoreHandle == 0UL)
            return;
        // SweepGate first: a sweep that already copied this semaphore finishes its native queries
        // before the owner can destroy the handle.
        lock (SweepGate)
        {
            using (VulkanFrameLockScope.Enter(SyncRoot, EVulkanFrameWaitReason.ResourceLifetimeLock))
            {
                for (int index = 0; index < _sweepableTimelines.Length; ++index)
                    if (_sweepableTimelines[index] == semaphoreHandle)
                        _sweepableTimelines[index] = 0UL;
            }
        }
    }

    private bool IsSweepableTimelineNoLock(ulong semaphoreHandle)
    {
        if (semaphoreHandle == 0UL)
            return false;
        for (int index = 0; index < _sweepableTimelines.Length; ++index)
            if (_sweepableTimelines[index] == semaphoreHandle)
                return true;
        return false;
    }

    // Reused copy target of SweepCompletions. The render thread runs the sweep at frame start, and
    // QueryBufferContentReuse runs it on the thread that asks, so SweepGate guards the buffer.
    private readonly VulkanLifetimeSubmission[] _sweepBuffer = new VulkanLifetimeSubmission[MaximumSweptSubmissions];

    /// <summary>Serializes users of the sweep copy buffer.</summary>
    internal object SweepGate { get; } = new();

    /// <summary>
    /// Diagnostics only: describes the graphics completion frontier for a buffer whose last
    /// graphics use is <paramref name="bufferLastGraphicsSequence"/>: the completed, observed and
    /// last graphics sequences, the buffer's last use and queued references, the number of
    /// unobserved graphics submissions, and the oldest one (sequence, queue, kind and age). Call
    /// while <see cref="SyncRoot"/> is held. Allocates the returned string.
    /// </summary>
    internal string DescribeGraphicsFrontierNoLock(ulong bufferLastGraphicsSequence, int bufferQueuedReferences)
    {
        int unobserved = 0;
        VulkanLifetimeSubmission oldest = default;
        bool hasOldest = false;
        for (int index = 0; index < LifetimeSubmissions.Count; ++index)
        {
            VulkanLifetimeSubmission submission = LifetimeSubmissions[index];
            if (submission.QueueDomain != EVulkanLifetimeQueueDomain.Graphics || submission.CompletionObserved)
                continue;
            unobserved++;
            if (!hasOldest || submission.QueueSequence < oldest.QueueSequence)
            {
                oldest = submission;
                hasOldest = true;
            }
        }

        string oldestText = hasOldest
            ? string.Create(CultureInfo.InvariantCulture,
                $"seq{oldest.QueueSequence}/queue0x{oldest.QueueHandle:X}/kind={VulkanLifetimeSubmissionKinds.GetName(oldest.KindId)}/ageMs={(oldest.SubmitTimestamp == 0L ? -1.0 : Stopwatch.GetElapsedTime(oldest.SubmitTimestamp).TotalMilliseconds):F1}")
            : "none";
        return string.Create(CultureInfo.InvariantCulture,
            $"completedGraphics={CompletedGraphicsSequence} observedGraphics={ObservedGraphicsSequence} lastGraphics={LastGraphicsSequence} bufferLastGraphics={bufferLastGraphicsSequence} bufferQueued={bufferQueuedReferences} unobservedGraphics={unobserved} oldestUnobserved={oldestText}");
    }
}
