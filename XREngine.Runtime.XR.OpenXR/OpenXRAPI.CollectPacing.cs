using System;
using System.Diagnostics;
using System.Threading;
using Silk.NET.OpenXR;
using XREngine;
using XREngine.Rendering;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// XR collect pacing: one collect and one publication for each XR frame.
///
/// Frame N+1 flows through these steps:
///   1. The render thread enters <c>xrEndFrame</c> for frame N (<see cref="EndFrameWithTiming"/>)
///      and sets <see cref="_collectReleaseEvent"/>. This is the release of a paced collect. The
///      release is at the entry, not the return: some runtimes (Monado) block in
///      <c>xrEndFrame</c> until the GPU work is done, others block in <c>xrWaitFrame</c>. From
///      the entry, the collect overlaps the block in both cases. The <c>finally</c> block of
///      <see cref="RenderFrameCore"/> sets the event again, for frames that end by an exception.
///   2. The collect thread leaves <see cref="WaitForCollectRelease"/> and collects. In single-pass
///      stereo it runs the speculative stereo collect for N+1, with the last pose and a wider
///      volume, while the render thread is still in <c>xrEndFrame</c> for N and the pacing
///      thread then blocks in <c>xrWaitFrame</c> for N+1.
///   3. The collect thread swaps (after the render thread released the buffers), then
///      <see cref="CompletePacedFramePublication"/> waits for a located frame newer than N
///      (<see cref="_pendingFrameEvent"/>; frame N stays pending until <c>xrEndFrame</c>
///      returns), assigns the speculative commands to it and sets <c>_framePrepared</c>. The
///      engine timer publishes the generation after this gate, so the render thread receives
///      frame N+1 with its commands in one publication.
///
/// Single-pass stereo with the speculative collect uses these steps while a focused session renders
/// eyes with the dedicated pacing thread. Two-pass eyes (sequential views and parallel command
/// buffer recording) collect with the located views, so a collect cannot overlap
/// <c>xrWaitFrame</c>. Their paced collect waits in step 2 for the next located frame instead,
/// then collects and publishes it. That wait also ends when the independent desktop output needs
/// its next frame. Without it, the loop collected and published after each render dispatch, about
/// 3.7 times for each XR frame in the per-eye benchmark, and only one of those publications fed the
/// eyes. Desktop priority keeps two-pass eyes on render pacing.
/// A frame without eyes, focus loss, a session stop or a runtime loss turns it off at once: the
/// waits return, and the collect loop goes back to render pacing (a collect after each render
/// dispatch). Every wait has a time limit, so a stalled frame cannot hold the collect thread.
/// </summary>
public unsafe partial class OpenXRAPI : IOpenXrCollectPacing
{
    /// <summary>Lower limit of a pacing wait, for runtimes that report no display period.</summary>
    private const double CollectPacingMinimumWaitMs = 12.0;

    /// <summary>Upper limit of a pacing wait. A longer stall uses render pacing for that collect.</summary>
    private const double CollectPacingMaximumWaitMs = 100.0;

    /// <summary>Length of one wait slice. Each slice checks again that pacing is still active.</summary>
    private const int CollectPacingWaitSliceMs = 2;

    private static readonly bool? OpenXrCollectPacingOverride =
        XREnvironment.GetBooleanOverride(XREngineEnvironmentVariables.OpenXrCollectPacing);

    /// <summary>
    /// False when <see cref="XREngineEnvironmentVariables.OpenXrTwoPassCollectPacing"/> is 0: two-pass
    /// eyes then keep render pacing while collect pacing is on.
    /// </summary>
    private static readonly bool OpenXrTwoPassCollectPacingEnabled =
        XREnvironment.GetBooleanOverride(XREngineEnvironmentVariables.OpenXrTwoPassCollectPacing) ?? true;

    /// <summary>
    /// Set by the render thread when it ends the XR frame of a publication (also without layers).
    /// The collect thread resets it when it publishes the next XR frame, before that frame can end.
    /// </summary>
    private readonly ManualResetEventSlim _collectReleaseEvent = new(initialState: false);

