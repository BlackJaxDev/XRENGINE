using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Drives <see cref="TextureStreamingWorkController"/> with a fake frame clock and fake item costs. Each simulated
/// frame lasts <c>max(targetPeriod, baseBusy + items * cost)</c>, as with a frame limiter, so a frame goes over the
/// budget only when the streaming work does not fit in the idle time.
/// </summary>
[TestFixture]
public sealed class TextureStreamingWorkControllerTests
{
    private const double Period60Hz = 1000.0 / 60.0;
    private const double Tolerance = 2.0;

    private readonly record struct Frame(int Items, double FrameMilliseconds);

    private static TextureStreamingWorkController CreateController(
        int initial = 1,
        int max = 16,
        int? pinned = null,
        int? cap = null)
        => new(new TextureStreamingWorkControllerOptions(initial, max, Tolerance, pinned, cap));

    /// <summary>Runs idle frames (no streaming items) so the controller learns the budget and the idle busy time.</summary>
    private static void RunIdle(TextureStreamingWorkController controller, int frames, double baseBusy, double period = Period60Hz)
    {
        for (int i = 0; i < frames; i++)
        {
            double frame = Math.Max(period, baseBusy);
            controller.Update(new TextureStreamingWorkSample(frame, baseBusy, period, 0.0, 0, false, 0.5));
        }
    }

    /// <summary>Runs frames with a full backlog: each frame does as many items as the controller allows.</summary>
    private static Frame[] RunBacklog(
        TextureStreamingWorkController controller,
        int frames,
        double baseBusy,
        Func<int, double> costAt,
        double share = 0.5,
        double period = Period60Hz)
    {
        Frame[] result = new Frame[frames];
        for (int i = 0; i < frames; i++)
        {
            int items = controller.ItemsThisFrame;
            double work = items * costAt(i);
            double busy = baseBusy + work;
            double frame = Math.Max(period, busy);
            result[i] = new Frame(items, frame);
            controller.Update(new TextureStreamingWorkSample(frame, busy, period, work, items, true, share));
        }

        return result;
    }

    /// <summary>Runs frames of a shot that costs <paramref name="sceneMilliseconds"/>, with work in each frame that can do it.</summary>
    private static Frame[] RunShot(TextureStreamingWorkController controller, int frames, double sceneMilliseconds, double costPerItem)
    {
        Frame[] result = new Frame[frames];
        for (int i = 0; i < frames; i++)
        {
            int items = controller.ItemsThisFrame;
            double work = items * costPerItem;
            double frame = sceneMilliseconds + work;
            result[i] = new Frame(items, frame);
            controller.Update(new TextureStreamingWorkSample(frame, frame, Period60Hz, work, items, true, 1.0));
        }

        return result;
    }

    /// <summary>
    /// Runs a backlog in which each item adds <paramref name="hiddenPerItem"/> to the frame that no timer sees (the timer
    /// sees 0.05 ms), and every <paramref name="idleEvery"/>-th frame has no work (zero for no such frames). It returns the
    /// frames and the highest budget that the controller used.
    /// </summary>
    private static (Frame[] Frames, double MaxBudget) RunHiddenCost(
        TextureStreamingWorkController controller,
        int frames,
        double baseBusy,
        double hiddenPerItem,
        int idleEvery,
        double target)
    {
        Frame[] result = new Frame[frames];
        double maxBudget = 0.0;
        for (int i = 0; i < frames; i++)
        {
            bool idle = idleEvery > 0 && i % idleEvery == 0;
            int items = idle ? 0 : controller.ItemsThisFrame;
            double busy = baseBusy + items * hiddenPerItem;
            double frame = Math.Max(Period60Hz, busy);
            result[i] = new Frame(items, frame);
            controller.Update(new TextureStreamingWorkSample(frame, busy, target, items * 0.05, items, !idle, 0.5));
            maxBudget = Math.Max(maxBudget, controller.BudgetMilliseconds);
        }

        return (result, maxBudget);
    }

