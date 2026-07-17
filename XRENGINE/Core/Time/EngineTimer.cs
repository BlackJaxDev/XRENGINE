using XREngine.Extensions;
using System.Diagnostics;
using System.Threading;
using System;
using XREngine.Data.Core;
using XREngine.Data.Profiling;
using XREngine.Data.Runtime.Memory;

namespace XREngine.Timers
{
    public enum ECollectVisibleLatePolicy
    {
        BlockUntilFresh = 0,
        ReusePreviousVisibility = 1,
    }

    public partial class EngineTimer : XRBase
    {
        private static readonly double SecondsPerStopwatchTick = 1.0 / Stopwatch.Frequency;

        public static long StopwatchTickFrequency => Stopwatch.Frequency;

        public static long SecondsToStopwatchTicks(double seconds)
            => seconds <= 0.0
                ? 0L
                : Math.Max(1L, (long)Math.Round(seconds * Stopwatch.Frequency));

        public static double TicksToSecondsDouble(long ticks)
            => ticks * SecondsPerStopwatchTick;

        public static float TicksToSeconds(long ticks)
            => (float)TicksToSecondsDouble(ticks);

        public static long StopwatchTicksToTimeSpanTicks(long stopwatchTicks)
            => stopwatchTicks == 0L
                ? 0L
                : (long)Math.Round(stopwatchTicks * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency));

        public static long TimeSpanTicksToStopwatchTicks(long timeSpanTicks)
            => timeSpanTicks == 0L
                ? 0L
                : (long)Math.Round(timeSpanTicks * (Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond));

        /// <summary>
        /// This is the delta used for physics and other fixed-timestep calculations.
        /// Fixed-timestep is consistent and does not vary based on rendering speed.
        /// </summary>
        private long _fixedUpdateDeltaTicks = SecondsToStopwatchTicks(0.033f);
        public long FixedUpdateDeltaTicks => _fixedUpdateDeltaTicks;
        public float FixedUpdateDelta
        {
            get => TicksToSeconds(_fixedUpdateDeltaTicks);
            set => SetField(ref _fixedUpdateDeltaTicks, SecondsToStopwatchTicks(value.ClampMin(0.0001f)));
        }
        /// <summary>
        /// This is the desired FPS for physics and other fixed-timestep calculations.
        /// It does not vary.
        /// </summary>
        public float FixedUpdateFrequency
        {
            get => (float)(Stopwatch.Frequency / (double)Math.Max(1L, _fixedUpdateDeltaTicks));
            set => FixedUpdateDelta = 1.0f / value.ClampMin(0.0001f);
        }

        private const float MaxFrequency = 1000.0f; // Frequency cap for Update/RenderFrame events
        private const int MaxFixedCatchUpSteps = 4;
        private static readonly long SleepWaitThresholdTicks = SecondsToStopwatchTicks(0.002);
        private static readonly long YieldWaitThresholdTicks = SecondsToStopwatchTicks(0.00025);

        #region Pause Support

        private bool _paused = false;
        private bool _stepOneFrame = false;

        /// <summary>
        /// When true, gameplay updates (Update, FixedUpdate) are paused.
        /// Rendering continues to allow UI interaction.
        /// </summary>
        public bool Paused
        {
            get => _paused;
            set
            {
                if (_paused == value)
                    return;
                _paused = value;
                PausedChanged?.Invoke(value);
            }
        }

        /// <summary>
        /// Fired when the pause state changes.
        /// </summary>
        public event Action<bool>? PausedChanged;

        /// <summary>
        /// Steps one frame while paused. Has no effect if not paused.
        /// </summary>
        public void StepOneFrame()
        {
            if (!_paused)
                return;
            _stepOneFrame = true;
        }

        /// <summary>
        /// Returns true if updates should be dispatched this frame.
        /// Handles pause state and single-step mode.
        /// </summary>
        private bool ShouldDispatchUpdate()
        {
            if (!_paused)
                return true;

            if (_stepOneFrame)
            {
                _stepOneFrame = false;
                return true;
            }

            return false;
        }

        #endregion

