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
        public sealed class GLSharedContext(string threadName = "XR GL Shared Context") : IDisposable
        {
            private readonly ConcurrentQueue<SharedContextJob> _jobs = new();
            private readonly AutoResetEvent _signal = new(false);
            private CancellationTokenSource? _cts;
            private Thread? _thread;
            private IWindow? _window;
            private volatile bool _running;
            private long _currentJobStartTimestamp;
            private string? _currentJobName;
            private long _oldestQueuedTimestamp;
            private long _completedCount;
            private long _failedCount;
            private volatile bool _workerUnhealthy;

            private const double WorkerUnhealthySeconds = 30.0;

            public bool IsRunning => _running && !IsWorkerUnhealthy;
            public bool IsThreadAlive => _running;
            public bool IsWorkerUnhealthy => _workerUnhealthy || CurrentJobElapsedSeconds >= WorkerUnhealthySeconds;
            public int PendingCount => _jobs.Count;
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
                    Name = threadName,
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
                    Name = threadName,
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
                => Enqueue(job, null);

            public void Enqueue(Action<GL> job, string? name)
            {
                long now = Stopwatch.GetTimestamp();
                if (_jobs.IsEmpty)
                    Interlocked.Exchange(ref _oldestQueuedTimestamp, now);

                _jobs.Enqueue(new SharedContextJob(job, name, now));
                _signal.Set();
            }

            public void Dispose()
            {
                _cts?.Cancel();
                _signal.Set();
                _thread?.Join(TimeSpan.FromSeconds(2));
                _cts?.Dispose();
                _running = false;

                // IWindow.Dispose should be safe to call from any thread for headless GLFW windows.
                _window?.Dispose();

                _thread = null;
                _cts = null;
                _window = null;
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
                        if (!_jobs.TryDequeue(out var job))
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

                            if (_jobs.TryPeek(out var oldest))
                                Interlocked.Exchange(ref _oldestQueuedTimestamp, oldest.EnqueuedTimestamp);
                            else
                                Interlocked.Exchange(ref _oldestQueuedTimestamp, 0);
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
                }
            }

            private static double StopwatchTicksToSeconds(long ticks)
                => ticks <= 0L ? 0.0 : (double)ticks / Stopwatch.Frequency;

            private readonly record struct SharedContextJob(Action<GL> Action, string? Name, long EnqueuedTimestamp);
        }
    }
}
