using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
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
        /// Created via <see cref="WindowOptions.SharedContext"/> so that GL program objects
        /// (and other shared resources) are accessible from both the main render context
        /// and this background thread.
        /// </summary>
        public sealed class GLSharedContext : IDisposable
        {
            private readonly string _threadName;
            private readonly double _workerUnhealthySeconds;
            // Per-priority FIFO buckets (lower priority value drained first). Indexed by (byte)EProgramPriority.
            // Buckets cover the full enum range so callers can route work without bounds checks.
            private const int PriorityBucketCount = 6;
            private readonly ConcurrentQueue<SharedContextJob>[] _jobs;
            private long _pendingCount;
            private readonly AutoResetEvent _signal = new(false);
            private CancellationTokenSource? _cts;
            private Thread? _thread;
            private IWindow? _window;
            private int _disposeRequested;
            private int _disposeResourcesOnWorkerExit;
            private int _resourcesReleased;
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

            /// <summary>
            /// Creates the shared context and starts the background thread.
            /// Must be called from the main render thread while the primary GL context is current.
            /// </summary>
            public bool Initialize(XRWindow primaryWindow)
            {
                if (_running)
                    return true;

                var primaryGLContext = primaryWindow.Window.GLContext;
                if (primaryGLContext is null)
                {
                    Debug.RenderingWarning("[SharedContext] Primary window has no GL context.");
                    return false;
                }

                try
                {
                    var options = WindowOptions.Default;
                    options.Size = new Vector2D<int>(1, 1);
                    options.IsVisible = false;
                    options.API = primaryWindow.Window.API;
                    options.SharedContext = primaryGLContext;
                    options.ShouldSwapAutomatically = false;

                    Silk.NET.Windowing.Window.PrioritizeGlfw();
                    var window = Silk.NET.Windowing.Window.Create(options);
                    window.Initialize();
                    _window = window;

                    // Ensure the primary context is current on this thread after creating the shared window.
                    primaryWindow.Window.MakeCurrent();
                }
                catch (Exception ex)
                {
                    Debug.RenderingWarning($"[SharedContext] Failed to create shared GL context: {ex.Message}");
                    _window?.Dispose();
                    _window = null;
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
            /// Initializes the shared context using a pre-created shared window.
            /// The caller is responsible for creating the window with
            /// <see cref="WindowOptions.SharedContext"/> pointing at the primary context
            /// and for restoring the primary context as current afterwards.
            /// </summary>
            public bool Initialize(IWindow preCreatedSharedWindow)
            {
                if (_running)
                    return true;

                _window = preCreatedSharedWindow;
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
                if (!joined && joinTimeout > TimeSpan.Zero && thread != Thread.CurrentThread)
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

            private void Run(IWindow window, CancellationToken token)
            {
                try
                {
                    window.MakeCurrent();
                    using var gl = GL.GetApi(window.GLContext);

                    _running = true;

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
                catch (Exception ex)
                {
                    _workerUnhealthy = true;
                    Debug.RenderingWarning($"[SharedContext] Thread terminated: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    _running = false;
                    if (Volatile.Read(ref _disposeResourcesOnWorkerExit) != 0)
                        ReleaseStoppedResources(window);
                }
            }

            private void ReleaseStoppedResources(IWindow? workerWindow = null)
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

                try
                {
                    (workerWindow ?? _window)?.Dispose();
                }
                catch
                {
                }

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