        //Events to subscribe to
        public XREvent? PreUpdateFrame;
        /// <summary>
        /// Subscribe to this event for game logic updates.
        /// </summary>
        public XREvent? UpdateFrame;
        public XREvent? PostUpdateFrame;
        /// <summary>
        /// Subscribe to this event to execute logic sequentially on the collect visible thread before parallel collection.
        /// Use this for processing pending operations that must complete before parallel viewport collection.
        /// </summary>
        public XREvent? PreCollectVisible;
        /// <summary>
        /// Subscribe to this event to execute parallel visibility collection on the collect visible thread.
        /// </summary>
        public XREvent? CollectVisible;
        /// <summary>
        /// Subscribe to this event to execute logic sequentially on the collect visible thread after parallel collection.
        /// Use this for work that consumes complete visibility data but should not sit in the SwapBuffers publish point.
        /// </summary>
        public XREvent? PostCollectVisible;
        /// <summary>
        /// Subscribe to this event to execute render commands that have been swapped for consumption.
        /// </summary>
        public XREvent? RenderFrame;
        /// <summary>
        /// Subscribe to this event to swap update and render buffers, on the render thread.
        /// </summary>
        public XREvent? SwapBuffers;
        /// <summary>
        /// Subscribe to this event to execute logic at a fixed rate completely separate from the update/render threads, such as physics.
        /// </summary>
        public XREvent? FixedUpdate;

        private long _updateTimeDiffTicks; // quantization error for UpdateFrame events
        private bool _isRunningSlowly; // true, when UpdatePeriod cannot reach TargetUpdatePeriod

        private readonly Stopwatch _watch = new();

        private ManualResetEventSlim _renderDone = new(false);
        private readonly CollectVisibleGenerationGate _visibilityGenerationGate = new();
        private int _renderReadyForNextCollectSignaled;
        private long _fixedUpdateAccumulatorTicks;
        private long _fixedUpdateClockTimestampTicks;
        private ECollectVisibleLatePolicy _collectVisibleLatePolicy = ReadCollectVisibleLatePolicyFromEnvironment();

        public ECollectVisibleLatePolicy CollectVisibleLatePolicy
        {
            get => _collectVisibleLatePolicy;
            set => SetField(ref _collectVisibleLatePolicy, value);
        }

        public ulong UpdateFrameId { get; private set; }
        public ulong CollectFrameId { get; private set; }
        public ulong SwapFrameId { get; private set; }
        public ulong PresentFrameId { get; private set; }
        public long RequestedCollectGeneration => _visibilityGenerationGate.RequestedGeneration;
        public long CompletedCollectGeneration => _visibilityGenerationGate.CompletedGeneration;
        public long PublishedCollectGeneration => _visibilityGenerationGate.PublishedGeneration;
        public long ConsumedCollectGeneration => _visibilityGenerationGate.ConsumedGeneration;
        public long RequiredCollectGeneration => _visibilityGenerationGate.RequiredGeneration;

        public bool IsRunning => _watch.IsRunning;

        public DeltaManager Render { get; } = new();
        public DeltaManager Update { get; } = new();
        public DeltaManager Collect { get; } = new();
        public DeltaManager FixedUpdateManager { get; } = new();

        private Thread? UpdateThreadHandle = null;
        private Thread? CollectVisibleThreadHandle = null;
        private Thread? FixedUpdateThreadHandle = null;

        //private static bool IsApplicationIdle() => NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0) == 0;

        //private static async Task IdleCallAsync(Action method, CancellationToken cancellationToken)
        //{
        //    while (!cancellationToken.IsCancellationRequested)
        //    {
        //        //if (IsApplicationIdle())
        //        method();
        //        await Task.Yield();
        //    }
        //}

