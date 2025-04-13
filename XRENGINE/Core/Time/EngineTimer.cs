using Extensions;
using System.Diagnostics;
using XREngine.Data.Core;

namespace XREngine.Timers
{
    public partial class EngineTimer : XRBase
    {
        /// <summary>
        /// This is the delta used for physics and other fixed-timestep calculations.
        /// Fixed-timestep is consistent and does not vary based on rendering speed.
        /// </summary>
        public float FixedUpdateDelta { get; set; } = 0.033f;
        /// <summary>
        /// This is the desired FPS for physics and other fixed-timestep calculations.
        /// It does not vary.
        /// </summary>
        public float FixedUpdateFrequency
        {
            get => 1.0f / FixedUpdateDelta.ClampMin(0.0001f);
            set => FixedUpdateDelta = 1.0f / value.ClampMin(0.0001f);
        }

        private const float MaxFrequency = 1000.0f; // Frequency cap for Update/RenderFrame events

        //Events to subscribe to
        public XREvent? PreUpdateFrame;
        /// <summary>
        /// Subscribe to this event for game logic updates.
        /// </summary>
        public XREvent? UpdateFrame;
        public XREvent? PostUpdateFrame;
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

        private float _updateTimeDiff = 0.0f; // quantization error for UpdateFrame events
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
        private async Task UpdateThread()
        {
            while (IsRunning)
                await DispatchUpdate();
        }
        /// <summary>
        /// This thread waits for the render thread to finish swapping the last frame's prerender buffers, 
        /// then dispatches a prerender to collect the next frame's batch of render commands while this frame renders.
        /// </summary>
        private async Task CollectVisibleThread()
        {
            while (IsRunning)
            {
                //Collects visible object and generates render commands for the game's current state
                await DispatchCollectVisible();

                //Wait for the render thread to swap update buffers with render buffers
                _renderDone.Wait(-1);
                _renderDone.Reset();
                await DispatchSwapBuffers();

                //Inform the render thread that the swap is done
                _swapDone.Set();
            }
        }
        /// <summary>
        /// This thread runs at a fixed rate, executing logic that should not be tied to the update/render threads.
        /// Typical events occuring here are logic like physics calculations.
        /// </summary>
        private async Task FixedUpdateThread()
        {
            while (IsRunning)
            {
                float timestamp = Time();
                float elapsed = (timestamp - FixedUpdateManager.LastTimestamp).Clamp(0.0f, 1.0f);
                if (elapsed < FixedUpdateDelta)
                    continue;
                
                FixedUpdateManager.Delta = elapsed;
                FixedUpdateManager.LastTimestamp = timestamp;
                await DispatchFixedUpdate();
                timestamp = Time();
                FixedUpdateManager.ElapsedTime = timestamp - FixedUpdateManager.LastTimestamp;
            }
        }
        /// <summary>
        /// Waits for the prerender to finish, then swaps buffers and dispatches a render.
        /// </summary>
        public void WaitToRender()
        {
            //Wait for swapping to finish
            _swapDone.Wait(-1);
            _swapDone.Reset();

            //Suspend this thread until a render is dispatched
            while (!DispatchRender()) ;

            //Inform the update thread that the render is done
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
        public float Time() => (float)_watch.ElapsedTicks / TimeSpan.TicksPerSecond;

        public bool DispatchRender()
        {
            try
            {
                float timestamp = Time();
                float elapsed = (timestamp - Render.LastTimestamp).Clamp(0.0f, 1.0f);
                bool dispatch = elapsed > 0.0f && elapsed >= TargetRenderPeriod;
                if (dispatch)
                {
                    //Debug.Out("Dispatching render.");

                    Render.Delta = elapsed;
                    Render.LastTimestamp = timestamp;
                    RenderFrame?.Invoke(); // This dispatch has to be synchronous to stay on the main thread

                    timestamp = Time();
                    Render.ElapsedTime = timestamp - Render.LastTimestamp;
                }
                return dispatch;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return false;
            }
        }

        public async Task DispatchCollectVisible()
        {
            try
            {
                float timestamp = Time();
                float elapsed = (timestamp - Collect.LastTimestamp).Clamp(0.0f, 1.0f);
                Collect.Delta = elapsed;
                Collect.LastTimestamp = timestamp;
                await (CollectVisible?.InvokeAsync() ?? Task.CompletedTask);
                timestamp = Time();
                Collect.ElapsedTime = timestamp - Collect.LastTimestamp;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        public Task DispatchSwapBuffers()
            => (SwapBuffers?.InvokeAsync() ?? Task.CompletedTask);

        private Task DispatchFixedUpdate()
            => (FixedUpdate?.InvokeAsync() ?? Task.CompletedTask);

        public async Task DispatchUpdate()
        {
            try
            {
                int runningSlowlyRetries = 4;

                float timestamp = Time();
                float elapsed = (timestamp - Update.LastTimestamp).Clamp(0.0f, 1.0f);

                //Raise UpdateFrame events until we catch up with the target update period
                while (IsRunning && elapsed > 0.0f && elapsed + _updateTimeDiff >= TargetUpdatePeriod)
                {
                    Update.Delta = elapsed;
                    Update.LastTimestamp = timestamp;

                    await (PreUpdateFrame?.InvokeAsync() ?? Task.CompletedTask);
                    await (UpdateFrame?.InvokeAsync() ?? Task.CompletedTask);
                    await (PostUpdateFrame?.InvokeAsync() ?? Task.CompletedTask);

                    timestamp = Time();
                    Update.ElapsedTime = timestamp - Update.LastTimestamp;

                    // Calculate difference (positive or negative) between
                    // actual elapsed time and target elapsed time. We must
                    // compensate for this difference.
                    _updateTimeDiff += elapsed - TargetUpdatePeriod;

                    if (TargetUpdatePeriod <= double.Epsilon)
                    {
                        // According to the TargetUpdatePeriod documentation,
                        // a TargetUpdatePeriod of zero means we will raise
                        // UpdateFrame events as fast as possible (one event
                        // per ProcessEvents() call)
                        break;
                    }

                    _isRunningSlowly = _updateTimeDiff >= TargetUpdatePeriod;
                    if (_isRunningSlowly && --runningSlowlyRetries == 0)
                    {
                        // If UpdateFrame consistently takes longer than TargetUpdateFrame
                        // stop raising events to avoid hanging inside the UpdateFrame loop.
                        break;
                    }

                    // Prepare for next loop
                    elapsed = (timestamp - Update.LastTimestamp).Clamp(0.0f, 1.0f);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private float _targetRenderPeriod;
        /// <summary>
        /// Gets or sets a float representing the target render frequency, in hertz.
        /// </summary>
        /// <remarks>
        /// <para>A value of 0.0 indicates that RenderFrame events are generated at the maximum possible frequency (i.e. only limited by the hardware's capabilities).</para>
        /// <para>Values lower than 1.0Hz are clamped to 0.0. Values higher than 500.0Hz are clamped to 200.0Hz.</para>
        /// </remarks>
        public float TargetRenderFrequency
        {
            get => _targetRenderPeriod == 0.0f ? 0.0f : 1.0f / _targetRenderPeriod;
            set
            {
                if (value < 1.0f)
                {
                    SetField(ref _targetRenderPeriod, 0.0f);
                    Debug.Out("Target render frequency set to unrestricted.");
                }
                else if (value < MaxFrequency)
                {
                    SetField(ref _targetRenderPeriod, 1.0f / value);
                    Debug.Out("Target render frequency set to {0}Hz.", value.ToString());
                }
                else
                {
                    SetField(ref _targetRenderPeriod, 1.0f / MaxFrequency);
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
            get => _targetRenderPeriod;
            set
            {
                if (value < 1.0f / MaxFrequency)
                {
                    SetField(ref _targetRenderPeriod, 0.0f);
                    Debug.Out("Target render frequency set to unrestricted.");
                }
                else if (value < 1.0f)
                {
                    SetField(ref _targetRenderPeriod, value);
                    Debug.Out("Target render frequency set to {0}Hz.", TargetRenderFrequency.ToString());
                }
                else
                {
                    SetField(ref _targetRenderPeriod, 1.0f);
                    Debug.Out("Target render frequency clamped to 1Hz.");
                }
            }
        }

        private float _targetUpdatePeriod;
        /// <summary>
        /// Gets or sets a float representing the target update frequency, in hertz.
        /// </summary>
        /// <remarks>
        /// <para>A value of 0.0 indicates that UpdateFrame events are generated at the maximum possible frequency (i.e. only limited by the hardware's capabilities).</para>
        /// <para>Values lower than 1.0Hz are clamped to 0.0. Values higher than 500.0Hz are clamped to 500.0Hz.</para>
        /// </remarks>
        public float TargetUpdateFrequency
        {
            get => _targetUpdatePeriod == 0.0f ? 0.0f : 1.0f / _targetUpdatePeriod;
            set
            {
                if (value < 1.0)
                {
                    SetField(ref _targetUpdatePeriod, 0.0f);
                    Debug.Out("Target update frequency set to unrestricted.");
                }
                else if (value < MaxFrequency)
                {
                    SetField(ref _targetUpdatePeriod, 1.0f / value);
                    Debug.Out("Target update frequency set to {0}Hz.", value);
                }
                else
                {
                    SetField(ref _targetUpdatePeriod, 1.0f / MaxFrequency);
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
            get => _targetUpdatePeriod;
            set
            {
                if (value < 1.0f / MaxFrequency)
                {
                    SetField(ref _targetUpdatePeriod, 0.0f);
                    Debug.Out("Target update frequency set to unrestricted.");
                }
                else if (value < 1.0)
                {
                    SetField(ref _targetUpdatePeriod, value);
                    Debug.Out("Target update frequency set to {0}Hz.", TargetUpdateFrequency);
                }
                else
                {
                    SetField(ref _targetUpdatePeriod, 1.0f);
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