    [TestCase(3, Period60Hz)]
    [TestCase(0, Period60Hz)]
    [TestCase(3, 0.0)]
    [TestCase(0, 0.0)]
    public void HiddenCost_DoesNotRaiseTheBudget_AndBacksOff(int idleEvery, double target)
    {
        // Each item adds 3 ms that no timer sees, for ten seconds. A frame with work never raises the budget, so the budget
        // stays at the frame interval (the target, or the present interval when there is no target). The frames over the
        // rule count against the work, so the limit backs off to the items that fit.
        TextureStreamingWorkController controller = CreateController(initial: 4);

        (Frame[] frames, double maxBudget) = RunHiddenCost(controller, 600, baseBusy: 10.0, hiddenPerItem: 3.0, idleEvery, target);

        maxBudget.ShouldBeLessThanOrEqualTo(Period60Hz + Tolerance + 1e-9);
        controller.BudgetMilliseconds.ShouldBeLessThan(Period60Hz + 0.5);
        controller.BackoffCount.ShouldBeGreaterThan(0);
        Frame[] settled = frames[60..];
        settled.Count(f => f.FrameMilliseconds > Period60Hz + Tolerance).ShouldBeLessThanOrEqualTo(settled.Length / 20);
        settled.Max(f => f.Items).ShouldBeLessThanOrEqualTo(3);
    }

    [Test]
    public void LowCapacity_SlowUploadsThatFit_StayInsideTheFrameRule()
    {
        // A slow machine: 14 ms of a 16.7 ms frame is used, and one upload costs 2.5 ms.
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 30, baseBusy: 14.0);

        Frame[] frames = RunBacklog(controller, 600, baseBusy: 14.0, costAt: _ => 2.5);

