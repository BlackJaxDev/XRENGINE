using System.Diagnostics;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Core;

/// <summary>
/// Checks that render frames defer queued app-thread work for at most the deferral limit.
/// </summary>
[TestFixture]
public sealed class AppThreadWorkDeferralPolicyTests
{
    private static readonly long Millisecond = Stopwatch.Frequency / 1000;
    private static readonly long Limit = 50 * Millisecond;

    [Test]
    public void EmptyQueue_NeverStartsWork()
    {
        var policy = new AppThreadWorkDeferralPolicy(Limit);

        for (long time = 0; time < 10 * Limit; time += Millisecond)
            policy.ShouldStartDuringRenderFrame(hasQueuedWork: false, time).ShouldBeFalse();

        AppThreadDispatchDiagnostics diagnostics = policy.Capture();
        diagnostics.DeferredChecks.ShouldBe(0);
        diagnostics.DeferredStarts.ShouldBe(0);
    }

    [Test]
    public void QueuedWork_StartsWhenItReachesTheLimit()
    {
        var policy = new AppThreadWorkDeferralPolicy(Limit);
        long start = 1000 * Millisecond;

        policy.ShouldStartDuringRenderFrame(true, start).ShouldBeFalse();
        policy.ShouldStartDuringRenderFrame(true, start + 49 * Millisecond).ShouldBeFalse();
        policy.ShouldStartDuringRenderFrame(true, start + Limit).ShouldBeTrue();

        AppThreadDispatchDiagnostics diagnostics = policy.Capture();
        diagnostics.DeferredChecks.ShouldBe(2);
        diagnostics.DeferredStarts.ShouldBe(1);
        diagnostics.MaximumDeferralMilliseconds.ShouldBe(50.0, 0.01);
    }

    [Test]
    public void PhaseLockedUpdateChecks_StartWorkWithinTwoIterations()
    {
        // The update thread checks once per 31 ms iteration, and each check falls inside a
        // render frame. Without the limit, the work would wait for as long as the phase holds.
        var policy = new AppThreadWorkDeferralPolicy(Limit);
        int iteration = 0;
        while (!policy.ShouldStartDuringRenderFrame(true, iteration * 31 * Millisecond))
            ++iteration;

        iteration.ShouldBe(2);
    }

    [Test]
    public void StartedWork_BeginsANewDeferral()
    {
        var policy = new AppThreadWorkDeferralPolicy(Limit);
        policy.ShouldStartDuringRenderFrame(true, 0).ShouldBeFalse();
        policy.ShouldStartDuringRenderFrame(true, Limit).ShouldBeTrue();
        policy.OnWorkStarted();

        // New work queued later waits for its own full deferral.
        long next = 3 * Limit;
        policy.ShouldStartDuringRenderFrame(true, next).ShouldBeFalse();
        policy.ShouldStartDuringRenderFrame(true, next + Limit - Millisecond).ShouldBeFalse();
        policy.ShouldStartDuringRenderFrame(true, next + Limit).ShouldBeTrue();
    }

    [Test]
    public void EmptiedQueue_EndsTheDeferral()
    {
        var policy = new AppThreadWorkDeferralPolicy(Limit);
        policy.ShouldStartDuringRenderFrame(true, 0).ShouldBeFalse();
        policy.ShouldStartDuringRenderFrame(false, 40 * Millisecond).ShouldBeFalse();

        // The queue emptied, so the deferral restarts with the next queued work.
        policy.ShouldStartDuringRenderFrame(true, 60 * Millisecond).ShouldBeFalse();
        policy.ShouldStartDuringRenderFrame(true, 109 * Millisecond).ShouldBeFalse();
        policy.ShouldStartDuringRenderFrame(true, 110 * Millisecond).ShouldBeTrue();
    }

    [Test]
    public void Capture_ReportsTheLongestDeferral()
    {
        var policy = new AppThreadWorkDeferralPolicy(Limit);
        policy.ShouldStartDuringRenderFrame(true, 0).ShouldBeFalse();
        // A long update iteration delays the next check past the limit.
        policy.ShouldStartDuringRenderFrame(true, 300 * Millisecond).ShouldBeTrue();
        policy.OnWorkStarted();
        policy.ShouldStartDuringRenderFrame(true, 400 * Millisecond).ShouldBeFalse();
        policy.ShouldStartDuringRenderFrame(true, 460 * Millisecond).ShouldBeTrue();

        policy.Capture().MaximumDeferralMilliseconds.ShouldBe(300.0, 0.01);
    }
}
