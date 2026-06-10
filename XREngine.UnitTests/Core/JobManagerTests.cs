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
    }
}