        frames.Max(f => f.FrameMilliseconds).ShouldBeLessThanOrEqualTo(Period60Hz + Tolerance);
        frames.Max(f => f.Items).ShouldBe(1);
        controller.BackoffCount.ShouldBe(0);
    }

    [Test]
    public void LowCapacity_UploadLargerThanTheHeadroom_RunsOnlyAsOftenAsTheTolerancePays()
    {
        // One upload costs 6 ms on a frame that has 2.7 ms idle, so each upload frame goes over the rule.
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 30, baseBusy: 14.0);

        Frame[] frames = RunBacklog(controller, 900, baseBusy: 14.0, costAt: _ => 6.0);

        // After a repeated back-off, the floor is tolerance / cost = 1/3 item per frame.
        Frame[] settled = frames[300..];
        settled.Max(f => f.Items).ShouldBe(1);
        double addedPerFrame = settled.Average(f => f.FrameMilliseconds - Period60Hz);
        addedPerFrame.ShouldBeLessThanOrEqualTo(Tolerance + 0.1);
        settled.Count(f => f.Items > 0).ShouldBeGreaterThan(0);
        controller.ItemsPerFrame.ShouldBeLessThan(1.0);
    }

    [Test]
    public void HighCapacity_FastUploads_RampUpAboveTheOldFixedLimit()
    {
        // A fast machine: 6 ms of a 16.7 ms frame is used, and one upload costs 0.4 ms.
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 30, baseBusy: 6.0);

        Frame[] frames = RunBacklog(controller, 120, baseBusy: 6.0, costAt: _ => 0.4);

        frames.Max(f => f.FrameMilliseconds).ShouldBeLessThanOrEqualTo(Period60Hz + Tolerance);
        controller.ItemsThisFrame.ShouldBeGreaterThanOrEqualTo(8);
        frames[^1].Items.ShouldBeGreaterThan(4);
    }

    [Test]
    public void HighCapacity_CutsceneShare_UsesMoreOfTheHeadroom()
    {
        TextureStreamingWorkController normal = CreateController(max: 64);
        TextureStreamingWorkController cutscene = CreateController(max: 64);
        RunIdle(normal, 30, baseBusy: 6.0);
        RunIdle(cutscene, 30, baseBusy: 6.0);

        Frame[] normalFrames = RunBacklog(normal, 200, baseBusy: 6.0, costAt: _ => 0.4, share: 0.5);
        Frame[] cutsceneFrames = RunBacklog(cutscene, 200, baseBusy: 6.0, costAt: _ => 0.4, share: 1.0);

        cutscene.ItemsThisFrame.ShouldBeGreaterThan(normal.ItemsThisFrame);
        normalFrames.Max(f => f.FrameMilliseconds).ShouldBeLessThanOrEqualTo(Period60Hz + Tolerance);
        cutsceneFrames.Max(f => f.FrameMilliseconds).ShouldBeLessThanOrEqualTo(Period60Hz + Tolerance);
    }

    [Test]
    public void SuddenCostSpike_BacksOffWithinFourFrames()
    {
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 30, baseBusy: 6.0);
        RunBacklog(controller, 120, baseBusy: 6.0, costAt: _ => 0.4);
        int before = controller.ItemsThisFrame;
        before.ShouldBeGreaterThanOrEqualTo(8);

        // The cost of each upload jumps tenfold, for example when a larger mip tier starts to stream.
        Frame[] frames = RunBacklog(controller, 60, baseBusy: 6.0, costAt: _ => 4.0);

        // Each over-budget frame halves the limit and gives a cost sample, so the frames are inside the rule again after at
        // most four frames.
        frames[0].FrameMilliseconds.ShouldBeGreaterThan(Period60Hz + Tolerance);
        frames[4..].Max(f => f.FrameMilliseconds).ShouldBeLessThanOrEqualTo(Period60Hz + Tolerance);
        controller.ItemsThisFrame.ShouldBeLessThan(before);
    }

    [Test]
    public void SteadyState_DoesNotOscillate()
    {
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 30, baseBusy: 8.0);

        Frame[] frames = RunBacklog(controller, 900, baseBusy: 8.0, costAt: _ => 0.7);

        int changes = 0;
        for (int i = 301; i < frames.Length; i++)
        {
            if (frames[i].Items != frames[i - 1].Items)
                changes++;
        }

        changes.ShouldBe(0);
        controller.OverrunCount.ShouldBe(0);
    }

    [Test]
    public void SteadyState_WithCostJitter_KeepsTheLimitStable()
    {
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 30, baseBusy: 8.0);

        // A deterministic jitter of up to 10 percent around 0.7 ms.
        uint state = 12345u;
        double Jitter(int _)
        {
            state = state * 1664525u + 1013904223u;
            return 0.7 * (0.9 + 0.2 * (state >> 8) / (double)(1u << 24));
        }

        Frame[] frames = RunBacklog(controller, 900, baseBusy: 8.0, costAt: Jitter);

        int changes = 0;
        for (int i = 301; i < frames.Length; i++)
        {
            if (frames[i].Items != frames[i - 1].Items)
                changes++;
        }

        changes.ShouldBeLessThanOrEqualTo(1);
        frames.Max(f => f.FrameMilliseconds).ShouldBeLessThanOrEqualTo(Period60Hz + Tolerance);
    }

    [Test]
    public void ExplicitOverride_PinsTheLimit()
    {
        TextureStreamingWorkController controller = CreateController(pinned: 3);
        RunIdle(controller, 30, baseBusy: 6.0);

        Frame[] frames = RunBacklog(controller, 200, baseBusy: 6.0, costAt: i => i < 100 ? 0.2 : 9.0);

        frames.ShouldAllBe(f => f.Items == 3);
        controller.ItemsThisFrame.ShouldBe(3);
    }

    [Test]
    public void ExplicitOverride_IsNotHeldBackByAProbe()
    {
        TextureStreamingWorkController controller = CreateController(pinned: 2);

        controller.ItemsThisFrame.ShouldBe(2);
        controller.MarkDiscontinuity();
        controller.ItemsThisFrame.ShouldBe(2);
    }

    [Test]
    public void ExplicitCap_BoundsTheAdaptiveLimit()
    {
        TextureStreamingWorkController controller = CreateController(cap: 2);
        RunIdle(controller, 30, baseBusy: 4.0);

        Frame[] frames = RunBacklog(controller, 300, baseBusy: 4.0, costAt: _ => 0.1);

        frames.Max(f => f.Items).ShouldBe(2);
    }

    [Test]
    public void FirstFrames_AreAProbeWithoutWork()
    {
        TextureStreamingWorkController controller = CreateController(initial: 4);

        Frame[] frames = RunBacklog(controller, 4, baseBusy: 6.0, costAt: _ => 0.2);

        frames[0].Items.ShouldBe(0);
        frames[1].Items.ShouldBe(0);
        frames[2].Items.ShouldBe(4);
        controller.BudgetMilliseconds.ShouldBe(Period60Hz, tolerance: 1e-9);
    }

    [Test]
    public void OverBudgetFrame_CausedByAnotherStall_BacksOffOnceButSetsNoFractionalRate()
    {
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 90, baseBusy: 8.0);

        // One upload of 0.5 ms runs in a frame that a 30 ms stall elsewhere made long. The frame counts against the work,
        // but one excess sample does not move the robust cost, and one back-off sets no ceiling.
        controller.Update(new TextureStreamingWorkSample(38.0, 38.0, Period60Hz, 0.5, 1, true, 0.5));

        controller.OverrunCount.ShouldBe(1);
        controller.BackoffCount.ShouldBe(1);
        controller.CeilingItems.ShouldBe(0);

        // The probe after the overrun measures the normal frame again, so the budget does not move.
        controller.IsProbing.ShouldBeTrue();
        RunIdle(controller, 4, baseBusy: 8.0);
        controller.ItemsPerFrame.ShouldBeGreaterThanOrEqualTo(1.0);
        controller.BudgetMilliseconds.ShouldBeLessThan(Period60Hz + 0.5);

        // Normal items push the excess sample out of the robust cost.
        RunBacklog(controller, 16, baseBusy: 8.0, costAt: _ => 0.5);
        controller.CostPerItemMilliseconds.ShouldBeLessThan(1.0);
    }

    [Test]
    public void HiddenCost_ThatTheCpuTimerMisses_StillHoldsTheLimitNearOnePerFrame()
    {
        // The CPU timer sees 0.5 ms for each upload, but each upload adds 8 ms to the frame (for example, driver work at
        // the next present). The frame excess is a cost sample, so the model does not allow about ten uploads.
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 30, baseBusy: 10.0);

        Frame[] frames = new Frame[1200];
        for (int i = 0; i < frames.Length; i++)
        {
            int items = controller.ItemsThisFrame;
            double busy = 10.0 + items * 8.0;
            double frame = Math.Max(Period60Hz, busy);
            frames[i] = new Frame(items, frame);
            controller.Update(new TextureStreamingWorkSample(frame, busy, Period60Hz, items * 0.5, items, true, 0.5));
        }

        // Each probe of two items goes over and backs off; a repeated back-off sets a ceiling of one item for its lifetime,
        // so a new probe above one item occurs about once in each ceiling lifetime.
        Frame[] settled = frames[60..];
        settled.Count(f => f.FrameMilliseconds > Period60Hz + Tolerance).ShouldBeLessThanOrEqualTo(settled.Length / 20);
        settled.Max(f => f.Items).ShouldBeLessThanOrEqualTo(2);
        settled.Count(f => f.Items == 1).ShouldBeGreaterThan(settled.Length * 9 / 10);
    }

    [Test]
    public void SceneJitter_ThatIsTheSameWithoutStreaming_DoesNotThrottle()
    {
        // A GPU-bound scene runs at about 20 ms against a 16.7 ms target, with jitter of 17 to 23 ms in frames with and
        // without uploads. One upload costs 0.03 ms, so the work of a full frame stays inside the tolerance.
        TextureStreamingWorkController controller = CreateController(initial: 4);
        uint state = 777u;
        double Jitter()
        {
            state = state * 1664525u + 1013904223u;
            return 17.0 + 6.0 * (state >> 8) / (double)(1u << 24);
        }

        long settledBackoffs = 0;
        for (int i = 0; i < 900; i++)
        {
            if (i == 300)
                settledBackoffs = controller.BackoffCount;

            bool idle = i % 3 == 0;
            int items = idle ? 0 : controller.ItemsThisFrame;
            double work = items * 0.03;
            double frame = Jitter() + work;
            controller.Update(new TextureStreamingWorkSample(frame, frame, Period60Hz, work, items, !idle, 0.5));
        }

        // The scene is slower than the target, so the frame interval is the mean idle frame; the jitter causes no back-off.
        controller.BudgetMilliseconds.ShouldBeGreaterThan(Period60Hz + Tolerance);
        controller.BackoffCount.ShouldBe(settledBackoffs);
        controller.ItemsThisFrame.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Test]
    public void FixedCostOfEachWorkFrame_InsideTheTolerance_DoesNotPinTheLimitToOne()
    {
        // Each frame with publications pays 1 ms once (for example, a descriptor and command rebuild), and each item adds
        // 0.1 ms that the timer sees. More items in a frame do not add frames that pay the fixed cost.
        TextureStreamingWorkController controller = CreateController(initial: 1);
        int[] available = [1, 3, 2, 6, 1, 4];
        for (int i = 0; i < 900; i++)
        {
            bool idle = i % 4 == 0;
            int items = idle ? 0 : Math.Min(controller.ItemsThisFrame, available[i % available.Length]);
            double work = items * 0.1;
            double frame = 20.0 + (items > 0 ? 1.0 : 0.0) + work;
            controller.Update(new TextureStreamingWorkSample(frame, frame, Period60Hz, work, items, !idle, 0.5));
        }

        controller.ItemsThisFrame.ShouldBeGreaterThanOrEqualTo(4);
        controller.CeilingItems.ShouldBe(0);
    }

    [Test]
    public void CameraCut_Signaled_ProbesTheNewShotAndCountsNoItemCost()
    {
        // Before the cut the shot costs 16.7 ms. The cut moves to a 24 ms shot during a burst; each item costs 0.03 ms. The
        // signal starts a probe: two frames without work measure the new shot.
        TextureStreamingWorkController controller = CreateController(initial: 4);
        RunIdle(controller, 120, baseBusy: 8.0);
        controller.MarkDiscontinuity();
        controller.ItemsThisFrame.ShouldBe(0);

        Frame[] frames = RunShot(controller, 120, sceneMilliseconds: 24.0, costPerItem: 0.03);

        frames[..2].ShouldAllBe(f => f.Items == 0);
        controller.BackoffCount.ShouldBe(0);
        controller.CostPerItemMilliseconds.ShouldBeLessThan(0.1);
        controller.BudgetMilliseconds.ShouldBe(24.0, tolerance: 0.5);
        frames[^1].Items.ShouldBe(16);
    }

    [Test]
    public void CameraCut_Unsignaled_BacksOffThenLearnsTheShotFromFramesWithoutWork()
    {
        // The same cut without a signal, in the middle of a burst. Every frame with work goes over the rule, so the work
        // backs off; an overrun at one item starts a probe, which measures the new shot.
        TextureStreamingWorkController controller = CreateController(initial: 4);
        RunIdle(controller, 120, baseBusy: 8.0);
        RunBacklog(controller, 60, baseBusy: 8.0, costAt: _ => 0.03, share: 1.0);

        Frame[] frames = RunShot(controller, 240, sceneMilliseconds: 24.0, costPerItem: 0.03);

        controller.BackoffCount.ShouldBeGreaterThan(0);
        controller.BudgetMilliseconds.ShouldBe(24.0, tolerance: 0.5);
        frames[120..].Count(f => f.FrameMilliseconds > controller.BudgetMilliseconds + Tolerance).ShouldBe(0);
        frames[^1].Items.ShouldBe(16);
    }

    [Test]
    public void SignaledScrub_ProbesAtMostOnceInEachProbeInterval()
    {
        // A scrub signals a discontinuity in each frame. Streaming stops only for the probe frames.
        TextureStreamingWorkController controller = CreateController(initial: 4);
        RunIdle(controller, 120, baseBusy: 8.0);
        long probesBefore = controller.ProbeCount;
        int idle = 0;
        for (int i = 0; i < 300; i++)
        {
            controller.MarkDiscontinuity();
            int items = controller.ItemsThisFrame;
            if (items == 0)
                idle++;
            double work = items * 0.1;
            controller.Update(new TextureStreamingWorkSample(Period60Hz, 8.0 + work, Period60Hz, work, items, true, 0.5));
        }

        // 300 frames are five seconds, so at most six probes of two frames.
        idle.ShouldBeLessThanOrEqualTo(12);
        (controller.ProbeCount - probesBefore).ShouldBeLessThanOrEqualTo(6);
    }

    [Test]
    public void SingleCpuHitch_InOneItem_DoesNotSetAFloorOrACeiling()
    {
        // Steady at 1.5 ms for each apply. One item takes 50 ms (a preemption that the GC pause correction cannot see).
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 30, baseBusy: 6.0);
        RunBacklog(controller, 120, baseBusy: 6.0, costAt: _ => 1.5);
        int before = controller.ItemsThisFrame;
        before.ShouldBeGreaterThanOrEqualTo(2);

        int items = controller.ItemsThisFrame;
        double work = 50.0 + (items - 1) * 1.5;
        double frame = 6.0 + work;
        controller.Update(new TextureStreamingWorkSample(frame, frame, Period60Hz, work, items, true, 0.5));

        // One overrun halves the limit and holds it 250 ms; it sets no ceiling and keeps the floor at 1.
        controller.CeilingItems.ShouldBe(0);
        controller.ItemsPerFrame.ShouldBeGreaterThanOrEqualTo(1.0);
        controller.CostPerItemMilliseconds.ShouldBeLessThan(5.0);

        Frame[] after = RunBacklog(controller, 60, baseBusy: 6.0, costAt: _ => 1.5);
        after.Sum(f => f.Items).ShouldBeGreaterThan(60);
        controller.ItemsThisFrame.ShouldBe(before);
    }

    [Test]
    public void IdleGapWithALoadStall_DoesNotRaiseTheBudget()
    {
        TextureStreamingWorkController controller = CreateController();
        controller.ObserveIdleFrame(900.0, 900.0);
        RunIdle(controller, 30, baseBusy: 8.0);
        controller.ObserveIdleFrame(900.0, 900.0);

        controller.BudgetMilliseconds.ShouldBeLessThan(Period60Hz + Tolerance + 0.5);
    }

    [Test]
    public void SingleIdleStallFrame_DoesNotRaiseTheBudget()
    {
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 30, baseBusy: 8.0);

        controller.Update(new TextureStreamingWorkSample(300.0, 300.0, Period60Hz, 0.0, 0, false, 0.5));
        RunIdle(controller, 1, baseBusy: 8.0);

        controller.BudgetMilliseconds.ShouldBeLessThanOrEqualTo(Period60Hz + Tolerance + 1e-9);
    }

    [Test]
    public void LoadStall_DuringAProbe_DoesNotRaiseTheBudget()
    {
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 90, baseBusy: 8.0);
        controller.MarkDiscontinuity();

        controller.Update(new TextureStreamingWorkSample(900.0, 900.0, Period60Hz, 0.0, 0, false, 0.5));
        RunIdle(controller, 30, baseBusy: 8.0);

        controller.IsProbing.ShouldBeFalse();
        controller.BudgetMilliseconds.ShouldBeLessThan(Period60Hz + Tolerance);
    }

    [Test]
    public void SlowPeriodThatEnds_DuringAStreamingBurst_LetsTheBudgetComeBack()
    {
        // Another process slows every frame to 120 ms for a while; then the frames are normal again, with streaming
        // work in every frame.
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 30, baseBusy: 8.0);
        for (int i = 0; i < 60; i++)
        {
            int items = controller.ItemsThisFrame;
            controller.Update(new TextureStreamingWorkSample(120.0, 120.0, Period60Hz, 0.2 * items, items, true, 0.5));
        }

        RunBacklog(controller, 120, baseBusy: 8.0, costAt: _ => 0.2);

        controller.BudgetMilliseconds.ShouldBeLessThan(Period60Hz + Tolerance);
        controller.ItemsThisFrame.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Test]
    public void HiddenCost_AtTheStartOfABurst_BacksOffAtOnce()
    {
        // The burst starts at 6 items in every frame, and each item adds 3 ms that no timer sees.
        TextureStreamingWorkController controller = CreateController(initial: 6);
        RunIdle(controller, 30, baseBusy: 8.0);
        Frame[] frames = new Frame[180];
        for (int i = 0; i < frames.Length; i++)
        {
            int items = controller.ItemsThisFrame;
            double busy = 8.0 + items * 3.0;
            double frame = Math.Max(Period60Hz, busy);
            frames[i] = new Frame(items, frame);
            controller.Update(new TextureStreamingWorkSample(frame, busy, Period60Hz, items * 0.05, items, true, 0.5));
        }

        // The first frame over the rule halves the limit; a second back-off sets a ceiling, so the limit stays near the
        // items that fit, and a new probe above them occurs about once in each ceiling lifetime.
        frames[1].Items.ShouldBeLessThanOrEqualTo(3);
        frames[30..].Count(f => f.Items > 3).ShouldBeLessThanOrEqualTo(4);
        frames[30..].Count(f => f.FrameMilliseconds > Period60Hz + Tolerance).ShouldBeLessThanOrEqualTo(4);
    }

    [Test]
    public void UnknownFrameTime_UsesTheCostButCountsNoOverrun()
    {
        TextureStreamingWorkController controller = CreateController();
        RunIdle(controller, 30, baseBusy: 8.0);

        controller.Update(new TextureStreamingWorkSample(double.NaN, double.NaN, Period60Hz, 3.0, 1, true, 0.5));

        controller.OverrunCount.ShouldBe(0);
        controller.CostPerItemMilliseconds.ShouldBe(3.0);
    }

    [Test]
    public void NoTargetFrameRate_LearnsTheBudgetFromFramesWithoutWork()
    {
        TextureStreamingWorkController controller = CreateController();
        for (int i = 0; i < 60; i++)
            controller.Update(new TextureStreamingWorkSample(25.0, 24.0, 0.0, 0.0, 0, false, 0.5));

        controller.BudgetMilliseconds.ShouldBe(25.0, tolerance: 0.01);

        // A 27.5 ms frame with 3 ms of upload work is over the 25 + 2 ms rule.
        controller.Update(new TextureStreamingWorkSample(27.5, 27.0, 0.0, 3.0, 1, true, 0.5));
        controller.OverrunCount.ShouldBe(1);
    }
}
