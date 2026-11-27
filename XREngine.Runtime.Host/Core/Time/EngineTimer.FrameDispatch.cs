using System;
using System.Diagnostics;
using System.Threading;
using XREngine.Data.Profiling;
using XREngine.Data.Runtime.Memory;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Timers;

public partial class EngineTimer
{
    private bool TryCollectVisibleGeneration(out long generation)
    {
        if (!IsRunning || FirstTerminalFault is not null)
        {
            generation = 0L;
            return false;
        }

        generation = _visibilityGenerationGate.RequestNextCollect();
        if (!DispatchCollectVisible() || !IsRunning || FirstTerminalFault is not null)
            return false;

        _visibilityGenerationGate.MarkCollectCompleted(generation);
        return true;
    }

    /// <summary>
    /// Runs the swap jobs and the buffer swap, then publishes <paramref name="generation"/>.
    /// With XR collect pacing, the publication first waits (bounded) for <c>xrWaitFrame</c> of the
    /// next XR frame, so that the generation carries that frame and its eye commands.
    /// </summary>
    /// <param name="generation">The collected generation to publish.</param>
    /// <param name="collectStartTimestamp">Stopwatch timestamp of the collect start, for the publication age.</param>
    /// <param name="xrPacing">The XR collect pacing gate, or null outside the threaded loop and outside OpenXR.</param>
    private bool TryPublishCollectVisibleGeneration(
        long generation,
        long collectStartTimestamp = 0L,
        IOpenXrCollectPacing? xrPacing = null)
    {
        if (!IsRunning || FirstTerminalFault is not null)
            return false;

        Volatile.Write(ref _collectVisiblePhase, "ProcessCollectVisibleSwapJobs");
        long swapJobsStartTicks = XREngine.Rendering.CollectVisibleStageTimings.StartIn(XREngine.Rendering.ECollectVisibleSection.Swap);
        using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread.ProcessCollectVisibleSwapJobs", ProfilerScopeKind.AlwaysOnHotPathLoop))
            ProcessCollectVisibleSwapJobs();
        XREngine.Rendering.CollectVisibleStageTimings.End(XREngine.Rendering.ECollectVisibleStage.SwapJobs, swapJobsStartTicks);
        if (!IsRunning || FirstTerminalFault is not null)
            return false;

        Volatile.Write(ref _collectVisiblePhase, "DispatchSwapBuffers");
        using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread.DispatchSwapBuffers", ProfilerScopeKind.AlwaysOnHotPathLoop))
            DispatchSwapBuffers();
        if (!IsRunning || FirstTerminalFault is not null)
            return false;

        long xrFrameWaitStart = 0L;
        long xrFrameWaitEnd = 0L;
        if (xrPacing is not null)
        {
            Volatile.Write(ref _collectVisiblePhase, "CompleteXrFramePublication");
            long start = Stopwatch.GetTimestamp();
            EOpenXrCollectPacingWait frameWait = xrPacing.CompletePacedFramePublication();
            if (frameWait is not (EOpenXrCollectPacingWait.NotPaced or EOpenXrCollectPacingWait.Ready))
            {
                xrFrameWaitStart = start;
                xrFrameWaitEnd = Stopwatch.GetTimestamp();
            }
            if (!IsRunning || FirstTerminalFault is not null)
                return false;
        }

        Volatile.Write(ref _publishedXrFrameWaitStartTimestamp, xrFrameWaitStart);
        Volatile.Write(ref _publishedXrFrameWaitEndTimestamp, xrFrameWaitEnd);
        Volatile.Write(ref _publishedCollectStartTimestamp, collectStartTimestamp != 0L ? collectStartTimestamp : Stopwatch.GetTimestamp());
        if (!IsCallerThreadLoop && !_usesExplicitFrameClock)
            Volatile.Write(ref _renderFrameConsumingSnapshot, 1);
        Volatile.Write(ref _collectVisiblePhase, "GatePublish");
        _visibilityGenerationGate.Publish(generation);
        return true;
    }

    private void DispatchVariableUpdate(long timestampTicks, long elapsedTicks)
    {
        long dispatchStartTicks = Stopwatch.GetTimestamp();
        using var updateIterationSample = Engine.Profiler.Start("EngineTimer.DispatchUpdate.Iteration", ProfilerScopeKind.AlwaysOnHotPathLoop);

#if !XRE_PUBLISHED
        long allocStart = 0;
        Engine.AllocationScope allocationScope = default;
        bool trackAlloc = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking;
        if (trackAlloc)
        {
            allocStart = GC.GetAllocatedBytesForCurrentThread();
            allocationScope = Engine.Allocations.BeginScope("Update.Frame", AllocationScopeCategory.RuntimeSystem);
        }
#endif

        Update.DeltaTicks = elapsedTicks;
        Update.LastTimestampTicks = timestampTicks;
        unchecked
        {
            UpdateFrameId++;
        }

        using (Engine.Profiler.Start("EngineTimer.DispatchUpdate.PreUpdate", ProfilerScopeKind.AlwaysOnHotPathLoop))
        {
            PreUpdateFrame?.Invoke();
        }

        using (Engine.Profiler.Start("EngineTimer.DispatchUpdate.Update", ProfilerScopeKind.AlwaysOnHotPathLoop))
        {
#if !XRE_PUBLISHED
            Engine.Profiler.BeginComponentTimingFrame(Time());
            try
            {
                UpdateFrame?.Invoke();
            }
            finally
            {
                Engine.Profiler.EndComponentTimingFrame(Time());
            }
#else
            UpdateFrame?.Invoke();
#endif
        }

        using (Engine.Profiler.Start("EngineTimer.DispatchUpdate.PostUpdate", ProfilerScopeKind.AlwaysOnHotPathLoop))
        {
            PostUpdateFrame?.Invoke();
        }

#if !XRE_PUBLISHED
        if (trackAlloc)
        {
            allocationScope.Dispose();
            long allocEnd = GC.GetAllocatedBytesForCurrentThread();
            Engine.Allocations.RecordUpdateTick(allocEnd - allocStart);
        }
#endif

        Update.ElapsedTicks = Math.Max(0L, Stopwatch.GetTimestamp() - dispatchStartTicks);
    }