        /// <summary>
        /// Runs the timer until Stop() is called.
        /// </summary>
        public void RunGameLoop()
        {
            if (IsRunning)
                return;

            _watch.Start();
            _renderDone = new ManualResetEventSlim(false);
            _visibilityGenerationGate.Reset();
            _renderReadyForNextCollectSignaled = 0;
            _collectVisibleLatePolicy = ReadCollectVisibleLatePolicyFromEnvironment();

            // Critical engine loops must NOT run on the shared ThreadPool; bursts of mesh/asset
            // import jobs were observed to starve them (see fps-drop diagnostics). Use dedicated
            // foreground threads at AboveNormal priority so they are scheduled independently
            // of ThreadPool saturation.
            UpdateThreadHandle = StartEngineLoopThread(UpdateThread, "XRE-Update");
            CollectVisibleThreadHandle = StartEngineLoopThread(CollectVisibleThread, "XRE-CollectVisible");
            FixedUpdateThreadHandle = StartEngineLoopThread(FixedUpdateThread, "XRE-FixedUpdate");
            // JobManager is now internally multi-threaded; no loop needed here.
            //There are 4 main threads: Update, Collect Visible, Render, and FixedUpdate.
            //Update runs as fast as requested without fences.
            //Collect Visible waits for Render to finish swapping buffers.
            //Render waits for Collect Visible to finish so it can swap buffers and then render.
            //FixedUpdate runs at a fixed framerate for physics stability.

            //SwapDone is set when the render thread finishes swapping buffers. This fence is set right before the render thread starts rendering.
            //PreRenderDone is set when the prerender thread finishes collecting render commands. This fence is set right before the render thread starts swapping buffers.

            Debug.Out($"Started game loop threads.");
        }

        /// <summary>
        /// Creates and starts a dedicated foreground thread at <see cref="ThreadPriority.AboveNormal"/>
        /// for one of the core engine loops (Update / CollectVisible / FixedUpdate).
        /// These loops must not share the ThreadPool with asset-import / job work, since import
        /// bursts can saturate the pool and starve the loop of scheduling opportunities.
        /// </summary>
        private static Thread StartEngineLoopThread(ThreadStart loop, string name)
        {
            var t = new Thread(loop)
            {
                Name = name,
                IsBackground = false,
                Priority = ThreadPriority.AboveNormal,
            };
            t.Start();
            return t;
        }

        private static ECollectVisibleLatePolicy ReadCollectVisibleLatePolicyFromEnvironment()
        {
            string? raw = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CollectVisibleLatePolicy);
            if (string.IsNullOrWhiteSpace(raw))
                return ECollectVisibleLatePolicy.BlockUntilFresh;

            string value = raw.Trim();
            if (Enum.TryParse(value, ignoreCase: true, out ECollectVisibleLatePolicy parsed) &&
                Enum.IsDefined(parsed))
                return parsed;

