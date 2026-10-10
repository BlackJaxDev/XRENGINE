using NUnit.Framework;
using XREngine.Execution;

namespace XREngine.UnitTests.Core;

[TestFixture]
[NonParallelizable]
public sealed class ProfilerStatsWorkerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopReturnTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public void RegisteredWorker_CancellationReturnsBeforeCallbackExits_ThenJoins()
        => RunWithDeadline(() =>
        {
            ManualResetEventSlim entered = new(false);
            ManualResetEventSlim release = new(false);
            string? name = null;
            bool? isBackground = null;
            ThreadPriority? priority = null;
            int? callbackThreadId = null;
            int testThreadId = Environment.CurrentManagedThreadId;
            int exceptionCount = 0;

            IProfilerStatsWorker worker = ProfilerStatsWorkerServices.GetRequiredFactory().Create(
                () =>
                {
                    Thread current = Thread.CurrentThread;
                    name = current.Name;
                    isBackground = current.IsBackground;
                    priority = current.Priority;
                    callbackThreadId = current.ManagedThreadId;
                    entered.Set();
                    release.Wait();
                    return 0;
                },
                _ => Interlocked.Increment(ref exceptionCount));

            bool started = false;
            Task? stop = null;
            try
            {
                worker.Start();
                started = true;
                Assert.That(entered.Wait(TestTimeout), Is.True, "The profiler callback did not start.");

                stop = Task.Run(worker.RequestStop);
                Assert.That(stop.Wait(StopReturnTimeout), Is.True, "RequestStop waited for the blocked callback.");
                Assert.That(worker.IsAlive, Is.True, "Cancellation ended the blocked callback.");
                Assert.Multiple(() =>
                {
                    Assert.That(name, Is.EqualTo("XREngine.ProfilerStats"));
                    Assert.That(isBackground, Is.True);
                    Assert.That(priority, Is.EqualTo(ThreadPriority.BelowNormal));
                    Assert.That(callbackThreadId, Is.Not.EqualTo(testThreadId));
                    Assert.That(Volatile.Read(ref exceptionCount), Is.Zero);
                });
            }
            finally
            {
                release.Set();
                if (started)
                {
                    stop ??= Task.Run(worker.RequestStop);
                    stop.GetAwaiter().GetResult();
                    worker.WaitForExit();
                }
                worker.Dispose();
                release.Dispose();
                entered.Dispose();
            }

            Assert.That(worker.IsAlive, Is.False);
        });

    [Test]
    public void RegisteredWorker_ReportsExactCallbackException_ThenContinues()
        => RunWithDeadline(() =>
        {
            ManualResetEventSlim reported = new(false);
            ManualResetEventSlim continued = new(false);
            ManualResetEventSlim release = new(false);
            InvalidOperationException expected = new("profiler-cycle-marker");
            Exception? actual = null;
            int reportCount = 0;
            int cycleCount = 0;

            IProfilerStatsWorker worker = ProfilerStatsWorkerServices.GetRequiredFactory().Create(
                () =>
                {
                    if (Interlocked.Increment(ref cycleCount) == 1)
                        throw expected;
                    continued.Set();
                    release.Wait();
                    return 0;
                },
                ex =>
                {
                    actual = ex;
                    Interlocked.Increment(ref reportCount);
                    reported.Set();
                });

            bool started = false;
            try
            {
                worker.Start();
                started = true;
                Assert.That(reported.Wait(TestTimeout), Is.True, "The callback exception was not reported.");
                Assert.That(continued.Wait(TestTimeout), Is.True, "The worker did not run another cycle.");
                Assert.Multiple(() =>
                {
                    Assert.That(actual, Is.SameAs(expected));
                    Assert.That(Volatile.Read(ref reportCount), Is.EqualTo(1));
                    Assert.That(Volatile.Read(ref cycleCount), Is.EqualTo(2));
                });
            }
            finally
            {
                release.Set();
                if (started)
                {
                    worker.RequestStop();
                    worker.WaitForExit();
                }
                worker.Dispose();
                release.Dispose();
                continued.Dispose();
                reported.Dispose();
            }

            Assert.That(worker.IsAlive, Is.False);
        });

    private static void RunWithDeadline(Action scenario)
    {
        // The deadline fails acceptance. It does not stop an OS thread or prove failed cleanup.
        Task.Run(scenario).WaitAsync(TestTimeout).GetAwaiter().GetResult();
    }
}
