using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Execution;
using XREngine.Timers;

namespace XREngine.UnitTests.Core;

[NonParallelizable]
public sealed class EngineTimerLoopDomainTests
{
    private static readonly FieldInfo FactoryField = typeof(EngineWorkerDomainServices)
        .GetField("_factory", BindingFlags.Static | BindingFlags.NonPublic)!;

    [TestCase(false)]
    [TestCase(true)]
    public void MissingLoopCapability_FailsBeforeStartingClock(bool missingFactory)
    {
        IEngineWorkerDomainFactory? previous = EngineWorkerDomainServices.Factory;
        FactoryField.SetValue(null, missingFactory ? null : new JobsOnlyFactory());
        try
        {
            var timer = new EngineTimer();
            long before = timer.TimeTicks();
            Should.Throw<InvalidOperationException>(timer.RunGameLoop);
            timer.IsRunning.ShouldBeFalse();
            timer.TimeTicks().ShouldBe(before);
            timer.StopAndWait(TimeSpan.Zero).ShouldBeTrue();
        }
        finally { FactoryField.SetValue(null, previous); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StopDuringFactoryCreation_CancelsStartup(bool waitForExit)
    {
        IEngineWorkerDomainFactory? previous = EngineWorkerDomainServices.Factory;
        var timer = new EngineTimer();
        var domain = new FailedStartDomain(timer) { AllowExit = true };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        FactoryField.SetValue(null, new BlockingFactory(domain, entered, release));
        Exception? startupFailure = null;
        var starter = new Thread(() =>
        {
            try { timer.RunGameLoop(); }
            catch (Exception failure) { startupFailure = failure; }
        }) { IsBackground = true };
        starter.Start();
        try
        {
            entered.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
            if (waitForExit)
                timer.StopAndWait(TimeSpan.Zero).ShouldBeFalse();
            else
                timer.Stop();
            release.Set();
            starter.Join(TimeSpan.FromSeconds(2)).ShouldBeTrue();
            startupFailure.ShouldBeNull();
            domain.StartCount.ShouldBe(0);
            domain.WaitCount.ShouldBe(1);
            timer.IsRunning.ShouldBeFalse();
            timer.StopAndWait(TimeSpan.Zero).ShouldBeTrue();
        }
        finally
        {
            release.Set();
            starter.Join(TimeSpan.FromSeconds(2));
            timer.StopAndWait(TimeSpan.Zero);
            FactoryField.SetValue(null, previous);
        }
    }

    [Test]
    public void PartialStart_StopsCallbacksAndRetainsIncompleteDomain()
    {
        IEngineWorkerDomainFactory? previous = EngineWorkerDomainServices.Factory;
        var timer = new EngineTimer();
        var domain = new FailedStartDomain(timer);
        FactoryField.SetValue(null, new LoopFactory(domain));
        try
        {
            Should.Throw<InvalidOperationException>(timer.RunGameLoop).Message.ShouldBe("injected partial start");
            timer.IsRunning.ShouldBeFalse();
            domain.WaitSawStoppedTimer.ShouldBeTrue();
            domain.WaitCount.ShouldBe(1);
            Should.Throw<InvalidOperationException>(timer.RunGameLoop);
            domain.StartCount.ShouldBe(1);
            timer.StopAndWait(TimeSpan.Zero).ShouldBeFalse();
            domain.AllowExit = true;
            timer.StopAndWait(TimeSpan.Zero).ShouldBeTrue();
        }
        finally
        {
            domain.AllowExit = true;
            timer.StopAndWait(TimeSpan.Zero);
            FactoryField.SetValue(null, previous);
        }
    }

    [Test]
    public void PartialStart_CleanupFailurePreservesBothErrorsAndOwner()
    {
        IEngineWorkerDomainFactory? previous = EngineWorkerDomainServices.Factory;
        var timer = new EngineTimer();
        var domain = new FailedStartDomain(timer) { FailWait = true };
        FactoryField.SetValue(null, new LoopFactory(domain));
        try
        {
            AggregateException failure = Should.Throw<AggregateException>(timer.RunGameLoop);
            failure.InnerExceptions.Count.ShouldBe(2);
            failure.InnerExceptions[0].Message.ShouldBe("injected partial start");
            failure.InnerExceptions[1].Message.ShouldBe("injected join failure");
            timer.IsRunning.ShouldBeFalse();
            Should.Throw<InvalidOperationException>(timer.RunGameLoop);
        }
        finally
        {
            domain.FailWait = false;
            domain.AllowExit = true;
            timer.StopAndWait(TimeSpan.Zero).ShouldBeTrue();
            FactoryField.SetValue(null, previous);
        }
    }

    [Test]
    public void StopDuringStartup_CannotReleaseTheStartingOwner()
    {
        IEngineWorkerDomainFactory? previous = EngineWorkerDomainServices.Factory;
        var timer = new EngineTimer();
        var domain = new FailedStartDomain(timer) { CheckStartupStop = true };
        FactoryField.SetValue(null, new LoopFactory(domain));
        try
        {
            Should.Throw<InvalidOperationException>(timer.RunGameLoop);
            domain.StartupStopCompleted.ShouldBeFalse();
            domain.WaitCount.ShouldBe(1);
        }
        finally
        {
            domain.AllowExit = true;
            timer.StopAndWait(TimeSpan.Zero);
            FactoryField.SetValue(null, previous);
        }
    }

    private class JobsOnlyFactory : IEngineWorkerDomainFactory
    {
        public IEngineGeneralWorkDomain CreateGeneralDomain(JobManager jobs, int workerCount) => throw new NotSupportedException();
        public IEngineJobAuxiliaryWorkDomain CreateAuxiliaryDomain(JobManager jobs) => throw new NotSupportedException();
    }

    private sealed class LoopFactory(IEngineLoopDomain domain) : JobsOnlyFactory, IEngineLoopDomainFactory
    {
        public IEngineLoopDomain CreateLoopDomain(Action update, Action collectVisible, Action fixedUpdate) => domain;
    }

    private sealed class BlockingFactory(IEngineLoopDomain domain, ManualResetEventSlim entered, ManualResetEventSlim release)
        : JobsOnlyFactory, IEngineLoopDomainFactory
    {
        public IEngineLoopDomain CreateLoopDomain(Action update, Action collectVisible, Action fixedUpdate)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The test did not release factory creation.");
            return domain;
        }
    }

    private sealed class FailedStartDomain(EngineTimer timer) : IEngineLoopDomain
    {
        public bool AllowExit;
        public bool CheckStartupStop;
        public bool StartupStopCompleted;
        public int StartCount;
        public int WaitCount;
        public bool WaitSawStoppedTimer;
        public bool FailWait;
        public bool HasLiveThreads => StartCount != 0 && !AllowExit;

        public void Start()
        {
            StartCount++;
            if (CheckStartupStop)
                StartupStopCompleted = timer.StopAndWait(TimeSpan.Zero);
            throw new InvalidOperationException("injected partial start");
        }

        public EngineLoopExitStatus WaitForExit(TimeSpan timeout)
        {
            WaitCount++;
            WaitSawStoppedTimer = !timer.IsRunning;
            if (FailWait)
                throw new InvalidOperationException("injected join failure");
            return new(AllowExit, true, true);
        }
    }
}
