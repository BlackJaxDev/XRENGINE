using Silk.NET.OpenGL;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        /// <summary>
        /// Processes <c>glProgramBinary</c> calls on a shared background GL context to avoid
        /// stalling the main render thread. Uses GLFW context sharing so program objects
        /// loaded on the background thread are immediately usable by the primary context
        /// after synchronization.
        /// <para/>
        /// In-flight uploads are capped at <see cref="MaxInFlight"/> to prevent the shared
        /// context from exhausting GPU memory while the main thread is also allocating.
        /// </summary>
        public sealed class GLProgramBinaryUploadQueue(OpenGLRenderer.GLSharedContext sharedContext)
        {
            private readonly GLSharedContext _sharedContext = sharedContext;
            private readonly ConcurrentDictionary<uint, UploadResult> _completed = new();
            private int _inFlight;
            private long _completedCount;
            private long _failedCount;
            private long _backpressureCount;

            /// <summary>
            /// Maximum number of program binary uploads that can be queued but not yet
            /// consumed by the main thread. Prevents unbounded VRAM allocation from the
            /// shared context competing with main-thread buffer/mesh uploads.
            /// </summary>
            public const int MaxInFlight = 8;

            public bool IsAvailable => _sharedContext.IsRunning;

            /// <summary>
            /// Returns <c>true</c> if the in-flight count is below <see cref="MaxInFlight"/>
            /// and a new upload can be enqueued without risking VRAM exhaustion.
            /// </summary>
            public bool CanEnqueue => Volatile.Read(ref _inFlight) < MaxInFlight;

            /// <summary>Number of uploads queued or completed but not yet consumed.</summary>
            public int InFlightCount => Volatile.Read(ref _inFlight);
            public long CompletedCount => Interlocked.Read(ref _completedCount);
            public long FailedCount => Interlocked.Read(ref _failedCount);
            public long BackpressureCount => Interlocked.Read(ref _backpressureCount);
            public double OldestPendingAgeSeconds => _sharedContext.OldestPendingAgeSeconds;
            public bool IsWorkerUnhealthy => _sharedContext.IsWorkerUnhealthy;

            public void RecordBackpressure()
                => Interlocked.Increment(ref _backpressureCount);

            public enum UploadStatus : byte { Success, Failed }

            public readonly record struct UploadResult(
                UploadStatus Status,
                GLEnum Format,
                ulong Hash,
                string CacheKey,
                double LoadMilliseconds,
                string? ErrorLog);

            /// <summary>
            /// Queues a program binary upload on the shared context thread.
            /// The GL program object must already be created (via <c>glCreateProgram</c>) on the main thread.
            /// After the upload completes, poll <see cref="TryGetResult"/> to retrieve the outcome.
            /// <para/>
            /// Check <see cref="CanEnqueue"/> before calling to respect the in-flight limit.
            /// </summary>
            public void EnqueueUpload(uint programId, byte[] binary, GLEnum format, uint length, ulong hash)
                => EnqueueUpload(programId, binary, format, length, hash, hash.ToString("X16"));

            public void EnqueueUpload(uint programId, byte[] binary, GLEnum format, uint length, ulong hash, string cacheKey)
            {
                Interlocked.Increment(ref _inFlight);
                _sharedContext.Enqueue(gl =>
                {
                    long start = Stopwatch.GetTimestamp();
                    GLEnum error;
                    int linkStatus = 0;
                    string? errorLog = null;
                    fixed (byte* ptr = binary)
                        gl.ProgramBinary(programId, format, ptr, length);

                    error = gl.GetError();
                    if (error == GLEnum.NoError)
                    {
                        gl.GetProgram(programId, GLEnum.LinkStatus, out linkStatus);
                        if (linkStatus == 0)
                            gl.GetProgramInfoLog(programId, out errorLog);
                    }
                    else
                    {
                        errorLog = error.ToString();
                    }

                    // glFinish ensures the binary is fully uploaded and validated before
                    // signaling completion. This is the recommended synchronization for
                    // shared-context resource uploads.
                    gl.Finish();

                    double loadMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - start);
                    _completed[programId] = new UploadResult(
                        error == GLEnum.NoError && linkStatus != 0 ? UploadStatus.Success : UploadStatus.Failed,
                        format,
                        hash,
                        cacheKey,
                        loadMilliseconds,
                        errorLog);
                    if (error == GLEnum.NoError && linkStatus != 0)
                        Interlocked.Increment(ref _completedCount);
                    else
                        Interlocked.Increment(ref _failedCount);
                }, $"ProgramBinaryUpload:{cacheKey}");
            }

            /// <summary>
            /// Checks whether an async binary upload has completed for the given program.
            /// Returns <c>true</c> if a result is available (success or failure).
            /// The result is consumed (removed) on retrieval, freeing an in-flight slot.
            /// </summary>
            public bool TryGetResult(uint programId, out UploadResult result)
            {
                if (_completed.TryRemove(programId, out result))
                {
                    Interlocked.Decrement(ref _inFlight);
                    return true;
                }
                return false;
            }

            private static double StopwatchTicksToMilliseconds(long ticks)
                => ticks <= 0L ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;
        }
    }
}
