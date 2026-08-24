using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Core;

public sealed class JobManagerTests
{
    [Test]
    public void ShutdownWithoutWorkerWait_ReturnsImmediatelyWhenWorkerJobIgnoresCancellation()
    {
        using ManualResetEventSlim releaseWorker = new(false);
        var manager = new JobManager(workerCount: 1);

        manager.Schedule(new ActionJob(() => releaseWorker.Wait()));

        SpinWait.SpinUntil(() => manager.Active.Count > 0, TimeSpan.FromSeconds(1)).ShouldBeTrue();

        Stopwatch stopwatch = Stopwatch.StartNew();
        manager.Shutdown(waitForWorkers: false);
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));

        releaseWorker.Set();
        SpinWait.SpinUntil(() => manager.Active.Count == 0, TimeSpan.FromSeconds(2)).ShouldBeTrue();
        manager.Shutdown(waitForWorkers: true).ShouldBeTrue();
    }

    [Test]
    public void ShutdownFromCurrentRenderThreadJob_ReleasesBoundedQueueSlot()
    {
        var manager = new JobManager(workerCount: 1, maxQueueSize: 1);

        manager.Schedule(
            new ActionJob(() => manager.Shutdown(waitForWorkers: false)),
            JobPriority.Normal,
            JobAffinity.RenderThread);

        manager.ProcessMainThreadJobs(maxJobs: 1);

        manager.Active.ShouldBeEmpty();
        manager.QueueSlotsInUse.ShouldBe(0);
    }

    [Test]
    [NonParallelizable]
    public void ProcessMainThreadJobs_PassesRenderThreadJobKindToObserver()
    {
        var manager = new JobManager(workerCount: 1);
        Action<JobAffinity, string, RenderThreadJobKind>? previousObserver = JobManager.JobDispatchObserver;
        JobAffinity observedAffinity = JobAffinity.Any;
        string? observedLabel = null;
        RenderThreadJobKind observedKind = RenderThreadJobKind.Unknown;

        try
        {
            JobManager.JobDispatchObserver = (affinity, label, kind) =>
            {
                observedAffinity = affinity;
                observedLabel = label;
                observedKind = kind;
            };

            manager.Schedule(
                new LabeledActionJob(() => { }, "TextureUploadTest"),
                JobPriority.Normal,
                JobAffinity.RenderThread,
                renderThreadKind: RenderThreadJobKind.TextureUpload);

            manager.ProcessMainThreadJobs(maxJobs: 1);

            observedAffinity.ShouldBe(JobAffinity.RenderThread);
            observedLabel.ShouldBe("Invoke:TextureUploadTest");
            observedKind.ShouldBe(RenderThreadJobKind.TextureUpload);
        }
        finally
        {
            JobManager.JobDispatchObserver = previousObserver;
            manager.Shutdown(waitForWorkers: false);
        }
    }

    [Test]
    public void Shutdown_ReturnsEvenWhenWorkerJobIgnoresCancellation()
    {
        using ManualResetEventSlim releaseWorker = new(false);
        var manager = new JobManager(workerCount: 1);

        manager.Schedule(new ActionJob(() => releaseWorker.Wait()));

        SpinWait.SpinUntil(() => manager.Active.Count > 0, TimeSpan.FromSeconds(1)).ShouldBeTrue();

        Stopwatch stopwatch = Stopwatch.StartNew();
        manager.Shutdown();
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));

        releaseWorker.Set();
        SpinWait.SpinUntil(() => manager.Active.Count == 0, TimeSpan.FromSeconds(2)).ShouldBeTrue();
        manager.Shutdown(waitForWorkers: true).ShouldBeTrue();
    }

    [Test]
    public void Shutdown_CancelsDeferredJobAlreadyRemovedFromDeferredQueue()
    {
        using ManualResetEventSlim releaseWorker = new(false);
        var manager = new JobManager(workerCount: 1, maxQueueSize: 1);
        JobHandle active = manager.Schedule(new ActionJob(() => releaseWorker.Wait()));
        SpinWait.SpinUntil(() => manager.Active.Count > 0, TimeSpan.FromSeconds(1)).ShouldBeTrue();

        JobHandle deferred = manager.Schedule(new ActionJob(() => { }));
        manager.Shutdown(waitForWorkers: false).ShouldBeFalse();

        SpinWait.SpinUntil(() => deferred.IsCompleted, TimeSpan.FromSeconds(1)).ShouldBeTrue();
        deferred.IsCanceled.ShouldBeTrue();
        manager.QueueSlotsInUse.ShouldBe(1);

        releaseWorker.Set();
        SpinWait.SpinUntil(() => active.IsCompleted, TimeSpan.FromSeconds(1)).ShouldBeTrue();
        manager.Shutdown(waitForWorkers: true).ShouldBeTrue();
        manager.QueueSlotsInUse.ShouldBe(0);
    }

    [Test]
    public void ScheduleAfterShutdown_CompletesCanceledHandleWithoutPublishingWork()
    {
        var manager = new JobManager(workerCount: 1, maxQueueSize: 1);
        manager.Shutdown(waitForWorkers: true).ShouldBeTrue();

        JobHandle handle = manager.Schedule(new ActionJob(() =>
            throw new InvalidOperationException("A post-shutdown job must never execute.")));

        SpinWait.SpinUntil(() => handle.IsCompleted, TimeSpan.FromSeconds(1)).ShouldBeTrue();
        handle.IsCanceled.ShouldBeTrue();
        manager.Active.ShouldBeEmpty();
        manager.QueueSlotsInUse.ShouldBe(0);
    }

    [Test]
    public void Shutdown_PreservesQueuedCancellationNotificationWithoutRunningItOnCaller()
    {
        using ManualResetEventSlim callbackEntered = new(false);
        using ManualResetEventSlim releaseCallback = new(false);
        var manager = new JobManager(workerCount: 1);
        int shutdownThreadId = Environment.CurrentManagedThreadId;
        int callbackThreadId = 0;
        var job = new EnumeratorJob(
            Array.Empty<object>(),
            onCanceled: () =>
            {
                Volatile.Write(ref callbackThreadId, Environment.CurrentManagedThreadId);
                callbackEntered.Set();
                releaseCallback.Wait();
            })
        {
            CallbackContext = null,
        };
        JobHandle handle = manager.Schedule(job, JobPriority.Normal, JobAffinity.RenderThread);

        Stopwatch stopwatch = Stopwatch.StartNew();
        manager.Shutdown(waitForWorkers: true, TimeSpan.FromMilliseconds(25)).ShouldBeFalse();
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        callbackEntered.Wait(TimeSpan.FromSeconds(1)).ShouldBeTrue();
        Volatile.Read(ref callbackThreadId).ShouldNotBe(shutdownThreadId);

        releaseCallback.Set();
        SpinWait.SpinUntil(() => handle.IsCompleted, TimeSpan.FromSeconds(1)).ShouldBeTrue();
        handle.IsCanceled.ShouldBeTrue();
        manager.Shutdown(waitForWorkers: true).ShouldBeTrue();
    }

    [Test]
    public void Shutdown_PendingTaskThatIgnoresCancellationPreventsCleanOwnershipReturn()
    {
        using ManualResetEventSlim pendingAttached = new(false);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new JobManager(workerCount: 1);
        var job = new EnumeratorJob(WaitForPendingTask(pending.Task, pendingAttached))
        {
            CallbackContext = null,
        };
        JobHandle handle = manager.Schedule(job);
        pendingAttached.Wait(TimeSpan.FromSeconds(1)).ShouldBeTrue();

        manager.Shutdown(waitForWorkers: true, TimeSpan.FromMilliseconds(25)).ShouldBeFalse();
        handle.IsCompleted.ShouldBeFalse();

        pending.TrySetResult(true);
        SpinWait.SpinUntil(() => handle.IsCompleted, TimeSpan.FromSeconds(1)).ShouldBeTrue();
        handle.IsCanceled.ShouldBeTrue();
        manager.Shutdown(waitForWorkers: true).ShouldBeTrue();
    }

    [Test]
    public void Shutdown_CancellationCallbackCanCompletePendingTaskWithoutSelfDeadlock()
    {
        using ManualResetEventSlim pendingAttached = new(false);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new JobManager(workerCount: 1);
        var job = new EnumeratorJob(WaitForPendingTask(pending.Task, pendingAttached))
        {
            CallbackContext = null,
        };
        JobHandle handle = manager.Schedule(job);
        pendingAttached.Wait(TimeSpan.FromSeconds(1)).ShouldBeTrue();
        using CancellationTokenRegistration registration = job.CancellationToken.Register(
            () => pending.TrySetResult(true));

        manager.Shutdown(waitForWorkers: true, TimeSpan.FromSeconds(1)).ShouldBeTrue();
        handle.IsCompleted.ShouldBeTrue();
        handle.IsCanceled.ShouldBeTrue();
        manager.Active.ShouldBeEmpty();
    }

    [Test]
    public void Shutdown_DrainedRequeuedJobLeavesActiveSetAfterCancellationFinalizes()
    {
        var manager = new JobManager(workerCount: 1);
        var job = new EnumeratorJob(YieldUntilNextDispatch())
        {
            CallbackContext = null,
        };
        JobHandle handle = manager.Schedule(job, JobPriority.Normal, JobAffinity.RenderThread);

        manager.ProcessMainThreadJobs(maxJobs: 1);
        manager.Active.ShouldContain(job);
        manager.GetQueuedCount(JobPriority.Normal, JobAffinity.RenderThread).ShouldBe(1);

        manager.Shutdown(waitForWorkers: true, TimeSpan.FromSeconds(1)).ShouldBeTrue();
        handle.IsCompleted.ShouldBeTrue();
        handle.IsCanceled.ShouldBeTrue();
        manager.Active.ShouldBeEmpty();
    }

    private static System.Collections.IEnumerable WaitForPendingTask(
        Task task,
        ManualResetEventSlim pendingAttached)
    {
        pendingAttached.Set();
        yield return task;
    }

    private static System.Collections.IEnumerable YieldUntilNextDispatch()
    {
        yield return WaitForNextDispatch.Instance;
        throw new InvalidOperationException("A shutdown-drained requeued job must not execute another step.");
    }
}
