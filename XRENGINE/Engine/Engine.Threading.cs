using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using XREngine.Data.Core;

namespace XREngine
{
    /// <summary>
    /// Threading properties and task scheduling functionality for the engine.
    /// </summary>
    public static partial class Engine
    {
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
        public static void EnqueueMainThreadTask(Action task)
            => Jobs.Schedule(new ActionJob(task), JobPriority.Normal, JobAffinity.MainThread);

        /// <summary>
        /// Schedules a labeled task to execute on the main (render) thread.
        /// </summary>
        /// <param name="task">The action to execute.</param>
        /// <param name="reason">A description for profiler labeling.</param>
        public static void EnqueueMainThreadTask(Action task, string reason)
            => Jobs.Schedule(new LabeledActionJob(task, reason), JobPriority.Normal, JobAffinity.MainThread);

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
        public static void AddMainThreadCoroutine(Func<bool> task)
            => Jobs.Schedule(new CoroutineJob(task), JobPriority.Normal, JobAffinity.MainThread);

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
        public static bool InvokeOnMainThread(Action task, string reason, bool executeNowIfAlreadyMainThread = false)
        {
            if (IsRenderThread)
            {
                if (executeNowIfAlreadyMainThread)
                {
                    task();
                }
                return false;
            }

            LogMainThreadInvoke(reason, MainThreadInvokeMode.Queued);
            Debug.Out($"[MainThreadInvoke] {reason} (queued)");
            EnqueueMainThreadTask(task, reason);
            return true;
        }

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
            Jobs.ProcessMainThreadJobs();
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