    /// <summary>
    /// Set by the pacing owner after it writes <c>_pendingXrFrame = 1</c>. A waiter that finds it
    /// set while no frame is pending resets it and checks the flag again.
    /// </summary>
    private readonly ManualResetEventSlim _pendingFrameEvent = new(initialState: false);

    /// <summary>
    /// Collect-thread state: 1 when the last publication carried an XR frame, so the next collect
    /// waits until the render thread ends that frame.
    /// </summary>
    private int _collectPacingArmed;

    /// <summary>1 when the last ended XR frame submitted its eye layer; 0 after a frame without eyes.</summary>
    private int _lastXrFrameSubmittedEyes;

    /// <summary>Lifecycle frame number of the frame whose <c>xrEndFrame</c> released the collect.</summary>
    private int _collectReleasedFrameNumber;

    /// <summary>Collect-thread state: true when the current collect started under XR collect pacing.</summary>
    private bool _currentCollectPaced;

    private const int SlackHistoryLength = 4;

    /// <summary>
    /// Slack after <c>xrEndFrame</c> of the last completed frames, in stopwatch ticks: the time from
    /// the <c>xrEndFrame</c> return to the next display period boundary of that frame. Render thread
    /// writes; the collect thread reads the minimum. -1 marks an empty slot.
    /// </summary>
    private readonly long[] _slackAfterEndFrameTicks = [-1L, -1L, -1L, -1L];
    private int _slackAfterEndFrameIndex;

    /// <summary>Wake-to-end work of the last completed frames, in stopwatch ticks. Render thread writes; 0 marks an empty slot.</summary>
    private readonly long[] _recentFrameWorkTicks = new long[SlackHistoryLength];
    private int _recentFrameWorkIndex;

    /// <summary>
    /// Time from the <c>xrEndFrame</c> return to the start of a desktop render in the same dispatch,
    /// for the last desktop renders, in stopwatch ticks. Render thread only. -1 marks an empty slot.
    /// </summary>
    private readonly long[] _desktopLeadTicks = [-1L, -1L, -1L, -1L];
    private int _desktopLeadIndex;
    private long _lastEndFrameReturnTimestamp;

    /// <summary>Collect-thread state: head render matrix that the last speculative stereo collect culled with.</summary>
    private System.Numerics.Matrix4x4 _speculativeCullHeadMatrix;
    private bool _speculativeCullHeadValid;

    /// <summary>1 when the last eye publication was a true single-pass stereo swap; 0 after a two-pass swap.</summary>
    private int _lastPublishedTrueSinglePassStereo;

    /// <summary>1 when the last eye publication was a two-pass swap (left and right eye viewports); 0 after a single-pass swap.</summary>
    private int _lastPublishedTwoPassEyes;

    /// <summary>Last measured time from <c>xrWaitFrame</c> return to <c>xrEndFrame</c> return, in stopwatch ticks.</summary>
    private long LastXrFrameWorkTicks => Volatile.Read(ref _lastFrameWorkTicks);

    private static bool IsOpenXrCollectPacingEnabled
        => OpenXrCollectPacingOverride ?? RuntimeEngine.Rendering.Settings.OpenXrCollectPacing;

    /// <summary>
    /// True while the collect loop paces to XR frames: the setting is on, the last eye publication
    /// can be paced (see <see cref="IsSinglePassPacingEligible"/> and
    /// <see cref="IsTwoPassPacingEligible"/>), the dedicated pacing thread runs, the session is
    /// begun and focused, no runtime loss is pending, and the last ended XR frame submitted its eyes.
    /// </summary>
    private bool IsCollectPacingActive
        => IsOpenXrCollectPacingEnabled &&
           (IsSinglePassPacingEligible || IsTwoPassPacingEligible) &&
           OpenXrRenderPacingHandling == global::XREngine.Rendering.API.Rendering.OpenXR.OpenXrRenderPacingMode.DedicatedThread &&
           _openXrPacingThread is not null &&
           _sessionBegun &&
           _sessionState == SessionState.Focused &&
           Volatile.Read(ref _lastXrFrameSubmittedEyes) != 0 &&
           !IsOpenXrRuntimeLossPending();

