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
                LogRenderingUploadEvent(
                    "ENQUEUE",
                    programId,
                    hash,
                    cacheKey,
                    format,
                    length,
                    $"inFlight={InFlightCount}/{MaxInFlight}");
                Interlocked.Increment(ref _inFlight);
                _sharedContext.Enqueue(gl =>
                {
                    LogRenderingUploadEvent("WORKER_BEGIN", programId, hash, cacheKey, format, length, null);
                    long start = Stopwatch.GetTimestamp();
                    GLEnum error;
                    int linkStatus = 0;
                    string? errorLog = null;
                    fixed (byte* ptr = binary)
                    {
                        long programBinaryStart = Stopwatch.GetTimestamp();
                        gl.ProgramBinary(programId, format, ptr, length);
                        if (ShouldLogRenderingShaderLinkVerbose())
                        {
                            double programBinaryMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - programBinaryStart);
                            LogRenderingWorkerGlCall(
                                "glProgramBinary",
                                programId,
                                hash,
                                cacheKey,
                                programBinaryMilliseconds,
                                $"binaryBytes={length} binaryFormat={format}");
                        }
                    }

                    error = GLEnum.NoError;
                    MeasureRenderingWorkerGlCall(
                        "glGetError",
                        programId,
                        hash,
                        cacheKey,
                        () => error = gl.GetError(),
                        "phase=binary-upload-error-check");
                    if (error == GLEnum.NoError)
                    {
                        MeasureRenderingWorkerGlCall(
                            "glGetProgramiv(GL_LINK_STATUS)",
                            programId,
                            hash,
                            cacheKey,
                            () => gl.GetProgram(programId, GLEnum.LinkStatus, out linkStatus),
                            "phase=binary-upload-link-status");
                        if (linkStatus == 0)
                        {
                            MeasureRenderingWorkerGlCall(
                                "glGetProgramInfoLog",
                                programId,
                                hash,
                                cacheKey,
                                () => gl.GetProgramInfoLog(programId, out errorLog),
                                "phase=binary-upload-link-log");
                        }
                    }
                    else
                    {
                        errorLog = error.ToString();
                    }

                    // glFinish ensures the binary is fully uploaded and validated before
                    // signaling completion. This is the recommended synchronization for
                    // shared-context resource uploads.
                    MeasureRenderingWorkerGlCall(
                        "glFinish",
                        programId,
                        hash,
                        cacheKey,
                        () => gl.Finish(),
                        "phase=binary-upload-handoff");

                    double loadMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - start);
                    LogRenderingUploadEvent(
                        error == GLEnum.NoError && linkStatus != 0 ? "WORKER_READY" : "WORKER_FAILED",
                        programId,
                        hash,
                        cacheKey,
                        format,
                        length,
                        $"loadMs={loadMilliseconds:F2} error={errorLog ?? error.ToString()}");
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

            private static double MeasureRenderingWorkerGlCall(
                string callName,
                uint programId,
                ulong hash,
                string cacheKey,
                Action action,
                string? detail = null)
            {
                if (!ShouldLogRenderingShaderLinkVerbose())
                {
                    action();
                    return 0.0;
                }

                long startTimestamp = Stopwatch.GetTimestamp();
                action();
                double elapsedMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - startTimestamp);
                LogRenderingWorkerGlCall(callName, programId, hash, cacheKey, elapsedMilliseconds, detail);
                return elapsedMilliseconds;
            }

            private static void LogRenderingWorkerGlCall(
                string callName,
                uint programId,
                ulong hash,
                string cacheKey,
                double elapsedMilliseconds,
                string? detail = null)
            {
                bool renderThread = Engine.IsRenderThread;
                Debug.Rendering(
                    EOutputVerbosity.Verbose,
                    false,
                    "[ShaderGLCall] call={0} program='<binary-upload-worker>' hash={1} programId={2} cacheKey={3} elapsedMs={4:F3} renderThread={5} renderThreadStallMs={6:F3}{7}.",
                    callName,
                    hash,
                    programId,
                    cacheKey,
                    elapsedMilliseconds,
                    renderThread,
                    renderThread ? elapsedMilliseconds : 0.0,
                    FormatRenderingDetail(detail));
            }

            private static void LogRenderingUploadEvent(
                string eventName,
                uint programId,
                ulong hash,
                string cacheKey,
                GLEnum format,
                uint length,
                string? detail)
            {
                if (!ShouldLogRenderingShaderLinkVerbose())
                    return;

                Debug.Rendering(
                    EOutputVerbosity.Verbose,
                    false,
                    "[ShaderBinaryUpload] {0} programId={1} hash={2} cacheKey={3} binaryBytes={4} binaryFormat={5} renderThread={6}{7}.",
                    eventName,
                    programId,
                    hash,
                    cacheKey,
                    length,
                    format,
                    Engine.IsRenderThread,
                    FormatRenderingDetail(detail));
            }

            private static bool ShouldLogRenderingShaderLinkVerbose()
                => Debug.AllowOutput && RuntimeDebugHostServices.Current.OutputVerbosity >= EOutputVerbosity.Verbose;

            private static string FormatRenderingDetail(string? detail)
                => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" detail='{detail.Replace('\'', '"')}'";

            private static double StopwatchTicksToMilliseconds(long ticks)
                => ticks <= 0L ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;
        }
    }
}
