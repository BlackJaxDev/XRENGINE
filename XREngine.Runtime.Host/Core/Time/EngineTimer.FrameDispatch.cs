using System;
using System.Diagnostics;
using System.Threading;
using XREngine.Data.Profiling;
using XREngine.Data.Runtime.Memory;

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

    private bool TryPublishCollectVisibleGeneration(long generation)
    {
        if (!IsRunning || FirstTerminalFault is not null)
            return false;

        Volatile.Write(ref _collectVisiblePhase, "ProcessCollectVisibleSwapJobs");
        using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread.ProcessCollectVisibleSwapJobs", ProfilerScopeKind.AlwaysOnHotPathLoop))
            ProcessCollectVisibleSwapJobs();
        if (!IsRunning || FirstTerminalFault is not null)
            return false;

        Volatile.Write(ref _collectVisiblePhase, "DispatchSwapBuffers");
        using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread.DispatchSwapBuffers", ProfilerScopeKind.AlwaysOnHotPathLoop))
            DispatchSwapBuffers();
        if (!IsRunning || FirstTerminalFault is not null)
            return false;

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
