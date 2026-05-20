using System;
using System.Diagnostics;
using System.Threading;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Dedicated OpenXR pacing-thread loop.
///
/// Used only when <see cref="OpenXRAPI.OpenXrRenderPacingMode.DedicatedThread"/> is selected.
///
/// The pacing thread owns the next-frame prep block (xrWaitFrame -> xrBeginFrame ->
/// xrLocateViews(Predicted) -> UpdateActionPoseCaches(Predicted) -> InvokeRecalcMatrixOnDraw(Predicted))
/// so the render thread does not block in xrWaitFrame inside its frame callback.
///
/// External-sync invariant with the render thread:
///   - Pacing thread runs the prep block once per frame, then sets _pendingXrFrame=1.
///   - Render thread consumes _pendingXrFrame inside its frame callback (LocateViews(Late),
///     RenderFrame, xrEndFrame), then calls <see cref="SignalPacingThreadFrameSubmitted"/>.
///   - Pacing thread waits on _openXrPacingWakeEvent before running the next iteration,
///     so xrWaitFrame on the pacing thread never overlaps xrEndFrame on the render thread.
///
/// The pacing thread runs at session-ready time and exits when stop is requested or the session ends.
/// </summary>
public unsafe partial class OpenXRAPI
{
    /// <summary>
    /// Ensures the OpenXR pacing thread is running (only meaningful when
    /// <see cref="OpenXrRenderPacingHandling"/> == <see cref="OpenXrRenderPacingMode.DedicatedThread"/>).
    /// Idempotent; safe to call every render tick.
    /// </summary>
    private void EnsureOpenXrPacingThreadStarted()
    {
        if (_openXrPacingThread is not null)
            return;
        if (!_sessionBegun)
            return;
        if (OpenXrRenderPacingHandling != OpenXrRenderPacingMode.DedicatedThread)
            return;

        Volatile.Write(ref _openXrPacingStopRequested, 0);
        // Wake event starts signaled so the very first iteration runs immediately (cold start).
        _openXrPacingWakeEvent.Set();

        var thread = new Thread(OpenXrPacingLoop)
        {
            Name = "XR Pacing",
            IsBackground = true
        };
        _openXrPacingThread = thread;
        thread.Start();
    }

    /// <summary>
    /// Signals the OpenXR pacing thread that the render thread has just submitted a frame
    /// (xrEndFrame complete). The pacing thread may now proceed with the next xrWaitFrame.
    /// </summary>
    private void SignalPacingThreadFrameSubmitted()
    {
        if (_openXrPacingThread is null)
            return;
        _openXrPacingWakeEvent.Set();
    }

    /// <summary>
    /// Requests the OpenXR pacing thread to exit and waits briefly for it to do so.
    /// Safe to call from session-end and CleanUp paths.
    /// </summary>
    private void StopOpenXrPacingThread()
    {
        var thread = _openXrPacingThread;
        if (thread is null)
            return;

        Volatile.Write(ref _openXrPacingStopRequested, 1);
        _openXrPacingWakeEvent.Set();

        try
        {
            // Give the thread a bounded window to drain; if it does not exit we simply abandon
            // the reference (the loop checks _openXrPacingStopRequested after each wake).
            if (!thread.Join(TimeSpan.FromMilliseconds(250)))
                Debug.LogWarning("OpenXR pacing thread did not exit within 250ms; abandoning reference.");
        }
        catch
        {
            // best-effort cleanup
        }
        finally
        {
            _openXrPacingThread = null;
            ClearOpenXrPacingThread();
            _openXrPacingWakeEvent.Reset();
            Volatile.Write(ref _openXrPacingStopRequested, 0);
        }
    }

    private void OpenXrPacingLoop()
    {
        MarkOpenXrPacingThread();

        try
        {
            while (Volatile.Read(ref _openXrPacingStopRequested) == 0)
            {
                long idleStart = Stopwatch.GetTimestamp();
                _openXrPacingWakeEvent.Wait();
                long idleEnd = Stopwatch.GetTimestamp();

                if (Volatile.Read(ref _openXrPacingStopRequested) != 0)
                    break;

                // Consume the signal so the next iteration parks until the render thread signals again.
                _openXrPacingWakeEvent.Reset();

                RuntimeEngine.Rendering.Stats.Vr.RecordVrXrPacingThreadIdleTime(
                    TimeSpan.FromSeconds((idleEnd - idleStart) / (double)Stopwatch.Frequency));

                if (!_sessionBegun)
                    continue;

                try
                {
                    // The prep block is thread-agnostic: WaitFrame/BeginFrame/LocateViews/UpdateActionPoseCaches
                    // use AssertOpenXrRenderThread which accepts either the render thread or this pacing thread.
                    PrepareNextFrameOnRenderThread();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"OpenXR pacing-thread prep iteration failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR pacing thread crashed: {ex.Message}");
        }
        finally
        {
            ClearOpenXrPacingThread();
        }
    }
}