            return value.ToLowerInvariant() switch
            {
                "block" => ECollectVisibleLatePolicy.BlockUntilFresh,
                "fresh" => ECollectVisibleLatePolicy.BlockUntilFresh,
                "reuse" => ECollectVisibleLatePolicy.ReusePreviousVisibility,
                "stale" => ECollectVisibleLatePolicy.ReusePreviousVisibility,
                _ => ECollectVisibleLatePolicy.BlockUntilFresh,
            };
        }

        private void WaitForRemainingTicks(long remainingTicks)
        {
            if (remainingTicks <= 0L)
                return;

            if (remainingTicks >= SleepWaitThresholdTicks)
            {
                int sleepMilliseconds = (int)Math.Min(
                    15L,
                    Math.Max(1L, (remainingTicks * 1000L / Stopwatch.Frequency) - 1L));
                Thread.Sleep(sleepMilliseconds);
                return;
            }

            if (remainingTicks >= YieldWaitThresholdTicks)
            {
                Thread.Yield();
                return;
            }

            Thread.SpinWait(32);
        }

        private void WaitUntilTimestamp(long targetTimestampTicks)
        {
            while (IsRunning)
            {
                long remainingTicks = targetTimestampTicks - TimeTicks();
                if (remainingTicks <= 0L)
                    return;

                WaitForRemainingTicks(remainingTicks);
            }
        }

        private void WaitUntilNextRenderDispatch()
        {
            if (_targetRenderPeriodTicks <= 0L)
            {
                Thread.Yield();
                return;
            }

            WaitUntilTimestamp(Render.LastTimestampTicks + _targetRenderPeriodTicks);
        }

        public void BlockForRendering(Func<bool> runUntilPredicate)
        {
            Debug.Out("Blocking for rendering.");
            while (runUntilPredicate())
                WaitToRender();
            Debug.Out("No longer blocking main thread for rendering.");
        }

        /// <summary>
        /// Update is always running game logic as fast as requested.
        /// </summary>
        private void UpdateThread()
        {
            Engine.SetUpdateThreadId(Environment.CurrentManagedThreadId);
            while (IsRunning)
            {
                // Drain update-thread work even when paused.
                Engine.ProcessUpdateThreadTasks();
                //using (Engine.Profiler.Start("EngineTimer.UpdateThread.DispatchUpdate"))
                //{
                    DispatchUpdate();
                //}
            }
        }
        /// <summary>
        /// This thread waits for the render thread to finish swapping the last frame's prerender buffers, 
        /// then dispatches a prerender to collect the next frame's batch of render commands while this frame renders.
        /// </summary>
        private void CollectVisibleThread()
        {
            while (IsRunning)
            {
                try
                {
                    RunCollectVisibleIteration();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    Stop();
                    return;
                }
            }
        }

        private void RunCollectVisibleIteration()
        {
            long collectGeneration = _visibilityGenerationGate.RequestNextCollect();
#if !XRE_PUBLISHED
            long allocStart = 0;
            Engine.AllocationScope allocationScope = default;
            bool trackAlloc = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking;
            if (trackAlloc)
            {
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                allocationScope = Engine.Allocations.BeginScope("CollectSwap.Frame", AllocationScopeCategory.RenderSubmission);
            }
#endif

            try
            {
                using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread", ProfilerScopeKind.AlwaysOnHotPathLoop))
                {
                    //Collects visible object and generates render commands for the game's current state
                    using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread.DispatchCollectVisible", ProfilerScopeKind.AlwaysOnHotPathLoop))
                    {
                        if (!DispatchCollectVisible())
                        {
                            Stop();
                            return;
                        }
                    }
                    _visibilityGenerationGate.MarkCollectCompleted(collectGeneration);

                    //Wait for the render thread to swap update buffers with render buffers
                    using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread.WaitForRender", ProfilerScopeKind.AlwaysOnHotPathLoop))
                    {
                        long waitStartTicks = TimeTicks();
                        _renderDone.Wait(-1);
                        Engine.Rendering.Stats.FrameLifecycle.RecordCollectWaitForRender(TimeTicks() - waitStartTicks);
                    }

                    if (!IsRunning)
                        return;

                    _renderDone.Reset();

#if !XRE_PUBLISHED
                    using var collectSwapPublicationAllocationScope = trackAlloc
                        ? Engine.Allocations.BeginScope("Visibility.CollectSwapPublication", AllocationScopeCategory.RenderSubmission)
                        : default;
#endif

                    using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread.ProcessCollectVisibleSwapJobs", ProfilerScopeKind.AlwaysOnHotPathLoop))
                    {
                        Engine.Jobs.ProcessCollectVisibleSwapJobs();
                    }

                    using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread.DispatchSwapBuffers", ProfilerScopeKind.AlwaysOnHotPathLoop))
                    {
                        DispatchSwapBuffers();
                    }

                    // Publish only after every swap listener completed. A failed listener leaves
                    // this generation unavailable and terminates the loop through the outer catch.
                    _visibilityGenerationGate.Publish(collectGeneration);
                }
            }
            finally
            {
#if !XRE_PUBLISHED
                if (trackAlloc)
                {
                    allocationScope.Dispose();
                    long allocEnd = GC.GetAllocatedBytesForCurrentThread();
                    Engine.Allocations.RecordCollectSwap(allocEnd - allocStart);
                }
#endif
            }
        }

        // Legacy JobManager loop removed; JobManager now owns its worker threads.

        /// <summary>
        /// This thread runs at a fixed rate, executing logic that should not be tied to the update/render threads.
        /// Typical events occuring here are logic like physics calculations.
        /// </summary>
        private void FixedUpdateThread()
        {
            Engine.SetPhysicsThreadId(Environment.CurrentManagedThreadId);
            _fixedUpdateAccumulatorTicks = 0L;
            _fixedUpdateClockTimestampTicks = TimeTicks();

            while (IsRunning)
            {
                // Always drain physics-thread work, even while paused.
                Engine.ProcessPhysicsThreadTasks();

                // Keep this as a dedicated, stable thread. Using await/Task.Delay here would
                // allow the loop to resume on a different ThreadPool worker, which breaks the
                // engine's "physics thread" affinity assumptions for queued PhysX mutations.
                if (!ShouldDispatchUpdate())
                {
                    _fixedUpdateAccumulatorTicks = 0L;
                    _fixedUpdateClockTimestampTicks = TimeTicks();
                    Thread.Sleep(1);
                    continue;
                }

                long timestampTicks = TimeTicks();
                long elapsedTicks = Math.Clamp(timestampTicks - _fixedUpdateClockTimestampTicks, 0L, Stopwatch.Frequency);
                _fixedUpdateClockTimestampTicks = timestampTicks;
                _fixedUpdateAccumulatorTicks = Math.Min(
                    _fixedUpdateAccumulatorTicks + elapsedTicks,
                    _fixedUpdateDeltaTicks * MaxFixedCatchUpSteps);

                if (_fixedUpdateAccumulatorTicks < _fixedUpdateDeltaTicks)
                {
                    WaitForRemainingTicks(_fixedUpdateDeltaTicks - _fixedUpdateAccumulatorTicks);
                    continue;
                }

                int steps = 0;
                while (IsRunning && steps < MaxFixedCatchUpSteps && _fixedUpdateAccumulatorTicks >= _fixedUpdateDeltaTicks)
                {
                    long dispatchStartTicks = TimeTicks();
                    FixedUpdateManager.DeltaTicks = _fixedUpdateDeltaTicks;
                    FixedUpdateManager.LastTimestampTicks = dispatchStartTicks;

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

                    timestampTicks = TimeTicks();
                    FixedUpdateManager.ElapsedTicks = Math.Max(0L, timestampTicks - dispatchStartTicks);
                    _fixedUpdateAccumulatorTicks -= _fixedUpdateDeltaTicks;
                    steps++;
                }

                if (_fixedUpdateAccumulatorTicks >= _fixedUpdateDeltaTicks)
                    _fixedUpdateAccumulatorTicks %= _fixedUpdateDeltaTicks;
            }
        }
        /// <summary>
        /// Waits for the prerender to finish, then swaps buffers and dispatches a render.
        /// Render-thread maintenance work is intentionally not drained here so the
        /// next frame's draw work always wins over background GPU uploads.
        /// </summary>
        public void WaitToRender()
        {
            bool reusedPreviousVisibility = false;
            while (IsRunning)
            {
                if (_visibilityGenerationGate.TryConsumeFresh(out _))
                {
                    break;
                }

                if (_visibilityGenerationGate.CanReusePreviousForRequiredGeneration(CollectVisibleLatePolicy))
                {
                    long requiredGeneration = _visibilityGenerationGate.RequiredGeneration;
                    if (!DispatchRender())
                    {
                        if (!IsRunning)
                            return;

                        WaitUntilNextRenderDispatch();
                        continue;
                    }

                    if (_visibilityGenerationGate.TryRecordStaleReuse(requiredGeneration))
                    {
                        Engine.Rendering.Stats.FrameLifecycle.RecordStaleCollectReuse();
                        reusedPreviousVisibility = true;
                        break;
                    }
                }

                long waitStartTicks = TimeTicks();
                if (!_visibilityGenerationGate.WaitForPublication())
                    return;
                Engine.Rendering.Stats.FrameLifecycle.RecordRenderWaitForCollect(TimeTicks() - waitStartTicks);
            }

            if (!IsRunning)
                return;

            if (!reusedPreviousVisibility)
            {
                // Suspend this thread until a render is dispatched. Keep the loop responsive,
                // but do not steal time from the upcoming render by draining queued jobs here.
                while (IsRunning && !DispatchRender())
                    WaitUntilNextRenderDispatch();
            }

            if (!IsRunning)
                return;

            MarkRenderFrameReadyForCollect();
        }

        /// <summary>
        /// Signals that the active render dispatch has finished consuming render-side command buffers.
        /// Backends may call this before a blocking native present so CollectVisible can publish the
        /// next frame while the desktop swapchain waits on the OS/driver.
        /// </summary>
        public void MarkRenderFrameReadyForCollect()
        {
            if (Interlocked.CompareExchange(ref _renderReadyForNextCollectSignaled, 1, 0) != 0)
                return;

            _renderDone.Set();
        }

        public bool IsCollectVisibleDone
            => _visibilityGenerationGate.IsFreshGenerationAvailable;

        //public void SetRenderDone()
        //{
        //    _renderDone.Set();
        //}

        public void Stop()
        {
            _watch.Stop();

            _visibilityGenerationGate.Terminate();
            _renderDone?.Set();
            //_updatingDone?.Set();

            //UpdateThreadHandle?.Join();
            UpdateThreadHandle = null;

            //CollectVisibleThreadHandle?.Join();
            CollectVisibleThreadHandle = null;

            //FixedUpdateThreadHandle?.Join();
            FixedUpdateThreadHandle = null;
        }

        /// <summary>
        /// Retrives the current timestamp from the stopwatch.
        /// </summary>
        /// <returns></returns>
        public long TimeTicks() => _watch.ElapsedTicks;

        public double TimeDouble() => TicksToSecondsDouble(TimeTicks());

        public float Time() => (float)TimeDouble();

        public bool DispatchRender()
        {
            try
            {
                long timestampTicks = TimeTicks();
                long elapsedTicks = Math.Clamp(timestampTicks - Render.LastTimestampTicks, 0L, Stopwatch.Frequency);
                bool dispatch = elapsedTicks > 0L && elapsedTicks >= _targetRenderPeriodTicks;
                if (dispatch)
                {
                    //Debug.Out("Dispatching render.");
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

                    Engine.Rendering.State.BeginRenderFrame();
                    ulong renderFrameId = Engine.Rendering.State.RenderFrameId;
                    long renderFrameStartTicks = TimeTicks();
                    Engine.SetDispatchingRenderFrame(true);
                    try
                    {
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

                    timestampTicks = TimeTicks();
                    Render.ElapsedTicks = Math.Max(0L, timestampTicks - Render.LastTimestampTicks);

                    long renderFrameElapsedTicks = Math.Max(0L, timestampTicks - renderFrameStartTicks);
                    double renderFrameMs = renderFrameElapsedTicks * 1000.0 / Stopwatch.Frequency;
                    Engine.Rendering.Stats.FrameOutputs.RecordWholeFrameRenderThread(renderFrameId, renderFrameElapsedTicks);
                    Engine.Rendering.Stats.FrameOutputs.SnapshotAndReset();
                    XREngine.Rendering.RenderPipelineGpuProfiler.Instance.RecordRenderThreadFrameMs(renderFrameId, renderFrameMs);
                    PresentFrameId = renderFrameId;
                }
                return dispatch;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Stop();
                return false;
            }
        }

        public bool DispatchCollectVisible()
        {
            try
            {
                using var sample = Engine.Profiler.Start("EngineTimer.DispatchCollectVisible", ProfilerScopeKind.AlwaysOnHotPathLoop);
#if !XRE_PUBLISHED
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("Visibility.Collect", AllocationScopeCategory.RenderSubmission)
                    : default;
#endif

                long timestampTicks = TimeTicks();
                long elapsedTicks = Math.Clamp(timestampTicks - Collect.LastTimestampTicks, 0L, Stopwatch.Frequency);
                Collect.DeltaTicks = elapsedTicks;
                Collect.LastTimestampTicks = timestampTicks;
                unchecked
                {
                    CollectFrameId++;
                }
                PreCollectVisible?.Invoke();
                CollectVisible?.Invoke();
                PostCollectVisible?.Invoke();
                timestampTicks = TimeTicks();
                Collect.ElapsedTicks = Math.Max(0L, timestampTicks - Collect.LastTimestampTicks);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return false;
            }
        }

        public void DispatchSwapBuffers()
        {
            using var sample = Engine.Profiler.Start("EngineTimer.DispatchSwapBuffers", ProfilerScopeKind.AlwaysOnHotPathLoop);
#if !XRE_PUBLISHED
            using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                ? Engine.Allocations.BeginScope("Render.SwapBuffers", AllocationScopeCategory.RenderSubmission)
                : default;
#endif
            unchecked
            {
                SwapFrameId++;
            }
            SwapBuffers?.Invoke();
        }

        private void DispatchFixedUpdate()
        {
            using var sample = Engine.Profiler.Start("EngineTimer.DispatchFixedUpdate", ProfilerScopeKind.AlwaysOnHotPathLoop);
#if !XRE_PUBLISHED
            using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                ? Engine.Allocations.BeginScope("FixedUpdate.Frame", AllocationScopeCategory.RuntimeSystem)
                : default;
#endif
            FixedUpdate?.Invoke();
        }

        public void DispatchUpdate()
        {
            // Check if we should dispatch updates (handles pause/step)
            if (!ShouldDispatchUpdate())
            {
                Thread.Sleep(1); // Don't spin while paused
                return;
            }

            try
            {
                //using var sample = Engine.Profiler.Start("EngineTimer.DispatchUpdate");

                int runningSlowlyRetries = 4;

                long timestampTicks = TimeTicks();
                long elapsedTicks = Math.Clamp(timestampTicks - Update.LastTimestampTicks, 0L, Stopwatch.Frequency);
                long adjustedElapsedTicks = elapsedTicks + _updateTimeDiffTicks;
                if (_targetUpdatePeriodTicks > 0L && adjustedElapsedTicks < _targetUpdatePeriodTicks)
                {
                    WaitForRemainingTicks(_targetUpdatePeriodTicks - adjustedElapsedTicks);
                    return;
                }

                //Raise UpdateFrame events until we catch up with the target update period
                while (IsRunning && elapsedTicks > 0L && elapsedTicks + _updateTimeDiffTicks >= _targetUpdatePeriodTicks)
                {
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

                    timestampTicks = TimeTicks();
                    Update.ElapsedTicks = Math.Max(0L, timestampTicks - Update.LastTimestampTicks);

                    // Calculate difference (positive or negative) between
                    // actual elapsed time and target elapsed time. We must
                    // compensate for this difference.
                    _updateTimeDiffTicks += elapsedTicks - _targetUpdatePeriodTicks;

                    if (_targetUpdatePeriodTicks <= 0L)
                    {
                        // According to the TargetUpdatePeriod documentation,
                        // a TargetUpdatePeriod of zero means we will raise
                        // UpdateFrame events as fast as possible (one event
                        // per ProcessEvents() call)
                        break;
                    }

                    _isRunningSlowly = _updateTimeDiffTicks >= _targetUpdatePeriodTicks;
                    if (_isRunningSlowly && --runningSlowlyRetries == 0)
                    {
                        // If UpdateFrame consistently takes longer than TargetUpdateFrame
                        // stop raising events to avoid hanging inside the UpdateFrame loop.
                        break;
                    }

                    // Prepare for next loop
                    elapsedTicks = Math.Clamp(timestampTicks - Update.LastTimestampTicks, 0L, Stopwatch.Frequency);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private long _targetRenderPeriodTicks;
        /// <summary>
        /// Gets or sets a float representing the target render frequency, in hertz.
        /// </summary>
        /// <remarks>
        /// <para>A value of 0.0 indicates that RenderFrame events are generated at the maximum possible frequency (i.e. only limited by the hardware's capabilities).</para>
        /// <para>Values lower than 1.0Hz are clamped to 0.0. Values higher than 500.0Hz are clamped to 200.0Hz.</para>
        /// </remarks>
        public float TargetRenderFrequency
        {
            get => _targetRenderPeriodTicks == 0L ? 0.0f : (float)(Stopwatch.Frequency / (double)_targetRenderPeriodTicks);
            set
            {
                float current = TargetRenderFrequency;
                if (Math.Abs(current - value) < 0.0001f)
                    return;

                if (value < 1.0f)
                {
                    SetField(ref _targetRenderPeriodTicks, 0L);
                    Debug.Out("Target render frequency set to unrestricted.");
                }
                else if (value < MaxFrequency)
                {
                    SetField(ref _targetRenderPeriodTicks, SecondsToStopwatchTicks(1.0 / value));
                    Debug.Out("Target render frequency set to {0}Hz.", value.ToString());
                }
                else
                {
                    SetField(ref _targetRenderPeriodTicks, SecondsToStopwatchTicks(1.0 / MaxFrequency));
                    Debug.Out("Target render frequency clamped to {0}Hz.", MaxFrequency.ToString());
                }
            }
        }

        /// <summary>
        /// Gets or sets a float representing the target render period, in seconds.
        /// </summary>
        /// <remarks>
        /// <para>A value of 0.0 indicates that RenderFrame events are generated at the maximum possible frequency (i.e. only limited by the hardware's capabilities).</para>
        /// <para>Values lower than 0.002 seconds (500Hz) are clamped to 0.0. Values higher than 1.0 seconds (1Hz) are clamped to 1.0.</para>
        /// </remarks>
        public float TargetRenderPeriod
        {
            get => TicksToSeconds(_targetRenderPeriodTicks);
            set
            {
                float current = TargetRenderPeriod;
                if (Math.Abs(current - value) < 0.0001f)
                    return;

                if (value < 1.0f / MaxFrequency)
                {
                    SetField(ref _targetRenderPeriodTicks, 0L);
                    Debug.Out("Target render frequency set to unrestricted.");
                }
                else if (value < 1.0f)
                {
                    SetField(ref _targetRenderPeriodTicks, SecondsToStopwatchTicks(value));
                    Debug.Out("Target render frequency set to {0}Hz.", TargetRenderFrequency.ToString());
                }
                else
                {
                    SetField(ref _targetRenderPeriodTicks, SecondsToStopwatchTicks(1.0));
                    Debug.Out("Target render frequency clamped to 1Hz.");
                }
            }
        }

        private long _targetUpdatePeriodTicks;
        /// <summary>
        /// Gets or sets a float representing the target update frequency, in hertz.
        /// </summary>
        /// <remarks>
        /// <para>A value of 0.0 indicates that UpdateFrame events are generated at the maximum possible frequency (i.e. only limited by the hardware's capabilities).</para>
        /// <para>Values lower than 1.0Hz are clamped to 0.0. Values higher than 500.0Hz are clamped to 500.0Hz.</para>
        /// </remarks>
        public float TargetUpdateFrequency
        {
            get => _targetUpdatePeriodTicks == 0L ? 0.0f : (float)(Stopwatch.Frequency / (double)_targetUpdatePeriodTicks);
            set
            {
                float current = TargetUpdateFrequency;
                if (Math.Abs(current - value) < 0.0001f)
                    return;

                if (value < 1.0)
                {
                    SetField(ref _targetUpdatePeriodTicks, 0L);
                    Debug.Out("Target update frequency set to unrestricted.");
                }
                else if (value < MaxFrequency)
                {
                    SetField(ref _targetUpdatePeriodTicks, SecondsToStopwatchTicks(1.0 / value));
                    Debug.Out("Target update frequency set to {0}Hz.", value);
                }
                else
                {
                    SetField(ref _targetUpdatePeriodTicks, SecondsToStopwatchTicks(1.0 / MaxFrequency));
                    Debug.Out("Target update frequency clamped to {0}Hz.", MaxFrequency);
                }
            }
        }

        /// <summary>
        /// Gets or sets a float representing the target update period, in seconds.
        /// </summary>
        /// <remarks>
        /// <para>A value of 0.0 indicates that UpdateFrame events are generated at the maximum possible frequency (i.e. only limited by the hardware's capabilities).</para>
        /// <para>Values lower than 0.002 seconds (500Hz) are clamped to 0.0. Values higher than 1.0 seconds (1Hz) are clamped to 1.0.</para>
        /// </remarks>
        public float TargetUpdatePeriod
        {
            get => TicksToSeconds(_targetUpdatePeriodTicks);
            set
            {
                float current = TargetUpdatePeriod;
                if (Math.Abs(current - value) < 0.0001f)
                    return;

                if (value < 1.0f / MaxFrequency)
                {
                    SetField(ref _targetUpdatePeriodTicks, 0L);
                    Debug.Out("Target update frequency set to unrestricted.");
                }
                else if (value < 1.0)
                {
                    SetField(ref _targetUpdatePeriodTicks, SecondsToStopwatchTicks(value));
                    Debug.Out("Target update frequency set to {0}Hz.", TargetUpdateFrequency);
                }
                else
                {
                    SetField(ref _targetUpdatePeriodTicks, SecondsToStopwatchTicks(1.0));
                    Debug.Out("Target update frequency clamped to 1Hz.");
                }
            }
        }

        private EVSyncMode _vSync;
        public EVSyncMode VSync
        {
            get => _vSync;
            set => SetField(ref _vSync, value);
        }
    }
}
