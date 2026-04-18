using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using XREngine.Data.Core;

namespace XREngine
{
    /// <summary>
    /// Threading properties and task scheduling functionality for the engine.
    /// </summary>
    public static partial class Engine
    {
        private static readonly ConcurrentDictionary<string, byte> _renderDispatchWarningLabels = new(StringComparer.Ordinal);
        private const int MaxRenderThreadJobsPerDispatch = 128;

        /// <summary>
        /// Time budget in milliseconds for render-thread job dispatch per frame.
        /// After each job completes, if cumulative time exceeds this budget, remaining
        /// jobs are deferred to the next frame to keep the window responsive.
        /// </summary>
        private const double RenderThreadJobBudgetMs = 4.0;

        #region Public Properties - Threading

        /// <summary>
        /// Gets whether the current thread is the main render thread.
        /// </summary>
        /// <remarks>
        /// Use this to guard operations that must only occur on the render thread,
        /// such as OpenGL/Vulkan API calls.
        /// </remarks>
        public static bool IsRenderThread
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Environment.CurrentManagedThreadId == RenderThreadId;
        }

        /// <summary>
        /// Gets the managed thread ID of the render thread.
        /// </summary>
        public static int RenderThreadId { get; private set; }

        /// <summary>
        /// Gets whether the current thread is the app/update thread.
        /// </summary>
        public static bool IsAppThread
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Environment.CurrentManagedThreadId == UpdateThreadId;
        }

        /// <summary>
        /// Gets the managed thread ID of the app/update thread.
        /// </summary>
        public static int UpdateThreadId { get; private set; }

        /// <summary>
        /// Gets whether the current thread is the physics thread.
        /// </summary>
        /// <remarks>
        /// Use this to guard operations that must only occur on the physics thread,
        /// such as PhysX scene mutations.
        /// </remarks>
        public static bool IsPhysicsThread
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Environment.CurrentManagedThreadId == PhysicsThreadId;
        }

        /// <summary>
        /// Gets the managed thread ID of the physics thread.
        /// </summary>
        public static int PhysicsThreadId { get; private set; }

        /// <summary>
        /// Sets the physics thread ID. Called internally by the physics system.
        /// </summary>
        internal static void SetPhysicsThreadId(int threadId)
            => PhysicsThreadId = threadId;

        /// <summary>
        /// Sets the render thread ID. Called during initialization.
        /// </summary>
        internal static void SetRenderThreadId(int threadId)
            => RenderThreadId = threadId;

        /// <summary>
        /// Sets the app/update thread ID. Called internally by the engine timer.
        /// </summary>
        internal static void SetUpdateThreadId(int threadId)
            => UpdateThreadId = threadId;

        internal static bool IsDispatchingRenderFrame => Volatile.Read(ref _isDispatchingRenderFrame) != 0;

        internal static void SetDispatchingRenderFrame(bool value)
            => Interlocked.Exchange(ref _isDispatchingRenderFrame, value ? 1 : 0);

        internal static void ObserveJobDispatch(JobAffinity affinity, string profilerLabel)
        {
#if DEBUG
            if (!IsDispatchingRenderFrame || affinity != JobAffinity.RenderThread)
                return;

            string label = string.IsNullOrWhiteSpace(profilerLabel)
                ? "<unnamed>"
                : profilerLabel.Trim();

            if (IsGpuTaggedRenderThreadJob(label))
                return;

            if (_renderDispatchWarningLabels.TryAdd(label, 0))
            {
                Debug.LogWarning(
                    $"[RenderThreadJobs] RenderFrame dispatched non-GPU-tagged render-thread job '{label}'. " +
                    "Move scene/editor/networking work to AppThread or UpdateThread unless it truly requires the graphics context.");
            }
#endif
        }

        private static bool IsGpuTaggedRenderThreadJob(string label)
        {
            if (label.Equals("Invoke:UISvgComponent.Rasterize", StringComparison.Ordinal))
                return true;

            // Progressive mipmap upload coroutine — GPU texture work.
            if (label.Contains("StartProgressiveCoroutine", StringComparison.Ordinal))
                return true;

            ReadOnlySpan<string> gpuTokens =
            [
                "OpenGL",
                "GL",
                "Vulkan",
                "Vk",
                "Gpu",
                "Shader",
                "RenderProgram",
                "Renderer",
                "Texture",
                "Buffer",
                "Framebuffer",
                "Fbo",
                "Fence",
                "MeshRenderer",
                "Readback",
                "Screenshot",
                "Upload",
                "Mip",
                "Viewport",
                "Imposter",
            ];

            foreach (string token in gpuTokens)
            {
                if (label.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        #endregion

        #region Task Scheduling

        /// <summary>
        /// Schedules a task to execute during the collect-visible/render swap point.
        /// </summary>
        /// <param name="task">The action to execute.</param>
        /// <remarks>
        /// Swap tasks run at a specific synchronization point between the update and render phases,
        /// making them suitable for operations that need consistent state across both systems.
        /// </remarks>
        public static void EnqueueSwapTask(Action task)
            => Jobs.Schedule(new ActionJob(task), JobPriority.Normal, JobAffinity.CollectVisibleSwap);

        /// <summary>
        /// Adds a coroutine that runs during the collect-visible/render swap point.
        /// </summary>
        /// <param name="task">A function that returns <c>true</c> when complete, <c>false</c> to continue.</param>
        /// <remarks>
        /// The task will be called repeatedly at each swap point until it returns <c>true</c>.
        /// </remarks>
        public static void AddSwapCoroutine(Func<bool> task)
            => Jobs.Schedule(new CoroutineJob(task), JobPriority.Normal, JobAffinity.CollectVisibleSwap);

        /// <summary>
        /// Schedules a task to execute on the main (render) thread.
        /// </summary>
        /// <param name="task">The action to execute.</param>
        /// <remarks>
        /// Use this for operations that require the graphics context, such as
        /// creating or modifying GPU resources.
        /// </remarks>
        public static void EnqueueRenderThreadTask(Action task)
            => Jobs.Schedule(new ActionJob(task), JobPriority.Normal, JobAffinity.RenderThread);

        /// <summary>
        /// Schedules a labeled task to execute on the main (render) thread.
        /// </summary>
        /// <param name="task">The action to execute.</param>
        /// <param name="reason">A description for profiler labeling.</param>
        public static void EnqueueRenderThreadTask(Action task, string reason)
            => Jobs.Schedule(new LabeledActionJob(task, reason), JobPriority.Normal, JobAffinity.RenderThread);

        /// <summary>
        /// Schedules a task to execute on the app/update thread.
        /// </summary>
        public static void EnqueueAppThreadTask(Action task)
            => Jobs.Schedule(new ActionJob(task), JobPriority.Normal, JobAffinity.AppThread);

        /// <summary>
        /// Schedules a labeled task to execute on the app/update thread.
        /// </summary>
        public static void EnqueueAppThreadTask(Action task, string reason)
            => Jobs.Schedule(new LabeledActionJob(task, reason), JobPriority.Normal, JobAffinity.AppThread);

        /// <summary>
        /// Schedules a task to execute on the main (render) thread.
        /// </summary>
        public static void EnqueueMainThreadTask(Action task)
            => EnqueueRenderThreadTask(task);

        /// <summary>
        /// Schedules a labeled task to execute on the main (render) thread.
        /// </summary>
        public static void EnqueueMainThreadTask(Action task, string reason)
            => EnqueueRenderThreadTask(task, reason);

        /// <summary>
        /// Schedules a task to run on the engine update thread.
        /// </summary>
        /// <param name="task">The action to execute.</param>
        /// <remarks>
        /// Use this for work that must not run on the render thread,
        /// such as play-mode transitions or game logic updates.
        /// </remarks>
        public static void EnqueueUpdateThreadTask(Action task)
        {
            if (task is null)
                return;

            _pendingUpdateThreadWork.Enqueue(task);
        }

        /// <summary>
        /// Schedules a task to run on the physics thread.
        /// </summary>
        /// <param name="task">The action to execute.</param>
        /// <remarks>
        /// Use this for PhysX scene mutations (add/remove/release bodies)
        /// to avoid cross-thread access violations.
        /// </remarks>
        public static void EnqueuePhysicsThreadTask(Action task)
        {
            if (task is null)
                return;
            _pendingPhysicsThreadWork.Enqueue(task);
        }

        /// <summary>
        /// Adds a coroutine that runs on the main (render) thread.
        /// </summary>
        /// <param name="task">A function that returns <c>true</c> when complete, <c>false</c> to continue.</param>
        public static void AddRenderThreadCoroutine(Func<bool> task)
            => Jobs.Schedule(new CoroutineJob(task), JobPriority.Normal, JobAffinity.RenderThread);

        /// <summary>
        /// Adds a labeled coroutine that runs on the main (render) thread.
        /// </summary>
        public static void AddRenderThreadCoroutine(Func<bool> task, string reason)
            => Jobs.Schedule(new LabeledCoroutineJob(task, reason), JobPriority.Normal, JobAffinity.RenderThread);

        /// <summary>
        /// Adds a coroutine that runs on the app/update thread.
        /// </summary>
        public static void AddAppThreadCoroutine(Func<bool> task)
            => Jobs.Schedule(new CoroutineJob(task), JobPriority.Normal, JobAffinity.AppThread);

        /// <summary>
        /// Adds a coroutine that runs on the main (render) thread.
        /// </summary>
        public static void AddMainThreadCoroutine(Func<bool> task)
            => AddRenderThreadCoroutine(task);

        /// <summary>
        /// Adds a labeled coroutine that runs on the main (render) thread.
        /// </summary>
        public static void AddMainThreadCoroutine(Func<bool> task, string reason)
            => AddRenderThreadCoroutine(task, reason);

        /// <summary>
        /// Invokes a task on the main thread if not already on it.
        /// </summary>
        /// <param name="task">The action to execute.</param>
        /// <param name="reason">A description for logging and profiling.</param>
        /// <param name="executeNowIfAlreadyMainThread">
        /// If <c>true</c> and already on the main thread, executes immediately.
        /// If <c>false</c>, the caller is expected to execute the task.
        /// </param>
        /// <returns><c>true</c> if the task was enqueued; <c>false</c> if already on the main thread.</returns>
        public static bool InvokeOnRenderThread(Action task, string reason, bool executeNowIfAlreadyRenderThread = false)
        {
            if (IsRenderThread)
            {
                if (executeNowIfAlreadyRenderThread)
                    task();

                return false;
            }

            var queuedEntry = LogMainThreadInvoke(reason, MainThreadInvokeMode.Queued);
            long queuedAtTimestamp = Stopwatch.GetTimestamp();
            Debug.Out($"[MainThreadInvoke] {reason} (queued)");
            EnqueueRenderThreadTask(() => ExecuteLoggedMainThreadInvoke(task, queuedEntry, queuedAtTimestamp), reason);
            return true;
        }

        /// <summary>
        /// Invokes a task on the app/update thread if not already there.
        /// </summary>
        public static bool InvokeOnAppThread(Action task, string reason, bool executeNowIfAlreadyAppThread = false)
        {
            if (IsAppThread)
            {
                if (executeNowIfAlreadyAppThread)
                    task();
                return false;
            }

            EnqueueAppThreadTask(task, reason);
            return true;
        }

        /// <summary>
        /// Invokes a task on the main/render thread if not already there.
        /// </summary>
        public static bool InvokeOnMainThread(Action task, string reason, bool executeNowIfAlreadyMainThread = false)
            => InvokeOnRenderThread(task, reason, executeNowIfAlreadyMainThread);

        #endregion

        #region Internal Task Processing

        /// <summary>
        /// Processes pending main thread tasks. Called from the timer's render frame event.
        /// </summary>
        private static void DequeueMainThreadTasks()
            => ProcessPendingMainThreadWork();

        /// <summary>
        /// Public entry point for processing main thread tasks.
        /// </summary>
        internal static void ProcessMainThreadTasks()
            => ProcessPendingMainThreadWork();

        /// <summary>
        /// Processes tasks queued for the update thread.
        /// </summary>
        /// <param name="maxTasks">Maximum number of tasks to process per call.</param>
        internal static void ProcessUpdateThreadTasks(int maxTasks = 1024)
        {
            int processed = 0;
            while (processed < maxTasks && _pendingUpdateThreadWork.TryDequeue(out var task))
            {
                try
                {
                    task();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
                processed++;
            }

            if (processed < maxTasks)
                ProcessPendingAppThreadWork(maxTasks - processed);
        }

        /// <summary>
        /// Processes tasks queued for the physics thread.
        /// </summary>
        /// <param name="maxTasks">Maximum number of tasks to process per call.</param>
        internal static void ProcessPhysicsThreadTasks(int maxTasks = 4096)
        {
            int processed = 0;
            while (processed < maxTasks && _pendingPhysicsThreadWork.TryDequeue(out var task))
            {
                try
                {
                    task();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
                processed++;
            }
        }

        private static void ProcessPendingMainThreadWork()
        {
            using var scope = Engine.Profiler.Start("MainThreadJobs.Dispatch");
            Jobs.ProcessMainThreadJobs(MaxRenderThreadJobsPerDispatch, RenderThreadJobBudgetMs);
        }

        private static void ExecuteLoggedMainThreadInvoke(Action task, MainThreadInvokeEntry entry, long? queuedAtTimestamp)
        {
            long executionStartTimestamp = Stopwatch.GetTimestamp();
            bool completed = false;
            Exception? exception = null;

            try
            {
                task();
                completed = true;
            }
            catch (Exception ex)
            {
                exception = ex;
                throw;
            }
            finally
            {
                long executionEndTimestamp = Stopwatch.GetTimestamp();
                double queueDelayMs = queuedAtTimestamp.HasValue
                    ? StopwatchTicksToMilliseconds(executionStartTimestamp - queuedAtTimestamp.Value)
                    : 0.0;
                double executionMs = StopwatchTicksToMilliseconds(executionEndTimestamp - executionStartTimestamp);
                LogMainThreadInvokeExecution(entry, queueDelayMs, executionMs, completed, exception);
            }
        }

        private static double StopwatchTicksToMilliseconds(long ticks)
            => ticks <= 0L ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;

        private static void ProcessPendingAppThreadWork(int maxJobs)
        {
            // App-thread work must not run while the render frame is being dispatched.
            if (IsDispatchingRenderFrame)
                return;

            using var scope = Engine.Profiler.Start("AppThreadJobs.Dispatch");
            Jobs.ProcessAppThreadJobs(maxJobs);
        }

        private static void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("Engine.SwapBuffers");
        }

        /// <summary>
        /// Called after the timer's update frame to process deferred operations.
        /// </summary>
        private static void Timer_PostUpdateFrame()
        {
            using var scope = Profiler.Start("Engine.Timer_PostUpdateFrame");

            XRObjectBase.ProcessPendingDestructions();
            Scene.Transforms.TransformBase.ProcessParentReassignments();
        }

        #endregion
    }
}
