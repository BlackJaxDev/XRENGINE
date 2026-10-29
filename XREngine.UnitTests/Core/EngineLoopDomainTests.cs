using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Execution;

namespace XREngine.UnitTests.Core;

[NonParallelizable]
public sealed class EngineLoopDomainTests
{
    private static IEngineLoopDomainFactory Factory()
    {
        ThreadedWorkerBackend.EnsureRegistered();
        return (IEngineLoopDomainFactory)EngineWorkerDomainServices.GetRequiredFactory();
    }

    [Test]
    public void NativeLoops_PreserveIdentityAndOneShutdownDeadline()
    {
        using var entered = new CountdownEvent(3);
        using var release = new ManualResetEventSlim();
        Thread?[] workers = new Thread[3];
        void Run(int index)
        {
            workers[index] = Thread.CurrentThread;
            entered.Signal();
            release.Wait();
        }
        IEngineLoopDomain domain = Factory().CreateLoopDomain(() => Run(0), () => Run(1), () => Run(2));
        domain.Start();
        try
        {
            entered.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
            string[] names = ["XRE-Update", "XRE-CollectVisible", "XRE-FixedUpdate"];
            for (int index = 0; index < workers.Length; index++)
            {
                workers[index]!.Name.ShouldBe(names[index]);
                workers[index]!.IsBackground.ShouldBeTrue();
                workers[index]!.Priority.ShouldBe(ThreadPriority.AboveNormal);
                workers[index]!.IsThreadPoolThread.ShouldBeFalse();
            }
            domain.WaitForExit(default).AllStopped.ShouldBeFalse();
            Should.Throw<ArgumentOutOfRangeException>(() => domain.WaitForExit(Timeout.InfiniteTimeSpan));
            var elapsed = Stopwatch.StartNew();
            domain.WaitForExit(TimeSpan.FromMilliseconds(300)).ShouldBe(new(false, false, false));
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(700));
            domain.HasLiveThreads.ShouldBeTrue();
        }
        finally
        {
            release.Set();
            domain.WaitForExit(TimeSpan.FromSeconds(2)).AllStopped.ShouldBeTrue();
        }
        domain.HasLiveThreads.ShouldBeFalse();
        domain.WaitForExit(TimeSpan.Zero).AllStopped.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(domain.Start);
    }

    [Test]
    public void StartupGateContention_UsesTheShutdownDeadline()
    {
        IEngineLoopDomain domain = Factory().CreateLoopDomain(static () => { }, static () => { }, static () => { });
        object gate = domain.GetType().GetField("_startGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(domain)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            lock (gate)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(2));
            }
        }) { IsBackground = true };
        owner.Start();
        try
        {
            entered.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
            var elapsed = Stopwatch.StartNew();
            domain.WaitForExit(TimeSpan.Zero).ShouldBe(new(false, false, false));
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500));
            elapsed.Restart();
            domain.WaitForExit(TimeSpan.FromMilliseconds(100)).AllStopped.ShouldBeFalse();
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            release.Set();
            owner.Join(TimeSpan.FromSeconds(2)).ShouldBeTrue();
        }
        domain.WaitForExit(TimeSpan.Zero).AllStopped.ShouldBeTrue();
    }

    [Test]
    public void IdleOwner_ReleasePreventsLaterStartup()
    {
        int calls = 0;
        IEngineLoopDomain domain = Factory().CreateLoopDomain(
            () => Interlocked.Increment(ref calls),
            () => Interlocked.Increment(ref calls),
            () => Interlocked.Increment(ref calls));
        domain.HasLiveThreads.ShouldBeFalse();
        domain.WaitForExit(TimeSpan.Zero).AllStopped.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(domain.Start);
        calls.ShouldBe(0);
    }

    [Test]
    public void NativeLoop_CannotJoinItselfButOwnerCanRetry()
    {
        using var completed = new ManualResetEventSlim();
        EngineLoopExitStatus status = default;
        IEngineLoopDomain? domain = null;
        domain = Factory().CreateLoopDomain(() =>
        {
            status = domain!.WaitForExit(TimeSpan.FromSeconds(1));
            completed.Set();
        }, static () => { }, static () => { });
        domain.Start();
        completed.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
        status.UpdateStopped.ShouldBeFalse();
        status.CollectVisibleStopped.ShouldBeTrue();
        status.FixedUpdateStopped.ShouldBeTrue();
        domain.WaitForExit(TimeSpan.FromSeconds(2)).AllStopped.ShouldBeTrue();
    }

    [Test]
    public void NativePartialStart_RetainsStartedWorkerUntilRetry()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IEngineLoopDomain domain = Factory().CreateLoopDomain(() =>
        {
            entered.Set();
            release.Wait();
        }, static () => { }, static () => { });

        // A consumed second thread causes the real Thread.Start to fail after
        // the first worker starts, without adding a production fault hook.
        Thread second = (Thread)domain.GetType().GetField("_collectVisibleThread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(domain)!;
        second.Start();
        second.Join(TimeSpan.FromSeconds(2)).ShouldBeTrue();
        try
        {
            Should.Throw<ThreadStateException>(domain.Start);
            entered.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
            domain.WaitForExit(TimeSpan.Zero).ShouldBe(new(false, true, true));
            domain.HasLiveThreads.ShouldBeTrue();
            Should.Throw<InvalidOperationException>(domain.Start);
        }
        finally
        {
            release.Set();
            domain.WaitForExit(TimeSpan.FromSeconds(2)).AllStopped.ShouldBeTrue();
        }
    }
}