    private void DispatchRenderFrame(long timestampTicks, long elapsedTicks, bool processMainThreadTasks)
    {
        using var sample = Engine.Profiler.Start("EngineTimer.DispatchRender", ProfilerScopeKind.AlwaysOnHotPathLoop);

#if !XRE_PUBLISHED
        long allocStart = 0;
        Engine.AllocationScope allocationScope = default;
        bool trackAlloc = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking;
        if (trackAlloc)
        {
            allocStart = GC.GetAllocatedBytesForCurrentThread();
            allocationScope = Engine.Allocations.BeginScope("Render.Frame", AllocationScopeCategory.RenderSubmission);
        }
#endif

        Render.DeltaTicks = elapsedTicks;
        Render.LastTimestampTicks = timestampTicks;
        Volatile.Write(ref _renderReadyForNextCollectSignaled, 0);

        ulong renderFrameId = RuntimeEngine.Rendering.BeginRenderFrame();
        long renderFrameStartTicks = Stopwatch.GetTimestamp();
        Engine.SetDispatchingRenderFrame(true);
        try
        {
            if (processMainThreadTasks)
                Engine.ProcessMainThreadTasks();
            RenderFrame?.Invoke(); // This dispatch has to be synchronous to stay on the render thread
        }
        finally
        {
            Engine.SetDispatchingRenderFrame(false);
        }

#if !XRE_PUBLISHED
        if (trackAlloc)
        {
            allocationScope.Dispose();
            long allocEnd = GC.GetAllocatedBytesForCurrentThread();
            Engine.Allocations.RecordRender(allocEnd - allocStart);
        }
#endif

        long renderFrameElapsedTicks = Math.Max(0L, Stopwatch.GetTimestamp() - renderFrameStartTicks);
        Render.ElapsedTicks = renderFrameElapsedTicks;
        double renderFrameMs = renderFrameElapsedTicks * 1000.0 / Stopwatch.Frequency;
        RuntimeEngine.Rendering.CompleteRenderFrame(renderFrameId, renderFrameElapsedTicks);
        XREngine.Rendering.RenderPipelineGpuProfiler.Instance.RecordRenderThreadFrameMs(renderFrameId, renderFrameMs);
        PresentFrameId = renderFrameId;
    }

    /// <summary>Adds elapsed ticks and caps fixed-update debt at the catch-up limit.</summary>
    private void AccumulateFixedUpdateTicks(long elapsedTicks) =>
        _fixedUpdateAccumulatorTicks = Math.Min(
            _fixedUpdateAccumulatorTicks + elapsedTicks,
            _fixedUpdateDeltaTicks * MaxFixedCatchUpSteps);

    private void DispatchAccumulatedFixedUpdates()
    {
        int steps = 0;
        while (IsRunning && steps < MaxFixedCatchUpSteps && _fixedUpdateAccumulatorTicks >= _fixedUpdateDeltaTicks)
        {
            long dispatchStartTicks = Stopwatch.GetTimestamp();
            FixedUpdateManager.DeltaTicks = _fixedUpdateDeltaTicks;
            FixedUpdateManager.LastTimestampTicks = TimeTicks();

#if !XRE_PUBLISHED
            long allocStart = 0;
            bool trackAlloc = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking;
            if (trackAlloc)
                allocStart = GC.GetAllocatedBytesForCurrentThread();
#endif

            DispatchFixedUpdate();

#if !XRE_PUBLISHED
            if (trackAlloc)
            {
                long allocEnd = GC.GetAllocatedBytesForCurrentThread();
                Engine.Allocations.RecordFixedUpdateTick(allocEnd - allocStart);
            }
#endif

            FixedUpdateManager.ElapsedTicks = Math.Max(0L, Stopwatch.GetTimestamp() - dispatchStartTicks);
            _fixedUpdateAccumulatorTicks -= _fixedUpdateDeltaTicks;
            steps++;
        }

        if (_fixedUpdateAccumulatorTicks >= _fixedUpdateDeltaTicks)
            _fixedUpdateAccumulatorTicks %= _fixedUpdateDeltaTicks;
    }
}
