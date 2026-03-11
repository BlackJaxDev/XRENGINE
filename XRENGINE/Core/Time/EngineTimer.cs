using Extensions;
using System.Diagnostics;
using System.Threading;
using System;
using XREngine.Data.Core;

namespace XREngine.Timers
{
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
        /// Subscribe to this event to execute logic on the render thread right before buffers are swapped.
        /// </summary>
        public XREvent? CollectVisible;
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

        private ManualResetEventSlim
            _renderDone = new(false),
            _swapDone = new(true);//,
            //_updateDone = new(false);

        public bool IsRunning => _watch.IsRunning;

        public DeltaManager Render { get; } = new();
        public DeltaManager Update { get; } = new();
        public DeltaManager Collect { get; } = new();
        public DeltaManager FixedUpdateManager { get; } = new();

        private CancellationTokenSource? _cancelRenderTokenSource = null;

        private Task? UpdateTask = null;
        private Task? CollectVisibleTask = null;
        private Task? RenderTask = null;
        private Task? SingleTask = null;
        private Task? FixedUpdateTask = null;
        // JobManager now runs its own worker threads; this task is no longer used.
        private Task? JobManagerTask = null;

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
            _swapDone = new ManualResetEventSlim(true);

            UpdateTask = Task.Run(UpdateThread);
            CollectVisibleTask = Task.Run(CollectVisibleThread);
            FixedUpdateTask = Task.Run(FixedUpdateThread);
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
#if !XRE_PUBLISHED
                long allocStart = 0;
                if (Engine.EditorPreferences.Debug.EnableThreadAllocationTracking)
                    allocStart = GC.GetAllocatedBytesForCurrentThread();
#endif

                using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread"))
                {
                    //Collects visible object and generates render commands for the game's current state
                    using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread.DispatchCollectVisible"))
                    {
                        DispatchCollectVisible();
                    }

                    //Wait for the render thread to swap update buffers with render buffers
                    using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread.WaitForRender"))
                    {
                        _renderDone.Wait(-1);
                    }

                    _renderDone.Reset();

                    using (Engine.Profiler.Start("EngineTimer.CollectVisibleThread.DispatchSwapBuffers"))
                    {
                        Engine.Jobs.ProcessCollectVisibleSwapJobs();
                        DispatchSwapBuffers();
                    }
                }

#if !XRE_PUBLISHED
                if (allocStart != 0)
                {
                    long allocEnd = GC.GetAllocatedBytesForCurrentThread();
                    Engine.Allocations.RecordCollectSwap(allocEnd - allocStart);
                }
#endif

