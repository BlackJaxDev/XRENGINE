using System.Diagnostics;
using NUnit.Framework;
using XREngine.Rendering;
using XREngine.Runtime.Platform.Desktop.Windowing;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class NativeRenderOwnerTests
{
    private int _previousRenderThread;

    [SetUp]
    public void SetUp() => _previousRenderThread = RuntimeEngine.RenderThreadId;

    [TearDown]
    public void TearDown() => RuntimeEngine.AssignRenderThread(_previousRenderThread);

    [Test]
    public void CollapsedLoopUsesCallingThreadAndWaitCallback()
    {
        int caller = Environment.CurrentManagedThreadId;
        int waits = 0;
        var owner = new DesktopRenderThreadHost(
            _ => Assert.Fail("Collapsed mode must not enter the split timer loop."),
            () => { Assert.That(Environment.CurrentManagedThreadId, Is.EqualTo(caller)); waits++; },
            () => Assert.Fail("A completed loop must not stop the timer."));
        owner.BlockForRendering(() => waits == 0, RuntimeRenderThreadHostMode.CollapsedWindowRenderThread);
        Assert.That(waits, Is.EqualTo(1));
        Assert.That(owner.CurrentRenderThreadId, Is.EqualTo(caller));
    }

    [Test]
    public void SplitLoopPreservesNativeThreadIdentityAndJoinsBeforeReturning()
    {
        Thread? worker = null;
        string? name = null;
        bool background = true;
        bool poolThread = true;
        ThreadPriority priority = ThreadPriority.Normal;
        var owner = new DesktopRenderThreadHost(_ =>
        {
            worker = Thread.CurrentThread;
            name = worker.Name;
            background = worker.IsBackground;
            poolThread = worker.IsThreadPoolThread;
            priority = worker.Priority;
        }, () => { }, () => { });
        owner.BlockForRendering(() => false, RuntimeRenderThreadHostMode.SplitWindowPumpPrototype);
        Assert.That(worker, Is.Not.Null);
        Assert.That(name, Is.EqualTo("XRE-Render"));
        Assert.That(background, Is.False);
        Assert.That(priority, Is.EqualTo(ThreadPriority.AboveNormal));
        Assert.That(poolThread, Is.False);
        Assert.That(worker!.IsAlive, Is.False);
        Assert.That(owner.CurrentRenderThreadId, Is.EqualTo(worker!.ManagedThreadId));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SplitFailureStopsTimerAndReleasesRunAdmission(bool stopFails)
    {
        int stops = 0;
        bool fail = true;
        var owner = new DesktopRenderThreadHost(
            _ => { if (fail) throw new IOException("render failed"); }, () => { },
            () => { stops++; if (stopFails) throw new IOException("stop failed"); });
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            owner.BlockForRendering(() => true, RuntimeRenderThreadHostMode.SplitWindowPumpPrototype))!;
        Assert.That(error.InnerException, stopFails ? Is.TypeOf<AggregateException>() : Is.TypeOf<IOException>());
        Assert.That(stops, Is.EqualTo(1));
        fail = false;
        Assert.DoesNotThrow(() => owner.BlockForRendering(() => false, RuntimeRenderThreadHostMode.SplitWindowPumpPrototype));
    }

    [Test]
    public void OverlappingAndReentrantRenderLoopsRejectWithoutReplacingTheOwner()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        DesktopRenderThreadHost? owner = null;
        owner = new DesktopRenderThreadHost(_ =>
        {
            Assert.Throws<InvalidOperationException>(() => owner!.BlockForRendering(
                () => false, RuntimeRenderThreadHostMode.CollapsedWindowRenderThread));
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        }, () => { }, () => { });
        Task run = Task.Run(() => owner.BlockForRendering(() => true, RuntimeRenderThreadHostMode.SplitWindowPumpPrototype));
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(3)), Is.True);
            Assert.Throws<InvalidOperationException>(() => owner.BlockForRendering(
                () => false, RuntimeRenderThreadHostMode.SplitWindowPumpPrototype));
        }
        finally
        {
            release.Set();
            run.GetAwaiter().GetResult();
        }
    }

    [Test]
    public void SecondaryJobsAndCleanupKeepOneBackgroundWorker()
    {
        var resources = new Resources();
        using var owner = new DesktopSecondaryGpuContext(resources);
        using var ran = new ManualResetEventSlim();
        Thread? worker = null;
        bool background = false;
        ThreadPriority priority = ThreadPriority.Lowest;
        owner.Start();
        Assert.That(owner.TryEnqueue(_ =>
        {
            worker = Thread.CurrentThread;
            background = worker.IsBackground;
            priority = worker.Priority;
            ran.Set();
        }), Is.True);
        Assert.That(ran.Wait(TimeSpan.FromSeconds(3)), Is.True);
        owner.Dispose();
        Await(() => owner.IsReleased);
        Assert.That(worker!.Name, Is.EqualTo("XR Secondary Render Context"));
        Assert.That(background, Is.True);
        Assert.That(priority, Is.EqualTo(ThreadPriority.Normal));
        Assert.That(resources.InitializeThread, Is.EqualTo(worker!.ManagedThreadId));
        Assert.That(resources.CleanupThread, Is.EqualTo(worker!.ManagedThreadId));
        Assert.That(resources.Cleanups, Is.EqualTo(1));
        Assert.That(resources.Releases, Is.EqualTo(1));
        Assert.That(owner.HasRenderer, Is.False);
        Assert.That(owner.TryEnqueue(_ => { }), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SecondaryFailureClosesAdmissionAndReleasesResources(bool duringInitialization)
    {
        var resources = new Resources { FailInitialization = duringInitialization };
        using var owner = new DesktopSecondaryGpuContext(resources);
        owner.Start();
        if (!duringInitialization)
            Assert.That(owner.TryEnqueue(_ => throw new IOException("job failed")), Is.True);
        Await(() => owner.IsReleased);
        Assert.That(owner.IsRunning, Is.False);
        Assert.That(resources.Cleanups, Is.EqualTo(1));
        Assert.That(resources.Releases, Is.EqualTo(1));
        Assert.That(owner.TryEnqueue(_ => { }), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SecondaryFailureRejectsJobsWhileCleanupRetainsResources(bool duringInitialization)
    {
        using var cleanupEntered = new ManualResetEventSlim();
        using var releaseCleanup = new ManualResetEventSlim();
        var resources = new Resources
        {
            FailInitialization = duringInitialization,
            OnCleanup = () =>
            {
                cleanupEntered.Set();
                releaseCleanup.Wait();
            },
        };
        using var owner = new DesktopSecondaryGpuContext(resources);
        int rejectedJobCalls = 0;
        try
        {
            owner.Start();
            if (!duringInitialization)
                Assert.That(owner.TryEnqueue(_ => throw new IOException("job failed")), Is.True);
            Assert.That(cleanupEntered.Wait(TimeSpan.FromSeconds(3)), Is.True);

            Assert.That(owner.TryEnqueue(_ => Interlocked.Increment(ref rejectedJobCalls)), Is.False);
            Assert.That(owner.IsRunning, Is.True);
            Assert.That(owner.HasRenderer, Is.True);
            Assert.That(owner.IsReleased, Is.False);
            Assert.That(resources.Releases, Is.Zero);
        }
        finally
        {
            releaseCleanup.Set();
            owner.Dispose();
        }

        Await(() => owner.IsReleased);
        Assert.That(owner.IsRunning, Is.False);
        Assert.That(owner.HasRenderer, Is.False);
        Assert.That(resources.Cleanups, Is.EqualTo(1));
        Assert.That(resources.Releases, Is.EqualTo(1));
        Assert.That(rejectedJobCalls, Is.Zero);
    }

    [Test]
    public void CleanupFailureStillReleasesThroughTheResourceOwner()
    {
        var resources = new Resources { FailCleanup = true };
        using var owner = new DesktopSecondaryGpuContext(resources);
        owner.Start();
        owner.Dispose();
        Await(() => owner.IsReleased);
        Assert.That(resources.Cleanups, Is.EqualTo(1));
        Assert.That(resources.Releases, Is.EqualTo(1));
    }

    [Test]
    public void TimedOutStopRetainsResourcesAndDiscardsPendingJobs()
    {
        var resources = new Resources();
        using var owner = new DesktopSecondaryGpuContext(resources);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int pendingCalls = 0;
        owner.Start();
        Assert.That(owner.TryEnqueue(_ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }), Is.True);
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(3)), Is.True);
            Assert.That(owner.TryEnqueue(_ => Interlocked.Increment(ref pendingCalls)), Is.True);
            var elapsed = Stopwatch.StartNew();
            owner.Dispose();
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
            Assert.That(owner.IsRunning, Is.True);
            Assert.That(owner.IsReleased, Is.False);
            Assert.That(resources.Releases, Is.Zero);
            Assert.That(owner.TryEnqueue(_ => { }), Is.False);
        }
        finally
        {
            release.Set();
            owner.Dispose();
        }
        Await(() => owner.IsReleased);
        Assert.That(pendingCalls, Is.Zero);
        Assert.That(resources.Releases, Is.EqualTo(1));
    }

    [Test]
    public void SecondaryJobCanStopItsOwnOwnerWithoutJoiningItself()
    {
        var resources = new Resources();
        using var owner = new DesktopSecondaryGpuContext(resources);
        owner.Start();
        TimeSpan duration = TimeSpan.MaxValue;
        Assert.That(owner.TryEnqueue(_ =>
        {
            var elapsed = Stopwatch.StartNew();
            owner.Dispose();
            duration = elapsed.Elapsed;
        }), Is.True);
        Await(() => owner.IsReleased);
        Assert.That(duration, Is.LessThan(TimeSpan.FromMilliseconds(500)));
        Assert.That(resources.Cleanups, Is.EqualTo(1));
    }

    [Test]
    public void PendingNativeDisposalKeepsOwnerAndDoesNotDispatchTwice()
    {
        Action<bool>? completed = null;
        var resources = new Resources { OnRelease = callback => Volatile.Write(ref completed, callback) };
        using var owner = new DesktopSecondaryGpuContext(resources);
        owner.Start();
        owner.Dispose();
        Await(() => Volatile.Read(ref completed) is not null);
        Assert.That(owner.IsReleased, Is.False);
        owner.Dispose();
        Assert.That(resources.Releases, Is.EqualTo(1));
        completed!(true);
        Assert.That(owner.IsReleased, Is.True);
        owner.Dispose();
        Assert.That(resources.Releases, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedNativeReleaseRetainsOwnerForRetry(bool dispatchThrows)
    {
        bool first = true;
        var resources = new Resources
        {
            OnRelease = completed =>
            {
                if (first)
                {
                    first = false;
                    if (dispatchThrows) throw new IOException("owner mailbox unavailable");
                    completed(false);
                }
                else completed(true);
            },
        };
        using var owner = new DesktopSecondaryGpuContext(resources);
        owner.Start();
        Assert.That(owner.TryEnqueue(_ => throw new IOException("stop worker")), Is.True);
        Await(() => resources.Releases == 1);
        Assert.That(owner.IsReleased, Is.False);
        owner.Dispose();
        Assert.That(owner.IsReleased, Is.True);
        Assert.That(resources.Releases, Is.EqualTo(2));
    }

    [Test]
    public void OldNativeCompletionCannotReleaseAnotherGeneration()
    {
        Action<bool>? oldCompletion = null;
        var oldResources = new Resources { OnRelease = completed => Volatile.Write(ref oldCompletion, completed) };
        using var oldOwner = new DesktopSecondaryGpuContext(oldResources);
        oldOwner.Start();
        oldOwner.Dispose();
        Await(() => Volatile.Read(ref oldCompletion) is not null);
        var newResources = new Resources();
        using var newOwner = new DesktopSecondaryGpuContext(newResources);
        newOwner.Start();
        oldCompletion!(true);
        Assert.That(oldOwner.IsReleased, Is.True);
        Assert.That(newOwner.IsRunning, Is.True);
        Assert.That(newOwner.IsReleased, Is.False);
        Assert.That(newResources.Releases, Is.Zero);
    }

    [Test]
    public void StoppedSecondaryOwnerCannotRestartAndReleasesOnlyOnce()
    {
        var resources = new Resources();
        using var owner = new DesktopSecondaryGpuContext(resources);
        owner.Dispose();
        owner.Dispose();
        Assert.That(owner.IsReleased, Is.True);
        Assert.That(resources.Releases, Is.EqualTo(1));
        Assert.Throws<InvalidOperationException>(owner.Start);
    }

    [Test]
    public void CollapsedNativeReleaseWaitsForTheCapturedRenderOwner()
    {
        int nativeOwner = Environment.CurrentManagedThreadId;
        Action? queued = null;
        int disposals = 0;
        var release = new DesktopSecondaryGpuContextRelease(nativeOwner, nativeOwner,
            task => Volatile.Write(ref queued, task),
            _ => Assert.Fail("A collapsed window must use its render mailbox."),
            () =>
            {
                Assert.That(Environment.CurrentManagedThreadId, Is.EqualTo(nativeOwner));
                disposals++;
                return true;
            });
        var resources = new Resources { OnRelease = release.Release };
        using var owner = new DesktopSecondaryGpuContext(resources);
        owner.Start();
        owner.Dispose();

        Assert.That(Volatile.Read(ref queued) is not null, Is.True);
        Assert.That(disposals, Is.Zero);
        Assert.That(owner.IsReleased, Is.False);
        queued!();
        Assert.That(disposals, Is.EqualTo(1));
        Assert.That(owner.IsReleased, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NativeReleaseRejectsAnInlineMailboxOnTheWrongThread(bool splitOwners)
    {
        int nativeOwner = Environment.CurrentManagedThreadId;
        int renderPosts = 0;
        int windowPosts = 0;
        int disposals = 0;
        bool? completed = null;
        var release = new DesktopSecondaryGpuContextRelease(nativeOwner,
            splitOwners ? -1 : nativeOwner,
            task => { renderPosts++; task(); },
            task => { windowPosts++; task(); },
            () => { disposals++; return true; });

        Task.Run(() => release.Release(success => completed = success)).GetAwaiter().GetResult();
        Assert.That(completed, Is.False);
        Assert.That(disposals, Is.Zero);
        Assert.That(renderPosts, Is.EqualTo(splitOwners ? 0 : 1));
        Assert.That(windowPosts, Is.EqualTo(splitOwners ? 1 : 0));

        release.Release(success => completed = success);
        Assert.That(completed, Is.True);
        Assert.That(disposals, Is.EqualTo(1));
    }

    [Test]
    public void UnconfirmedNativeDisposalKeepsTheSecondaryOwnerRetained()
    {
        int nativeOwner = Environment.CurrentManagedThreadId;
        Action? queued = null;
        int disposals = 0;
        var release = new DesktopSecondaryGpuContextRelease(nativeOwner, nativeOwner,
            task => Volatile.Write(ref queued, task),
            _ => Assert.Fail("A collapsed window must use its render mailbox."),
            () => { disposals++; return false; });
        var resources = new Resources { OnRelease = release.Release };
        using var owner = new DesktopSecondaryGpuContext(resources);
        owner.Start();
        owner.Dispose();
        Assert.That(Volatile.Read(ref queued) is not null, Is.True);

        queued!();
        Assert.That(disposals, Is.EqualTo(1));
        Assert.That(owner.IsReleased, Is.False);
        Assert.That(owner.HasRenderer, Is.True);
        owner.Dispose();
        Assert.That(disposals, Is.EqualTo(2));
        Assert.That(owner.IsReleased, Is.False);
    }

    private static void Await(Func<bool> condition)
        => Assert.That(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(3)), Is.True);

    private sealed class Resources : IDesktopSecondaryGpuContextResources
    {
        public bool FailInitialization;
        public bool FailCleanup;
        public Action? OnCleanup;
        public Action<Action<bool>>? OnRelease;
        public int InitializeThread;
        public int CleanupThread;
        public int Cleanups;
        public int Releases;
        public bool HasRenderer => true;
        public void Initialize()
        {
            InitializeThread = Environment.CurrentManagedThreadId;
            if (FailInitialization) throw new IOException("initialization failed");
        }
        public void Execute(Action<AbstractRenderer> job) => job(null!);
        public void CleanUp()
        {
            CleanupThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref Cleanups);
            OnCleanup?.Invoke();
            if (FailCleanup) throw new IOException("cleanup failed");
        }
        public void Release(Action<bool> completed)
        {
            Interlocked.Increment(ref Releases);
            if (OnRelease is { } release) release(completed);
            else completed(true);
        }
    }
}
