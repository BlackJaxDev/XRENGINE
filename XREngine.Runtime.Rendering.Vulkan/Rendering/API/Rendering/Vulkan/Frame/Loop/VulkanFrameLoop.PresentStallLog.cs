using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// The time without a completed desktop frame after which the frame loop writes a
    /// <c>PresentStalled</c> line. A healthy loop completes a frame every display interval.
    /// </summary>
    private static readonly long PresentStallReportTicks = Stopwatch.Frequency * 2;

    /// <summary>The minimum time between two <c>PresentStalled</c> lines of one stall.</summary>
    private static readonly long PresentStallRepeatTicks = Stopwatch.Frequency * 5;

    /// <summary>The timestamp of the last completed desktop frame, or 0 before the first one.</summary>
    private long _lastCompletedDesktopFrameTimestamp;

    /// <summary>The timestamp of the last <c>PresentStalled</c> line, or 0 when no stall is open.</summary>
    private long _lastPresentStallReportTimestamp;

    /// <summary>The number of desktop frame attempts since the last completed frame.</summary>
    private long _desktopAttemptsWithoutPresent;

    /// <summary>
    /// Records the outcome of one desktop frame attempt, and writes a line to the PresentNow
    /// auxiliary log when no frame has completed for <see cref="PresentStallReportTicks"/>. A frame
    /// that is rejected and retried each callback (for example a readiness retry or a deferred
    /// recording) never raises a failure, so without this record a Release session that stops
    /// presenting shows no cause. The line names the reason, failure stage, and readiness retry
    /// of the latest attempt. A completed frame after a stall writes <c>PresentResumed</c>.
    /// </summary>
    /// <remarks>
    /// The per-attempt cost is one timestamp read and a few field writes. Strings are formatted
    /// only when a line is written, at most once per <see cref="PresentStallRepeatTicks"/>.
    /// Stalls before the first completed frame are not reported, because startup loads defer many frames.
    /// </remarks>
    private void TrackDesktopPresentStall(
        ref VulkanFrameAttempt attempt,
        EVulkanFrameOutcome outcome,
        in VulkanDesktopFrameFailure failure)
    {
        long now = Stopwatch.GetTimestamp();
        if (outcome == EVulkanFrameOutcome.Completed)
        {
            if (_lastPresentStallReportTimestamp != 0)
            {
                WritePresentNowAuxiliaryLog(
                    $"PresentResumed frame={attempt.FrameNumber} " +
                    $"attemptsWithoutPresent={_desktopAttemptsWithoutPresent} " +
                    $"stalledMs={PresentStallMilliseconds(_lastCompletedDesktopFrameTimestamp, now):F0}");
            }
            _lastCompletedDesktopFrameTimestamp = now;
            _lastPresentStallReportTimestamp = 0;
            _desktopAttemptsWithoutPresent = 0;
            return;
        }

        _desktopAttemptsWithoutPresent++;
        if (_lastCompletedDesktopFrameTimestamp == 0 ||
            attempt.Reason == EDesktopFrameReason.ZeroSurface ||
            now - _lastCompletedDesktopFrameTimestamp < PresentStallReportTicks)
            return;
        if (_lastPresentStallReportTimestamp != 0 &&
            now - _lastPresentStallReportTimestamp < PresentStallRepeatTicks)
            return;

        _lastPresentStallReportTimestamp = now;
        VulkanPresentNowReadinessRetry retry = attempt.PresentNowReadinessRetry;
        string retryText = retry.IsValid
            ? $"retryStage={retry.Stage} retryTicket='{retry.ActiveTicket}' " +
              $"retryDependency='{retry.DependencyChain}' retryDetail={retry.Detail}"
            : "retry=<none>";
        WritePresentNowAuxiliaryLog(
            $"PresentStalled frame={attempt.FrameNumber} " +
            $"attemptsWithoutPresent={_desktopAttemptsWithoutPresent} " +
            $"stalledMs={PresentStallMilliseconds(_lastCompletedDesktopFrameTimestamp, now):F0} " +
            $"outcome={outcome} reason={attempt.Reason} failureKind={failure.Kind} " +
            $"failureStage={failure.Stage} native={failure.NativeResult} " +
            $"exception={failure.ExceptionType ?? "<none>"} detail={failure.Detail ?? "<none>"} " +
            $"recordingDeferral={attempt.RecordingDeferralDetail ?? "<none>"} {retryText}");
    }

    private static double PresentStallMilliseconds(long startTimestamp, long endTimestamp)
        => (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
}