                //Inform the render thread that the swap is done
                _swapDone.Set();
            }
        }

        // Legacy JobManager loop removed; JobManager now owns its worker threads.

        /// <summary>
        /// This thread runs at a fixed rate, executing logic that should not be tied to the update/render threads.
        /// Typical events occuring here are logic like physics calculations.
        /// </summary>
        private async Task FixedUpdateThread()
        {
            Engine.SetPhysicsThreadId(Environment.CurrentManagedThreadId);
            while (IsRunning)
            {
                // Always drain physics-thread work, even while paused.
                Engine.ProcessPhysicsThreadTasks();

                // Skip fixed updates when paused (unless stepping)
                if (!ShouldDispatchUpdate())
                {
                    await Task.Delay(1);
                    continue;
                }

                long timestampTicks = TimeTicks();
                long elapsedTicks = Math.Clamp(timestampTicks - FixedUpdateManager.LastTimestampTicks, 0L, Stopwatch.Frequency);
                if (elapsedTicks < _fixedUpdateDeltaTicks)
                    continue;
                
                FixedUpdateManager.DeltaTicks = elapsedTicks;
                FixedUpdateManager.LastTimestampTicks = timestampTicks;

#if !XRE_PUBLISHED
                long allocStart = 0;
                if (Engine.EditorPreferences.Debug.EnableThreadAllocationTracking)
                    allocStart = GC.GetAllocatedBytesForCurrentThread();
#endif

                DispatchFixedUpdate();

#if !XRE_PUBLISHED
                if (allocStart != 0)
                {
                    long allocEnd = GC.GetAllocatedBytesForCurrentThread();
                    Engine.Allocations.RecordFixedUpdateTick(allocEnd - allocStart);
                }
#endif

                timestampTicks = TimeTicks();
                FixedUpdateManager.ElapsedTicks = Math.Max(0L, timestampTicks - FixedUpdateManager.LastTimestampTicks);
            }
        }
        /// <summary>
        /// Waits for the prerender to finish, then swaps buffers and dispatches a render.
        /// </summary>
        public void WaitToRender()
        {
            // Wait for the collect-visible thread to finish swapping buffers.
            while (!_swapDone.Wait(0))
            {
                //Engine.ProcessMainThreadTasks();
                //Thread.Yield();
            }
            _swapDone.Reset();

            // Suspend this thread until a render is dispatched, draining queued work between polls.
            while (!DispatchRender())
            {
                //Engine.ProcessMainThreadTasks();
                //Thread.Yield();
            }

            // Inform the update thread that the render is done
            _renderDone.Set();
        }

        public bool IsCollectVisibleDone
            => _swapDone.IsSet;

        //public void ResetCollectVisible()
        //{
        //    _swapDone.Reset();
        //}

        //public void SetRenderDone()
        //{
        //    _renderDone.Set();
        //}

        public void Stop()
        {
            _watch.Stop();

            _swapDone?.Set();
            _renderDone?.Set();
            //_updatingDone?.Set();

            //UpdateTask?.Wait(-1);
            UpdateTask = null;

            //CollectVisibleTask?.Wait(-1);
            CollectVisibleTask = null;

            //RenderTask?.Wait(-1);
            RenderTask = null;

            //SingleTask?.Wait(-1);
            SingleTask = null;

            //FixedUpdateTask?.Wait(-1);
            FixedUpdateTask = null;
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
                    using var sample = Engine.Profiler.Start("EngineTimer.DispatchRender");

#if !XRE_PUBLISHED
                    long allocStart = 0;
                    if (Engine.EditorPreferences.Debug.EnableThreadAllocationTracking)
                        allocStart = GC.GetAllocatedBytesForCurrentThread();
#endif

                    Render.DeltaTicks = elapsedTicks;
                    Render.LastTimestampTicks = timestampTicks;

                    Engine.Rendering.State.BeginRenderFrame();
                    RenderFrame?.Invoke(); // This dispatch has to be synchronous to stay on the main thread

#if !XRE_PUBLISHED
                    if (allocStart != 0)
                    {
                        long allocEnd = GC.GetAllocatedBytesForCurrentThread();
                        Engine.Allocations.RecordRender(allocEnd - allocStart);
                    }
#endif

                    timestampTicks = TimeTicks();
                    Render.ElapsedTicks = Math.Max(0L, timestampTicks - Render.LastTimestampTicks);
                }
                return dispatch;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return false;
            }
        }

        public void DispatchCollectVisible()
        {
            try
            {
                using var sample = Engine.Profiler.Start("EngineTimer.DispatchCollectVisible");

                long timestampTicks = TimeTicks();
                long elapsedTicks = Math.Clamp(timestampTicks - Collect.LastTimestampTicks, 0L, Stopwatch.Frequency);
                Collect.DeltaTicks = elapsedTicks;
                Collect.LastTimestampTicks = timestampTicks;
                PreCollectVisible?.Invoke();
                //CollectVisible?.InvokeParallel();
                (CollectVisible?.InvokeAsync() ?? Task.CompletedTask).Wait();
                timestampTicks = TimeTicks();
                Collect.ElapsedTicks = Math.Max(0L, timestampTicks - Collect.LastTimestampTicks);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        public void DispatchSwapBuffers()
        {
            using var sample = Engine.Profiler.Start("EngineTimer.DispatchSwapBuffers");
            SwapBuffers?.Invoke();
        }

        private void DispatchFixedUpdate()
        {
            using var sample = Engine.Profiler.Start("EngineTimer.DispatchFixedUpdate");
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

                //Raise UpdateFrame events until we catch up with the target update period
                while (IsRunning && elapsedTicks > 0L && elapsedTicks + _updateTimeDiffTicks >= _targetUpdatePeriodTicks)
                {
                    using var updateIterationSample = Engine.Profiler.Start("EngineTimer.DispatchUpdate.Iteration");

#if !XRE_PUBLISHED
                    long allocStart = 0;
                    if (Engine.EditorPreferences.Debug.EnableThreadAllocationTracking)
                        allocStart = GC.GetAllocatedBytesForCurrentThread();
#endif

                    Update.DeltaTicks = elapsedTicks;
                    Update.LastTimestampTicks = timestampTicks;

                    using (Engine.Profiler.Start("EngineTimer.DispatchUpdate.PreUpdate"))
                    {
                        PreUpdateFrame?.Invoke();
                    }

                    using (Engine.Profiler.Start("EngineTimer.DispatchUpdate.Update"))
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

                    using (Engine.Profiler.Start("EngineTimer.DispatchUpdate.PostUpdate"))
                    {
                        PostUpdateFrame?.Invoke();
                    }

#if !XRE_PUBLISHED
                    if (allocStart != 0)
                    {
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
