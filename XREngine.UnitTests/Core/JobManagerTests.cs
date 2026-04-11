using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Core;

public sealed class JobManagerTests
{
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
