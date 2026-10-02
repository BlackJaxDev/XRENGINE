using System;
using System.Threading;
using XREngine.Execution;

namespace XREngine.Timers;

public partial class EngineTimer
{
    private int _callerThreadLoopOwnerThreadId;

    /// <summary>Whether a host owns frame cadence and executes all phases on its calling thread.</summary>
    public bool IsCallerThreadLoop => Volatile.Read(ref _callerThreadLoopOwnerThreadId) != 0;

    /// <summary>
    /// Starts a host-driven clock without creating loop workers. The host must install the
    /// caller-thread job executor and claim the render thread before starting this lifecycle.
    /// </summary>
    public void StartCallerThreadLoop()
    {
        if (IsRunning || Volatile.Read(ref _explicitFrameOwnerThreadId) != 0 ||
            UpdateThreadHandle is { IsAlive: true } ||
            CollectVisibleThreadHandle is { IsAlive: true } ||
            FixedUpdateThreadHandle is { IsAlive: true })
        {
            throw new InvalidOperationException("The previous engine timer lifecycle must stop before starting a caller-thread loop.");
        }

        if (Engine.Jobs.ExecutionMode != JobExecutionMode.CallerThread)
            throw new InvalidOperationException("A caller-thread loop requires an explicitly configured caller-thread job executor.");
        if (!RuntimeEngine.IsRenderThread)
            throw new InvalidOperationException("The caller-thread loop must start on the registered render thread.");

        int threadId = Environment.CurrentManagedThreadId;
        Engine.SetUpdateThreadId(threadId);
        Engine.SetPhysicsThreadId(threadId);
        _explicitFrameTimestampTicks = TimeTicks();
        _usesExplicitFrameClock = true;
        _visibilityGenerationGate.Reset();
        _renderDone.Reset();
        _renderReadyForNextCollectSignaled = 0;
        Interlocked.Exchange(ref _firstTerminalFault, null);
        Volatile.Write(ref _collectVisiblePhase, "Idle");
        Volatile.Write(ref _callerThreadLoopOwnerThreadId, threadId);
        ResetFrameTiming();
    }

    /// <summary>
    /// Executes one fixed/update/collect/swap/render sequence without waiting. Elapsed time is
    /// supplied by the host, capped at one second, and fixed simulation catches up by at most
    /// four ticks. Host cadence replaces desktop update/render frequency waits.
    /// </summary>
    /// <returns>True when the frame rendered; false when a callback stopped the lifecycle.</returns>
    public bool StepFrame(double elapsedSeconds) => StepFrame(elapsedSeconds, dispatchSimulation: true);

    /// <summary>Executes a caller-thread presentation frame with an independently gated simulation.</summary>
    /// <param name="dispatchSimulation">False suppresses fixed and variable updates without
    /// changing application pause state. Jobs, visibility, swapping and rendering continue;
    /// simulation debt is discarded instead of accumulating during the gate.</param>
    /// <returns>True when the frame rendered; false when a callback stopped the lifecycle.</returns>
    public bool StepFrame(double elapsedSeconds, bool dispatchSimulation)
    {
        RequireCallerThreadLoopOwner();
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Frame elapsed time must be finite and non-negative.");
        if (!RuntimeEngine.IsRenderThread)
            throw new InvalidOperationException("The caller-thread loop lost render-thread ownership.");

        long elapsedTicks = SecondsToStopwatchTicks(Math.Min(elapsedSeconds, 1.0));
        BeginExplicitFrameClock(elapsedTicks, callerThreadLoop: true);
        string phase = "Jobs";
        try
        {
            Engine.Jobs.ProcessCallerThreadJobs(JobAffinity.Any);
            Engine.Jobs.ProcessCallerThreadJobs(JobAffinity.Remote);
            Engine.ProcessUpdateThreadTasks();
            Engine.ProcessPhysicsThreadTasks();
            if (!IsCallerThreadLoop)
                return false;

            // Consume a paused single-step request once for both simulation phases.
            if (dispatchSimulation && ShouldDispatchUpdate())
            {
                phase = "FixedUpdate";
                _fixedUpdateAccumulatorTicks = Math.Min(
                    _fixedUpdateAccumulatorTicks + elapsedTicks,
                    _fixedUpdateDeltaTicks * MaxFixedCatchUpSteps);
                DispatchAccumulatedFixedUpdates();
                if (!IsCallerThreadLoop)
                    return false;

                phase = "Update";
                DispatchVariableUpdate(TimeTicks(), elapsedTicks);
            }
            else
            {
                _fixedUpdateAccumulatorTicks = 0L;
                Update.LastTimestampTicks = TimeTicks();
                Update.DeltaTicks = 0L;
                FixedUpdateManager.DeltaTicks = 0L;
            }

            if (!IsCallerThreadLoop)
                return false;

            phase = "CollectVisible";
            if (!TryCollectVisibleGeneration(out long generation))
            {
                if (FirstTerminalFault is not null)
                    throw new InvalidOperationException(FirstTerminalFault.ExceptionMessage);
                return false;
            }

            phase = "SwapBuffers";
            if (!TryPublishCollectVisibleGeneration(generation))
                return false;
            if (!_visibilityGenerationGate.TryConsumeFresh(out long consumed) || consumed != generation)
                throw new InvalidOperationException("The caller-thread frame could not consume its completed visibility publication.");

            phase = "Render";
            DispatchRenderFrame(TimeTicks(), elapsedTicks, processMainThreadTasks: true);
            return IsCallerThreadLoop;
        }
        catch (Exception exception)
        {
            CaptureTerminalFault(exception, "CallerThread", phase);
            Stop();
            throw;
        }
        finally
        {
            Volatile.Write(ref _explicitFrameOwnerThreadId, 0);
        }
    }

    /// <summary>
    /// Discards simulation debt after suspension. The host must also reset its elapsed-time
    /// source before the next step so time spent suspended is not reintroduced.
    /// </summary>
    public void ResetFrameTiming()
    {
        RequireCallerThreadLoopOwner();
        if (Volatile.Read(ref _explicitFrameOwnerThreadId) != 0)
            throw new InvalidOperationException("Frame timing can only reset between caller-thread frames.");

        _fixedUpdateAccumulatorTicks = 0L;
        _fixedUpdateClockTimestampTicks = TimeTicks();
        _updateTimeDiffTicks = 0L;
        ResetDeltaClock(Update);
        ResetDeltaClock(Collect);
        ResetDeltaClock(Render);
        ResetDeltaClock(FixedUpdateManager);
    }

    private void ResetDeltaClock(DeltaManager clock)
    {
        clock.LastTimestampTicks = TimeTicks();
        clock.DeltaTicks = 0L;
        clock.ElapsedTicks = 0L;
    }

    private void RequireCallerThreadLoopOwner()
    {
        if (Volatile.Read(ref _callerThreadLoopOwnerThreadId) != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Only the active caller-thread loop owner can step or reset the engine timer.");
    }

    private void RejectCallerThreadWait()
    {
        if (IsCallerThreadLoop || RuntimeWorkScheduler.IsCallerThread || OperatingSystem.IsBrowser())
            throw new InvalidOperationException("A caller-thread host must use StepFrame instead of a blocking timer dispatch.");
    }

    private static void ProcessCollectVisibleSwapJobs()
    {
        Engine.SetFrameSwapThread(true);
        try
        {
            Engine.Jobs.ProcessCollectVisibleSwapJobs();
        }
        finally
        {
            Engine.SetFrameSwapThread(false);
        }
    }
}
