using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free hybrid sleep/yield/spin limiter used only by the Mailbox
/// LowLatency profile. Deadlines advance monotonically to avoid drift.
/// </summary>
internal sealed class VulkanDesktopFramePacer
{
    private long _nextDeadlineTimestamp;
    private long _configuredIntervalTicks;

    internal void Pace(
        in VulkanPresentationProfileSnapshot profile,
        bool bypass,
        out TimeSpan sleepElapsed,
        out TimeSpan spinElapsed)
    {
        sleepElapsed = TimeSpan.Zero;
        spinElapsed = TimeSpan.Zero;
        if (bypass || !profile.LimiterEnabled || profile.TargetInterval <= TimeSpan.Zero)
        {
            Reset();
            return;
        }

        long intervalTicks = Math.Max(
            1L,
            (long)Math.Round(
                profile.TargetInterval.TotalSeconds * Stopwatch.Frequency));
        long now = Stopwatch.GetTimestamp();
        if (_configuredIntervalTicks != intervalTicks ||
            _nextDeadlineTimestamp == 0L ||
            now - _nextDeadlineTimestamp > intervalTicks * 4L)
        {
            _configuredIntervalTicks = intervalTicks;
            _nextDeadlineTimestamp = now + intervalTicks;
            return;
        }

        long deadline = _nextDeadlineTimestamp;
        if (now >= deadline)
        {
            AdvanceDeadline(now, intervalTicks);
            return;
        }

        double spinThresholdMilliseconds = Math.Clamp(
            RuntimeRenderingHostServices.Settings
                .VulkanPresentationLimiterSpinThresholdMilliseconds,
            0.0f,
            2.0f);
        long spinThresholdTicks = (long)Math.Round(
            spinThresholdMilliseconds * Stopwatch.Frequency / 1000.0);
        long sleepStart = Stopwatch.GetTimestamp();
        while (true)
        {
            now = Stopwatch.GetTimestamp();
            long remainingTicks = deadline - now;
            if (remainingTicks <= spinThresholdTicks)
                break;

            double remainingMilliseconds =
                remainingTicks * 1000.0 / Stopwatch.Frequency;
            int sleepMilliseconds = (int)Math.Floor(
                remainingMilliseconds - spinThresholdMilliseconds);
            if (sleepMilliseconds > 0)
                Thread.Sleep(sleepMilliseconds);
            else
                Thread.Yield();
        }
        sleepElapsed = Stopwatch.GetElapsedTime(sleepStart);

        long spinStart = Stopwatch.GetTimestamp();
        SpinWait spinner = default;
        while (Stopwatch.GetTimestamp() < deadline)
            spinner.SpinOnce(sleep1Threshold: -1);
        spinElapsed = Stopwatch.GetElapsedTime(spinStart);
        AdvanceDeadline(Stopwatch.GetTimestamp(), intervalTicks);
    }

    private void AdvanceDeadline(long now, long intervalTicks)
    {
        long next = _nextDeadlineTimestamp + intervalTicks;
        _nextDeadlineTimestamp = next > now
            ? next
            : now + intervalTicks;
    }

    private void Reset()
    {
        _nextDeadlineTimestamp = 0L;
        _configuredIntervalTicks = 0L;
    }
}
