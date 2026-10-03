using Silk.NET.OpenGL;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using XREngine.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        /// <summary>
        /// A lightweight shared OpenGL context running on a dedicated background thread.
        /// Created with a shared desktop GL context so that GL program objects
        /// (and other shared resources) are accessible from both the main render context
        /// and this background thread.
        /// </summary>
        public sealed class GLSharedContext : IDisposable
        {
            private readonly string _threadName;
            private readonly double _workerUnhealthySeconds;
            private static readonly object GlfwSharedContextStartupLock = new();
            // Per-priority FIFO buckets (lower priority value drained first). Indexed by (byte)EProgramPriority.
            // Buckets cover the full enum range so callers can route work without bounds checks.
            private const int PriorityBucketCount = (int)EProgramPriority.Deferred + 1;
            private readonly ConcurrentQueue<SharedContextJob>[] _jobs;
            private long _pendingCount;
            private readonly AutoResetEvent _signal = new(false);
            private CancellationTokenSource? _cts;
            private Thread? _thread;
            private IRuntimeWindowBackend? _window;
            private XRWindow? _ownerWindow;
            private int _disposeRequested;
            private int _disposeResourcesOnWorkerExit;
            private int _resourcesReleased;
            private int _contextDetachFailed;
            private volatile bool _running;
            private long _currentJobStartTimestamp;
            private string? _currentJobName;
            private long _oldestQueuedTimestamp;
            private long _completedCount;
            private long _failedCount;
            private volatile bool _workerUnhealthy;

            /// <summary>
            /// Default unhealthy threshold for jobs running on the shared context thread.
            /// Long enough to cover most asset/upload work but short enough to detect a
            /// genuinely wedged worker. Caller may override via the constructor when
            /// the work shape is known to be slow (e.g. cold shader compile/link of
            /// large uber shaders, which can legitimately take more than a minute on
            /// first run with a fresh driver cache).
            /// </summary>
            private const double DefaultWorkerUnhealthySeconds = 30.0;

            public GLSharedContext(string threadName = "XR GL Shared Context")
                : this(threadName, DefaultWorkerUnhealthySeconds)
            {
            }

            public GLSharedContext(string threadName, double workerUnhealthySeconds)
            {
                _threadName = threadName;
                _workerUnhealthySeconds = workerUnhealthySeconds > 0.0
                    ? workerUnhealthySeconds
                    : DefaultWorkerUnhealthySeconds;
                _jobs = new ConcurrentQueue<SharedContextJob>[PriorityBucketCount];
                for (int i = 0; i < PriorityBucketCount; i++)
                    _jobs[i] = new ConcurrentQueue<SharedContextJob>();
            }

            public bool IsRunning => _running && !IsWorkerUnhealthy;
            public bool IsThreadAlive => _thread is { IsAlive: true };
            public bool IsDisposeRequested => Volatile.Read(ref _disposeRequested) != 0;
            public bool IsWorkerUnhealthy => _workerUnhealthy || CurrentJobElapsedSeconds >= _workerUnhealthySeconds;
            public double WorkerUnhealthySeconds => _workerUnhealthySeconds;
            public int PendingCount => (int)Interlocked.Read(ref _pendingCount);
            public long CompletedCount => Interlocked.Read(ref _completedCount);
            public long FailedCount => Interlocked.Read(ref _failedCount);
            public string? CurrentJobName => _currentJobName;
            public double OldestPendingAgeSeconds
            {
                get
                {
                    long timestamp = Interlocked.Read(ref _oldestQueuedTimestamp);
                    return timestamp == 0 ? 0.0 : StopwatchTicksToSeconds(Stopwatch.GetTimestamp() - timestamp);
                }
            }

            public double CurrentJobElapsedSeconds
            {
                get
                {
                    long timestamp = Interlocked.Read(ref _currentJobStartTimestamp);
                    return timestamp == 0 ? 0.0 : StopwatchTicksToSeconds(Stopwatch.GetTimestamp() - timestamp);
                }
            }

            internal static void RunWithStartupLock(Action action)
            {
                lock (GlfwSharedContextStartupLock)
                    action();
            }

            /// <summary>
            /// Creates the shared context and starts the background thread.
            /// Must be called from the main render thread while the primary GL context is current.
            /// </summary>
            public bool Initialize(XRWindow primaryWindow)
            {
                if (_running)
                    return true;

                if (Environment.CurrentManagedThreadId != primaryWindow.NativeWindowThreadId)
                {
                    Debug.RenderingWarning("[SharedContext] Creation requires the primary desktop window owner thread.");
                    return false;
                }

                var primaryGLContext = primaryWindow.DesktopGlContext;
                if (primaryGLContext is null)
                {
                    Debug.RenderingWarning("[SharedContext] Primary window has no GL context.");
                    return false;
                }

                IRuntimeWindowBackend? window = null;
                try
                {
                    var startup = WindowStartupValues.Default with
                    {
                        Title = _threadName,
                        Width = 1,
                        Height = 1,
                    };
                    var request = new RuntimeWindowCreateOptions(
                        startup,
                        RuntimeGraphicsApiKind.OpenGL,
                        EInteractiveWindowResizeStrategy.Default,
                        RuntimeWindowPurpose.SecondaryGpuContext,
                        new XREngine.Data.Vectors.IVector2(0, 0),
                        new XREngine.Data.Vectors.IVector2(1, 1),
                        false, false, false, false, false,
                        32, 24, 8, 4, 6, false, false, false,
                        primaryGLContext);

                    lock (GlfwSharedContextStartupLock)
                    {
                        Debug.OpenGL($"[SharedContext] Creating hidden GLFW shared context '{_threadName}'.");
                        window = RuntimeWindowBackendRegistry.RequireFactory().Create(in request);
                        window.Initialize(new SharedWindowEventSink());
                        _window = window;
                        _ownerWindow = primaryWindow;

                        // Only the detached GL context crosses to the worker; native window APIs stay here.
                        window.GlContext?.ClearCurrent();
                        primaryGLContext.MakeCurrent();
                        Debug.OpenGL($"[SharedContext] Hidden GLFW shared context '{_threadName}' initialized.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.RenderingWarning($"[SharedContext] Failed to create shared GL context '{_threadName}': {ex.Message}");
                    (window ?? _window)?.Dispose();
                    _window = null;
                    _ownerWindow = null;
                    return false;
                }

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                var sharedWindow = _window;

                _thread = new Thread(() => Run(sharedWindow, token))
                {
                    IsBackground = true,
                    Name = _threadName,
                };
                Debug.OpenGL($"[SharedContext] Starting worker '{_threadName}'.");
                _thread.Start();

                SpinWait.SpinUntil(() => _running || token.IsCancellationRequested, TimeSpan.FromSeconds(3));
                if (!_running)
                {
                    Debug.RenderingWarning("[SharedContext] Background thread failed to start.");
                    Dispose();
                    return false;
                }

                return true;
            }

            /// <summary>
            /// Initializes a pre-created backend whose native window is owned by the caller's window thread.
            /// With an <paramref name="ownerWindow"/>, disposal destroys the native window on that window's
            /// thread; without one, the caller keeps ownership and destroys the native window itself.
            /// </summary>
            public bool Initialize(IRuntimeWindowBackend preCreatedSharedWindow, XRWindow? ownerWindow = null)
            {
                if (_running)
                    return true;

                _window = preCreatedSharedWindow;
                _ownerWindow = ownerWindow;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                _thread = new Thread(() => Run(preCreatedSharedWindow, token))
                {
                    IsBackground = true,
                    Name = _threadName,
                };
                _thread.Start();

                SpinWait.SpinUntil(() => _running || token.IsCancellationRequested, TimeSpan.FromSeconds(3));
                if (!_running)
                {
                    Debug.RenderingWarning("[SharedContext] Background thread failed to start.");
                    Dispose();
                    return false;
                }

                return true;
            }

            /// <summary>
            /// Queues a GL job to execute on the shared context thread.
            /// The action receives the shared context's GL API instance.
            /// </summary>
            public void Enqueue(Action<GL> job)
                => Enqueue(job, null, EProgramPriority.Main);

            public void Enqueue(Action<GL> job, string? name)
                => Enqueue(job, name, EProgramPriority.Main);

            /// <summary>
            /// Queues a GL job tagged with a priority bucket. Lower-valued priorities are
            /// drained before higher-valued ones; jobs within the same bucket are FIFO.
            /// </summary>
            public void Enqueue(Action<GL> job, string? name, EProgramPriority priority)
            {
                if (Volatile.Read(ref _disposeRequested) != 0)
                    return;

                int bucket = (int)priority;
                if ((uint)bucket >= (uint)PriorityBucketCount)
                    bucket = PriorityBucketCount - 1;

                long now = Stopwatch.GetTimestamp();
                // If the queue is currently empty across all buckets, this job is the oldest one.
                if (Interlocked.Read(ref _pendingCount) == 0)
                    Interlocked.Exchange(ref _oldestQueuedTimestamp, now);

                _jobs[bucket].Enqueue(new SharedContextJob(job, name, now));
                Interlocked.Increment(ref _pendingCount);
                try
                {
                    _signal.Set();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            public void Dispose()
                => Dispose(TimeSpan.FromSeconds(2));

            /// <summary>
            /// Requests worker shutdown without waiting for active GL work to finish.
            /// Used from renderer/window shutdown so an in-flight driver shader link
            /// cannot turn application close into a multi-second join chain.
            /// </summary>
            public void DisposeForShutdown()
                => Dispose(TimeSpan.Zero);

            private void Dispose(TimeSpan joinTimeout)
            {
                if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
                    return;

                Interlocked.Exchange(ref _disposeResourcesOnWorkerExit, 1);

                try
                {
                    _cts?.Cancel();
                    _signal.Set();
                }
                catch (ObjectDisposedException)
                {
                }

                Thread? thread = _thread;
                bool joined = thread is null || !thread.IsAlive;
                if (!joined && thread is not null && joinTimeout > TimeSpan.Zero && thread != Thread.CurrentThread)
                    joined = thread.Join(joinTimeout);

                if (!joined && thread is not null && !thread.IsAlive)
                    joined = true;

                if (joined)
                {
                    ReleaseStoppedResources();
                    return;
                }

                _running = false;
                string? currentJobName = CurrentJobName;
                int pendingCount = PendingCount;
                double currentJobElapsedSeconds = CurrentJobElapsedSeconds;
                if (currentJobName is not null || pendingCount > 0 || currentJobElapsedSeconds > 0.0)
                {
                    Debug.RenderingWarning(
                        "[SharedContext] Abandoning active worker '{0}' during shutdown; currentJob={1} elapsedSeconds={2:F1} pendingJobs={3}.",
                        _threadName,
                        currentJobName ?? "<none>",
                        currentJobElapsedSeconds,
                        pendingCount);
                }
            }

            private void Run(IRuntimeWindowBackend window, CancellationToken token)
            {
                try
                {
                    GL gl;
                    lock (GlfwSharedContextStartupLock)
                    {
                        Debug.OpenGL($"[SharedContext] Worker '{_threadName}' making hidden shared context current.");
                        IRuntimeWindowGlContext context = window.GlContext
                            ?? throw new InvalidOperationException("The shared desktop window has no GL context.");
                        context.MakeCurrent();
                        gl = GL.GetApi(context.GetProcAddress);
                        _running = true;
                        Debug.OpenGL($"[SharedContext] Worker '{_threadName}' is running.");
                    }

                    using (gl)
                    {
                        while (!token.IsCancellationRequested)
                        {
                            if (!TryDequeueHighestPriority(out var job))
                            {
                                Interlocked.Exchange(ref _oldestQueuedTimestamp, 0);
                                _signal.WaitOne(TimeSpan.FromMilliseconds(5));
                                continue;
                            }

                            try
                            {
                                _currentJobName = job.Name;
                                Interlocked.Exchange(ref _currentJobStartTimestamp, Stopwatch.GetTimestamp());
                                job.Action(gl);
                                Interlocked.Increment(ref _completedCount);
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref _failedCount);
                                Debug.RenderingWarning($"[SharedContext] Job failed: {ex.Message}");
                            }
                            finally
                            {
                                _currentJobName = null;
                                Interlocked.Exchange(ref _currentJobStartTimestamp, 0);

                                long oldestTs = PeekOldestPendingTimestamp();
                                Interlocked.Exchange(ref _oldestQueuedTimestamp, oldestTs);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _workerUnhealthy = true;
                    Debug.RenderingWarning($"[SharedContext] Thread terminated: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    _running = false;
                    try
                    {
                        window.GlContext?.ClearCurrent();
                    }
                    catch (Exception ex)
                    {
                        Debug.RenderingWarning($"[SharedContext] Worker context detach failed: {ex.Message}");
                        Volatile.Write(ref _contextDetachFailed, 1);
                    }
                    if (Volatile.Read(ref _disposeResourcesOnWorkerExit) != 0)
                        ReleaseStoppedResources(window);
                }
            }

            private void ReleaseStoppedResources(IRuntimeWindowBackend? workerWindow = null)
            {
                if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0)
                    return;

                try
                {
                    _cts?.Dispose();
                }
                catch
                {
                }

                IRuntimeWindowBackend? nativeWindow = workerWindow ?? _window;
                XRWindow? owner = _ownerWindow;
                if (nativeWindow is not null && owner is not null)
                    RuntimeRenderingHostServices.Scheduling.EnqueueWindowThreadTask(
                        owner,
                        Volatile.Read(ref _contextDetachFailed) != 0
                            ? nativeWindow.RetainAbandonedResources
                            : nativeWindow.Dispose,
                        $"GLSharedContext.DestroyNative[{_threadName}]");

                try
                {
                    _signal.Dispose();
                }
                catch
                {
                }

                _thread = null;
                _cts = null;
                _window = null;
                _ownerWindow = null;
            }

            private sealed class SharedWindowEventSink : IRuntimeWindowEventSink
            {
                public void SurfaceChanged(WindowSurfaceSnapshot _) { }
                public void FocusChanged(bool _) { }
                public void FileDropped(string[] _) { }
                public void KeyDown(XREngine.Input.Devices.EKey _) { }
                public bool CloseRequested() => true;
                public void InteractiveResizeStarted() { }
                public void InteractiveResizeUpdated(XREngine.Data.Vectors.IVector2 _) { }
                public void InteractiveResizeEnded() { }
                public void RepaintRequested() { }
                public void RenderRequested(double _) { }
            }

            private static double StopwatchTicksToSeconds(long ticks)
                => ticks <= 0L ? 0.0 : (double)ticks / Stopwatch.Frequency;

            /// <summary>
            /// Drains one job from the lowest-numbered (highest-priority) non-empty bucket.
            /// Returns false when every bucket is empty.
            /// </summary>
            private bool TryDequeueHighestPriority(out SharedContextJob job)
            {
                for (int i = 0; i < PriorityBucketCount; i++)
                {
                    if (_jobs[i].TryDequeue(out job))
                    {
                        Interlocked.Decrement(ref _pendingCount);
                        return true;
                    }
                }
                job = default;
                return false;
            }

            /// <summary>
            /// Returns the oldest pending job's enqueue timestamp across all priority buckets,
            /// or 0 when no jobs are pending. Used to keep <see cref="OldestPendingAgeSeconds"/>
            /// honest in the multi-bucket layout.
            /// </summary>
            private long PeekOldestPendingTimestamp()
            {
                long oldest = 0;
                for (int i = 0; i < PriorityBucketCount; i++)
                {
                    if (_jobs[i].TryPeek(out var head))
                    {
                        if (oldest == 0 || head.EnqueuedTimestamp < oldest)
                            oldest = head.EnqueuedTimestamp;
                    }
                }
                return oldest;
            }

            private readonly record struct SharedContextJob(Action<GL> Action, string? Name, long EnqueuedTimestamp);
        }
    }
}