    /// <summary>
    /// True when the last eye publication used true single-pass stereo with the speculative
    /// collect. That collect runs before <c>xrWaitFrame</c> returns.
    /// </summary>
    private bool IsSinglePassPacingEligible
        => OpenXrSpeculativeCollectEnabled &&
           Volatile.Read(ref _lastPublishedTrueSinglePassStereo) != 0;

    /// <summary>
    /// True when the last eye publication was a two-pass swap and the independent desktop output
    /// waits for XR frames. Two-pass eyes collect with the located views, so a paced two-pass
    /// collect waits for the next located frame instead of running after each render dispatch.
    /// Desktop priority (a due time of 0) keeps render pacing, because each desktop frame needs a
    /// collect of its own.
    /// </summary>
    private bool IsTwoPassPacingEligible
        => OpenXrTwoPassCollectPacingEnabled &&
           Volatile.Read(ref _lastPublishedTwoPassEyes) != 0 &&
           RuntimeRenderingHostServices.Presentation.VrIndependentDesktopNextDueTimestamp != 0L;

    /// <summary>
    /// True when the next collect can run before <c>xrWaitFrame</c> returns: the last swap
    /// published single-pass stereo commands, so the updating buffer is free for the speculative
    /// collect. It is false after a speculative collect could not be assigned to its frame, and
    /// after a two-pass swap; the next collect is then a full collect with the located views.
    /// </summary>
    private bool CanCollectAheadOfFrameWait
        => _openXrStereoUpdatingBufferFree &&
           Volatile.Read(ref _lastPublishedTrueSinglePassStereo) != 0;

    long IOpenXrCollectPacing.DisplayPeriodTicks
        => Volatile.Read(ref _lastFramePeriodNanoseconds) * Stopwatch.Frequency / 1_000_000_000L;

    EOpenXrCollectPacingWait IOpenXrCollectPacing.WaitForCollectRelease()
        => WaitForCollectRelease();

    bool IOpenXrCollectPacing.IsCurrentCollectPaced => _currentCollectPaced;

    double IOpenXrCollectPacing.PacedDesktopSlackMs => PacedDesktopSlackMs;

    void IOpenXrCollectPacing.NoteDesktopRenderStart()
        => NoteDesktopRenderStart();

    EOpenXrCollectPacingWait IOpenXrCollectPacing.CompletePacedFramePublication()
        => CompletePacedFramePublication();

    /// <inheritdoc cref="IOpenXrCollectPacing.WaitForCollectRelease"/>
    private EOpenXrCollectPacingWait WaitForCollectRelease()
    {
        _currentCollectPaced = false;
        if (!IsOpenXrCollectPacingEnabled)
        {
            _collectPacingArmed = 0;
            return EOpenXrCollectPacingWait.NotPaced;
        }

        if (!IsCollectPacingActive)
        {
            _collectPacingArmed = 0;
            if (_sessionBegun)
                XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.RenderPacedCollects);
            return EOpenXrCollectPacingWait.NotPaced;
        }

        XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.PacedCollects);
        bool twoPass = Volatile.Read(ref _lastPublishedTwoPassEyes) != 0;
        if (twoPass)
            XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.TwoPassPacedCollects);
        _currentCollectPaced = true;
        EOpenXrCollectPacingWait result = EOpenXrCollectPacingWait.Ready;
        if (_collectPacingArmed != 0)
        {
            _collectPacingArmed = 0;
            if (!_collectReleaseEvent.IsSet)
            {
                long start = Stopwatch.GetTimestamp();
                FramePacingTimeline.Mark(EFramePacingMark.CollectWaitXrSubmitStart);
                result = WaitForPacingEvent(_collectReleaseEvent, static _ => true);
                long end = Stopwatch.GetTimestamp();
                FramePacingTimeline.Mark(EFramePacingMark.CollectWaitXrSubmitEnd);
                XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.SubmitWaits);
                XrCollectPacingTelemetry.AddElapsed(EXrCollectPacingCounter.SubmitWaitMicroseconds, start, end);
                if (result == EOpenXrCollectPacingWait.TimedOut)
                    XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.SubmitWaitTimeouts);
                if (result != EOpenXrCollectPacingWait.Waited)
                {
                    // Render pacing for this collect: it runs now, without a paced admission.
                    _currentCollectPaced = false;
                    return result;
                }
            }
        }

        // A full stereo collect (after a speculative collect that could not be assigned) needs
        // the located views, so it waits for xrWaitFrame here. Otherwise the collect runs ahead
        // and the publication gate waits for the frame.
        if (CanCollectAheadOfFrameWait || IsNewerFramePending(this))
            return result;

        if (twoPass)
        {
            // Two-pass eyes collect with the located views. Wait for the next located frame, but
            // stop when the independent desktop output needs its next frame.
            XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.TwoPassFrameWaits);
            EOpenXrCollectPacingWait frameWait = WaitForPendingFrame(
                RuntimeRenderingHostServices.Presentation.VrIndependentDesktopNextDueTimestamp);
            if (frameWait is not (EOpenXrCollectPacingWait.Waited or EOpenXrCollectPacingWait.Ready))
                _currentCollectPaced = false;
            return frameWait;
        }

        XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.LocatedFrameWaitsBeforeCollect);
        return WaitForPendingFrame();
    }

    /// <inheritdoc cref="IOpenXrCollectPacing.CompletePacedFramePublication"/>
    private EOpenXrCollectPacingWait CompletePacedFramePublication()
    {
        if (!IsOpenXrCollectPacingEnabled || !IsCollectPacingActive)
            return EOpenXrCollectPacingWait.NotPaced;

        // The swap callback already assigned the frame (xrWaitFrame returned before the swap).
        if (Volatile.Read(ref _framePrepared) != 0)
        {
            ArmCollectPacing();
            return EOpenXrCollectPacingWait.Ready;
        }

        // Nothing waits for a frame: the collect failed, or two-pass stereo found no frame.
        if (_openXrSpeculativeCollectState != EOpenXrSpeculativeCollectState.Ready)
        {
            XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.PublicationsWithoutXrFrame);
            return EOpenXrCollectPacingWait.Ready;
        }

        XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.PublicationFrameWaits);
        EOpenXrCollectPacingWait result = WaitForPendingFrame();
        if (result is not (EOpenXrCollectPacingWait.Waited or EOpenXrCollectPacingWait.Ready))
        {
            XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.PublicationsWithoutXrFrame);
            return result;
        }

        // Runs the same publication as the swap callback: assigns the speculative commands to the
        // located frame, swaps the stereo command buffers and sets _framePrepared.
        OpenXrSwapBuffers();
        if (Volatile.Read(ref _framePrepared) != 0)
            ArmCollectPacing();
        else
            XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.PublicationsWithoutXrFrame);
        return result;
    }

    /// <summary>
    /// Marks the publication as one that carries an XR frame. Resets the release event before the
    /// timer publishes, so the render thread cannot end this frame before the reset.
    /// </summary>
    private void ArmCollectPacing()
    {
        _collectReleaseEvent.Reset();
        _collectPacingArmed = 1;
        XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.PublicationsWithXrFrame);
    }

    /// <summary>
    /// Waits (bounded) until the pacing owner has located the next XR frame. A wait also stops at
    /// <paramref name="desktopDueTimestamp"/> (a stopwatch timestamp; <see cref="long.MaxValue"/>
    /// for none), so a two-pass collect does not hold the independent desktop output past its
    /// minimum rate.
    /// </summary>
    private EOpenXrCollectPacingWait WaitForPendingFrame(long desktopDueTimestamp = long.MaxValue)
    {
        if (IsNewerFramePending(this))
            return EOpenXrCollectPacingWait.Ready;

        long start = Stopwatch.GetTimestamp();
        FramePacingTimeline.Mark(EFramePacingMark.CollectWaitXrFrameStart);
        EOpenXrCollectPacingWait result = WaitForPacingEvent(
            _pendingFrameEvent,
            static api => IsNewerFramePending(api),
            desktopDueTimestamp);
        long end = Stopwatch.GetTimestamp();
        FramePacingTimeline.Mark(EFramePacingMark.CollectWaitXrFrameEnd);
        XrCollectPacingTelemetry.AddElapsed(EXrCollectPacingCounter.FrameWaitMicroseconds, start, end);
        if (result == EOpenXrCollectPacingWait.TimedOut)
            XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.FrameWaitTimeouts);
        else if (result == EOpenXrCollectPacingWait.DesktopDue)
            XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.TwoPassDesktopDueWakes);
        return result;
    }

    /// <summary>
    /// Waits in short slices until <paramref name="signal"/> is set and <paramref name="condition"/>
    /// holds, until pacing stops, or until the time limit. A set signal whose condition does not
    /// hold is reset and checked again, so a stale set cannot cause a busy loop.
    /// </summary>
    private EOpenXrCollectPacingWait WaitForPacingEvent(
        ManualResetEventSlim signal,
        Func<OpenXRAPI, bool> condition,
        long desktopDueTimestamp = long.MaxValue)
    {
        long limit = Stopwatch.GetTimestamp() + GetCollectPacingWaitLimitTicks();
        // A desktop due time before the pacing limit ends the wait with DesktopDue instead.
        bool desktopLimited = desktopDueTimestamp < limit;
        if (desktopLimited)
            limit = desktopDueTimestamp;
        while (true)
        {
            if (signal.IsSet)
            {
                if (condition(this))
                    return EOpenXrCollectPacingWait.Waited;

                signal.Reset();
                if (condition(this))
                    return EOpenXrCollectPacingWait.Waited;
            }

            if (!IsCollectPacingActive)
            {
                XrCollectPacingTelemetry.Count(EXrCollectPacingCounter.Deactivations);
                return EOpenXrCollectPacingWait.Deactivated;
            }

            long remaining = limit - Stopwatch.GetTimestamp();
            if (remaining <= 0L)
                return desktopLimited ? EOpenXrCollectPacingWait.DesktopDue : EOpenXrCollectPacingWait.TimedOut;

            int sliceMs = (int)Math.Clamp(remaining * 1000L / Stopwatch.Frequency, 1L, CollectPacingWaitSliceMs);
            signal.Wait(sliceMs);
        }
    }

    /// <summary>
    /// Time limit of one pacing wait: four display periods, or two times the largest XR frame work
    /// (wake to end) of the last frames when that is longer. A frame with cascade redraws can take
    /// much longer than the frame before it, so the limit uses the largest recent work, not the
    /// last one. The cap keeps a stalled frame from holding the collect thread.
    /// </summary>
    private long GetCollectPacingWaitLimitTicks()
    {
        double periodMs = Volatile.Read(ref _lastFramePeriodNanoseconds) / 1_000_000.0;
        long largestWorkTicks = LastXrFrameWorkTicks;
        for (int i = 0; i < SlackHistoryLength; i++)
            largestWorkTicks = Math.Max(largestWorkTicks, Volatile.Read(ref _recentFrameWorkTicks[i]));
        double workMs = largestWorkTicks * 1000.0 / Stopwatch.Frequency;
        double limitMs = Math.Clamp(
            Math.Max(4.0 * periodMs, 2.0 * workMs),
            CollectPacingMinimumWaitMs,
            CollectPacingMaximumWaitMs);
        return (long)(limitMs * Stopwatch.Frequency / 1000.0);
    }

    /// <summary>
    /// True when a located frame newer than the frame that released the collect is pending. The
    /// released frame stays pending while the render thread is in its <c>xrEndFrame</c>.
    /// </summary>
    private static bool IsNewerFramePending(OpenXRAPI api)
        => Volatile.Read(ref api._pendingXrFrame) != 0 &&
           Volatile.Read(ref api._openXrPendingFrameNumber) > Volatile.Read(ref api._collectReleasedFrameNumber);

    /// <summary>
    /// The release point of a paced collect: the render thread enters <c>xrEndFrame</c> for the
    /// frame that it took. Called at the start of <see cref="EndFrameWithTiming"/>.
    /// </summary>
    private void ReleasePacedCollectAtSubmit()
    {
        Volatile.Write(ref _collectReleasedFrameNumber, Volatile.Read(ref _openXrPendingFrameNumber));
        _collectReleaseEvent.Set();
    }

    /// <summary>
    /// Records the result of an ended XR frame for collect pacing and sets the release again, for a
    /// frame that ended by an exception before <c>xrEndFrame</c>. Called once from the
    /// <c>finally</c> block of <see cref="RenderFrameCore"/>, which runs for every frame that the
    /// render thread took (with or without layers).
    /// </summary>
    private void ReleasePacedCollect(bool submittedEyes)
    {
        Volatile.Write(ref _lastXrFrameSubmittedEyes, submittedEyes ? 1 : 0);
        _pendingFrameEvent.Reset();
        _collectReleaseEvent.Set();
    }

    /// <summary>
    /// Records the slack after <c>xrEndFrame</c> of a completed frame: the time from the return to
    /// the next display period boundary (counted from the <c>xrWaitFrame</c> return). Render thread.
    /// </summary>
    private void RecordSlackAfterEndFrame(long endFrameReturnTimestamp, long frameWorkTicks)
    {
        Volatile.Write(ref _lastEndFrameReturnTimestamp, endFrameReturnTimestamp);
        int workSlot = _recentFrameWorkIndex;
        Volatile.Write(ref _recentFrameWorkTicks[workSlot], Math.Max(0L, frameWorkTicks));
        _recentFrameWorkIndex = (workSlot + 1) % SlackHistoryLength;
        long periodTicks = Volatile.Read(ref _lastFramePeriodNanoseconds) * Stopwatch.Frequency / 1_000_000_000L;
        if (periodTicks <= 0L || frameWorkTicks <= 0L)
            return;

        long periods = Math.Max(1L, (frameWorkTicks + periodTicks - 1L) / periodTicks);
        long slack = periods * periodTicks - frameWorkTicks;
        int slot = _slackAfterEndFrameIndex;
        Volatile.Write(ref _slackAfterEndFrameTicks[slot], slack);
        _slackAfterEndFrameIndex = (slot + 1) % SlackHistoryLength;
    }

    /// <summary>
    /// Records the lead from the last <c>xrEndFrame</c> return to a desktop render start in the same
    /// dispatch. A desktop render that does not follow an XR frame end closely is not counted.
    /// Render thread; no allocation.
    /// </summary>
    private void NoteDesktopRenderStart()
    {
        long endReturn = Volatile.Read(ref _lastEndFrameReturnTimestamp);
        long periodTicks = Volatile.Read(ref _lastFramePeriodNanoseconds) * Stopwatch.Frequency / 1_000_000_000L;
        if (endReturn == 0L || periodTicks <= 0L)
            return;

        long lead = Stopwatch.GetTimestamp() - endReturn;
        if (lead < 0L || lead > periodTicks)
            return;

        int slot = _desktopLeadIndex;
        Volatile.Write(ref _desktopLeadTicks[slot], lead);
        _desktopLeadIndex = (slot + 1) % SlackHistoryLength;
    }

    /// <summary>
    /// Slack for the independent desktop admission when the collect is paced, in milliseconds, or
    /// -1 when unknown. A paced collect starts when the render thread enters <c>xrEndFrame</c>, and
    /// the desktop frame it admits renders after the next eye frame. So the value is the slack of
    /// that later window, estimated from completed frames: the median slack after
    /// <c>xrEndFrame</c> of the last four frames, minus the largest measured lead from the
    /// <c>xrEndFrame</c> return to the desktop render start. A replay of recorded frames chose the
    /// median: its admitted desktop rate was nearest to the render-paced rate, with fewer
    /// mispredicted admissions than render pacing. The minimum held the desktop too often, and the
    /// last value alone admitted too many frames. Collect thread; no allocation.
    /// </summary>
    private double PacedDesktopSlackMs
    {
        get
        {
            Span<long> slacks = stackalloc long[SlackHistoryLength];
            int count = 0;
            for (int i = 0; i < SlackHistoryLength; i++)
            {
                long value = Volatile.Read(ref _slackAfterEndFrameTicks[i]);
                if (value < 0L)
                    continue;

                // Insertion sort of at most four values.
                int position = count++;
                while (position > 0 && slacks[position - 1] > value)
                {
                    slacks[position] = slacks[position - 1];
                    position--;
                }
                slacks[position] = value;
            }
            if (count == 0)
                return -1.0;

            long median = (count & 1) != 0
                ? slacks[count / 2]
                : (slacks[count / 2 - 1] + slacks[count / 2]) / 2L;

            long maximumLead = 0L;
            for (int i = 0; i < SlackHistoryLength; i++)
                maximumLead = Math.Max(maximumLead, Volatile.Read(ref _desktopLeadTicks[i]));

            return Math.Max(0L, median - maximumLead) * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>Stores the head render matrix that a speculative stereo collect culled with. Collect thread.</summary>
    private void RecordSpeculativeCullHead(System.Numerics.Matrix4x4 headRenderMatrix)
    {
        _speculativeCullHeadMatrix = headRenderMatrix;
        _speculativeCullHeadValid = true;
    }

    /// <summary>
    /// Measures the angle between the head view of the speculative collect and the predicted head
    /// view of the frame that receives its commands (predicted head pose of that frame, placed by
    /// the current world matrix of the head's parent). Collect thread, when the commands are assigned.
    /// </summary>
    private void RecordSpeculativeViewAngle()
    {
        if (!_speculativeCullHeadValid)
            return;

        _speculativeCullHeadValid = false;
        var hmdNode = RuntimeEngine.VRState.ViewInformation.HMDNode;
        if (hmdNode is null)
            return;

        System.Numerics.Matrix4x4 predictedLocal;
        lock (_openXrPoseLock)
            predictedLocal = _openXrPredHeadLocalPose;
        System.Numerics.Matrix4x4 parentWorld = hmdNode.Transform.Parent?.WorldMatrix ?? System.Numerics.Matrix4x4.Identity;
        System.Numerics.Matrix4x4 finalHead = predictedLocal * parentWorld;
        System.Numerics.Vector3 a = -new System.Numerics.Vector3(_speculativeCullHeadMatrix.M31, _speculativeCullHeadMatrix.M32, _speculativeCullHeadMatrix.M33);
        System.Numerics.Vector3 b = -new System.Numerics.Vector3(finalHead.M31, finalHead.M32, finalHead.M33);
        if (a.LengthSquared() < 1e-8f || b.LengthSquared() < 1e-8f)
            return;

        double cosine = Math.Clamp(System.Numerics.Vector3.Dot(System.Numerics.Vector3.Normalize(a), System.Numerics.Vector3.Normalize(b)), -1.0f, 1.0f);
        SpeculativeViewAngleTelemetry.Record(Math.Acos(cosine) * (180.0 / Math.PI));
    }

    /// <summary>Publishes a located frame to a collect that waits for it.</summary>
    private void SignalPendingFrameForCollectPacing()
        => _pendingFrameEvent.Set();

    /// <summary>
    /// Turns collect pacing off until the next frame with eyes, and wakes both waits so a waiting
    /// collect returns at once. Called on session state changes, session stop and runtime loss.
    /// </summary>
    private void StopCollectPacing()
    {
        Volatile.Write(ref _lastXrFrameSubmittedEyes, 0);
        _collectReleaseEvent.Set();
        _pendingFrameEvent.Set();
    }
}
