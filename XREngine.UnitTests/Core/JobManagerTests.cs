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
