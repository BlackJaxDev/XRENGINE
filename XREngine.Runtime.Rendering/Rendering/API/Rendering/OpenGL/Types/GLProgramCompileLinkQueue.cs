using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using XREngine.Rendering.Shaders;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        /// <summary>
        /// Dispatches shader compilation and program linking to one or more shared GL
        /// context worker threads. This eliminates main-thread stalls on drivers that
        /// lack <c>GL_ARB_parallel_shader_compile</c> by performing all blocking GL
        /// compiler work on background threads, then synchronizing via <c>glFinish()</c>.
        /// <para/>
        /// Shader objects are created, sourced, compiled, attached, and destroyed
        /// entirely on the shared context that owns the job (they share the same object
        /// namespace via GLFW context sharing). The program object (identified by
        /// <paramref name="programId"/>) must already exist — it was created on the
        /// main thread. With multiple workers, jobs are dispatched to the least-loaded
        /// worker so a single hot fragment-shader link cannot starve the queue tail.
        /// </summary>
        public sealed class GLProgramCompileLinkQueue
        {
            private readonly GLSharedContext[] _workers;
            private readonly bool _completionStatusPollingEnabled;
            private readonly ConcurrentDictionary<uint, CompileResult> _completed = new();
            private readonly ConcurrentDictionary<uint, byte> _inFlightProgramIds = new();
            private readonly ConcurrentDictionary<uint, byte> _cancelledProgramIds = new();
            private int _roundRobinCursor;
            private int _inFlight;
            private long _completedCount;
            private long _failedCount;
            private long _rejectedCount;
            private long _backpressureCount;
            private const int WorkerCompletionFastPollIterations = 64;
            private const double WorkerCompletionStuckFlushMilliseconds = 5000.0;
            private const double DefaultWorkerCompletionHardAbandonMilliseconds = 180000.0;
            private static readonly double WorkerCompletionHardAbandonMilliseconds = ResolveWorkerCompletionHardAbandonMilliseconds();
            private static readonly bool DisableCompletionPollingForSharedContextWorkerPrograms = string.Equals(
                Environment.GetEnvironmentVariable("XRE_SHARED_CONTEXT_DISABLE_COMPLETION_POLLING"),
                "1",
                StringComparison.Ordinal);
            private static readonly bool SuppressParallelCompileForSingleStageWorkerPrograms =
                string.Equals(
                    Environment.GetEnvironmentVariable("XRE_SHARED_CONTEXT_HAZARD_DISABLE_PARALLEL"),
                    "1",
                    StringComparison.Ordinal);

            /// <summary>
            /// Per-worker in-flight cap. Compilation is heavier than binary upload;
            /// keep the per-worker count lower to avoid GPU memory pressure and
            /// driver contention. Total in-flight is <see cref="MaxInFlight"/>
            /// multiplied by the worker count.
            /// </summary>
            public const int MaxInFlight = 4;
            private const int InteractivePriorityReservePerWorker = 2;

            public GLProgramCompileLinkQueue(GLSharedContext sharedContext)
                : this(new[] { sharedContext }, completionStatusPollingEnabled: false)
            {
            }

            public GLProgramCompileLinkQueue(IReadOnlyList<GLSharedContext> sharedContexts)
                : this(sharedContexts, completionStatusPollingEnabled: false)
            {
            }

            internal GLProgramCompileLinkQueue(IReadOnlyList<GLSharedContext> sharedContexts, bool completionStatusPollingEnabled)
            {
                if (sharedContexts is null || sharedContexts.Count == 0)
                    throw new ArgumentException("At least one shared context is required.", nameof(sharedContexts));

                _workers = new GLSharedContext[sharedContexts.Count];
                for (int i = 0; i < sharedContexts.Count; i++)
                    _workers[i] = sharedContexts[i] ?? throw new ArgumentNullException(nameof(sharedContexts));

                _completionStatusPollingEnabled =
                    completionStatusPollingEnabled &&
                    !DisableCompletionPollingForSharedContextWorkerPrograms;
            }

            public int WorkerCount => _workers.Length;
            public int MaxInFlightTotal => MaxInFlight * _workers.Length;

            public bool IsAvailable
            {
                get
                {
                    for (int i = 0; i < _workers.Length; i++)
                        if (_workers[i].IsRunning)
                            return true;
                    return false;
                }
            }

            public bool CanEnqueue => CanEnqueuePriority(EProgramPriority.Main);
            public bool CanEnqueuePriority(EProgramPriority priority)
            {
                int limit = MaxInFlightTotal;
                if (priority == EProgramPriority.Interactive)
                    limit += InteractivePriorityReservePerWorker * _workers.Length;

                return Volatile.Read(ref _inFlight) < limit;
            }
            public int InFlightCount => Volatile.Read(ref _inFlight);
            public long CompletedCount => Interlocked.Read(ref _completedCount);
            public long FailedCount => Interlocked.Read(ref _failedCount);
            public long RejectedCount => Interlocked.Read(ref _rejectedCount);
            public long BackpressureCount => Interlocked.Read(ref _backpressureCount);

            private static double ResolveWorkerCompletionHardAbandonMilliseconds()
            {
                string? configured = Environment.GetEnvironmentVariable("XRE_SHARED_CONTEXT_LINK_TIMEOUT_MS");
                if (double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out double milliseconds) &&
                    milliseconds >= WorkerCompletionStuckFlushMilliseconds)
                {
                    return milliseconds;
                }

                return DefaultWorkerCompletionHardAbandonMilliseconds;
            }

            public double OldestPendingAgeSeconds
            {
                get
                {
                    double oldest = 0.0;
                    for (int i = 0; i < _workers.Length; i++)
                    {
                        double age = _workers[i].OldestPendingAgeSeconds;
                        if (age > oldest)
                            oldest = age;
                    }
                    return oldest;
                }
            }

            /// <summary>
            /// True only if every running worker is unhealthy (or none are running).
            /// Caller code that gates on this should still check <see cref="IsAvailable"/>
            /// to know whether to issue work at all.
            /// </summary>
            public bool IsWorkerUnhealthy
            {
                get
                {
                    bool anyRunning = false;
                    for (int i = 0; i < _workers.Length; i++)
                    {
                        if (!_workers[i].IsRunning)
                            continue;

                        anyRunning = true;
                        if (!_workers[i].IsWorkerUnhealthy)
                            return false;
                    }
                    return anyRunning;
                }
            }

            /// <summary>
            /// Picks the least-loaded running worker for the next compile/link job.
            /// Round-robin advance is used as a tiebreaker so equal-depth queues fan
            /// out evenly. Returns <c>null</c> if no worker is running.
            /// </summary>
            private GLSharedContext? PickWorker()
            {
                int start = Interlocked.Increment(ref _roundRobinCursor);
                GLSharedContext? best = null;
                int bestPending = int.MaxValue;
                for (int i = 0; i < _workers.Length; i++)
                {
                    int idx = ((start + i) % _workers.Length + _workers.Length) % _workers.Length;
                    var worker = _workers[idx];
                    if (!worker.IsRunning)
                        continue;
                    int pending = worker.PendingCount;
                    if (pending < bestPending)
                    {
                        bestPending = pending;
                        best = worker;
                        if (pending == 0)
                            break;
                    }
                }
                return best;
            }

            public enum CompileStatus : byte
            {
                Success,
                CompileFailed,
                LinkFailed,
                RejectedHazard,
                QueueFull,
            }

            public readonly record struct ShaderInput(string ResolvedSource, ShaderType Type);

            public readonly record struct CompileResult(
                CompileStatus Status,
                string? ErrorLog,
                double CompileMilliseconds,
                double LinkMilliseconds);

            /// <summary>
            /// Queues a full compile → attach → link pipeline on the shared context thread.
            /// The program object <paramref name="programId"/> must already be created on the main thread.
            /// Shader objects are created and destroyed on the shared context; only the linked
            /// program survives.
            /// </summary>
            public void EnqueueCompileAndLink(uint programId, ShaderInput[] shaders)
            {
                if (!TryEnqueueCompileAndLink(programId, shaders, EProgramPriority.Main, out string? rejectReason))
                    throw new InvalidOperationException(rejectReason ?? "Unable to enqueue OpenGL compile/link job.");
            }

            public bool TryEnqueueCompileAndLink(uint programId, ShaderInput[] shaders, out string? rejectReason)
                => TryEnqueueCompileAndLink(programId, shaders, EProgramPriority.Main, out rejectReason);

            /// <summary>
            /// Queues a compile/attach/link job tagged with a priority bucket. Lower-valued
            /// priorities (<see cref="EProgramPriority.Interactive"/>, then <see cref="EProgramPriority.Main"/>)
            /// are linked before higher-valued background work (<see cref="EProgramPriority.Shadow"/>, <see cref="EProgramPriority.VR"/>,
            /// <see cref="EProgramPriority.Compute"/>) inside the shared-context worker.
            /// </summary>
            public bool TryEnqueueCompileAndLink(uint programId, ShaderInput[] shaders, EProgramPriority priority, out string? rejectReason)
            {
                ShaderInputSummary summary = SummarizeShaderInputs(shaders);
                if (ContainsKnownAsyncLinkHazard(shaders))
                {
                    rejectReason = "known async-link hazard";
                    LogRenderingQueueEvent("REJECTED_HAZARD", programId, summary, rejectReason);
                    Interlocked.Increment(ref _rejectedCount);
                    return false;
                }

                if (!CanEnqueuePriority(priority))
                {
                    rejectReason = "compile/link queue is at capacity";
                    LogRenderingQueueEvent("BACKPRESSURE", programId, summary, rejectReason);
                    Interlocked.Increment(ref _backpressureCount);
                    return false;
                }

                GLSharedContext? worker = PickWorker();
                if (worker is null)
                {
                    rejectReason = "no shared-context worker is running";
                    LogRenderingQueueEvent("BACKPRESSURE", programId, summary, rejectReason);
                    Interlocked.Increment(ref _backpressureCount);
                    return false;
                }

                rejectReason = null;
                LogRenderingQueueEvent(
                    "ENQUEUE",
                    programId,
                    summary,
                    $"inFlight={InFlightCount}/{MaxInFlightTotal} workers={_workers.Length} pickedPending={worker.PendingCount}");
                _inFlightProgramIds[programId] = 0;
                Interlocked.Increment(ref _inFlight);
                worker.Enqueue(gl =>
                {
                    try
                    {
                        LogRenderingQueueEvent("WORKER_BEGIN", programId, summary, null);

                        // When worker contexts run with ARB/KHR parallel shader compile
                        // enabled, GL_COMPILE_STATUS/GL_LINK_STATUS become blocking waits.
                        // On some drivers those waits hold a driver-wide compiler lock and
                        // stall unrelated render-thread GL calls (for example glCreateProgram).
                        // Poll GL_COMPLETION_STATUS first so status queries are only issued
                        // after the driver says the compile/link is finished.
                        bool useWorkerCompletionPolling = _completionStatusPollingEnabled;
                        bool hazardSuppressParallel = ShouldSuppressParallelCompileForWorkerProgram(shaders);
                        ArbParallelShaderCompile? hazardArbExt = null;
                        if (hazardSuppressParallel && gl.TryGetExtension(out ArbParallelShaderCompile arbForHazard))
                        {
                            hazardArbExt = arbForHazard;
                            try
                            {
                                MeasureRenderingWorkerGlCall(
                                    "glMaxShaderCompilerThreadsARB",
                                    programId,
                                    0,
                                    null,
                                    () => arbForHazard.MaxShaderCompilerThreads(0u),
                                    "worker=source-compile-thread-suppression threads=0");
                            }
                            catch { hazardArbExt = null; }
                        }
                        bool workerCompilerThreadsSuppressed = hazardArbExt is not null;

                        long compileStartTimestamp = Stopwatch.GetTimestamp();
                        uint[] shaderIds = new uint[shaders.Length];
                        bool allCompiled = true;
                        string? errorLog = null;

                        for (int i = 0; i < shaders.Length; i++)
                        {
                            ref readonly ShaderInput input = ref shaders[i];
                            ShaderType shaderType = input.Type;
                            string resolvedSource = input.ResolvedSource;
                            uint sid = 0;
                            MeasureRenderingWorkerGlCall(
                                "glCreateShader",
                                programId,
                                0,
                                shaderType,
                                () => sid = gl.CreateShader(shaderType),
                                "worker=source-compile");
                            shaderIds[i] = sid;

                            MeasureRenderingWorkerGlCall(
                                "glShaderSource",
                                programId,
                                sid,
                                shaderType,
                                () => gl.ShaderSource(sid, resolvedSource),
                                $"bytes={CountUtf8Bytes(resolvedSource)} lines={CountLines(resolvedSource)} worker=source-compile");
                            MeasureRenderingWorkerGlCall(
                                "glCompileShader",
                                programId,
                                sid,
                                shaderType,
                                () => gl.CompileShader(sid),
                                "worker=source-compile");

                            // Poll non-blocking GL_COMPLETION_STATUS_ARB before issuing the
                            // blocking GL_COMPILE_STATUS query. On NVIDIA, a blocking status
                            // query during a long cold compile holds a driver-wide lock that
                            // starves the main GL context (no render-thread GL calls progress
                            // until the worker's query returns). Polling with a short sleep
                            // releases that lock between queries.
                            if (useWorkerCompletionPolling && !PollCompletionStatusBlocking(
                                gl,
                                worker,
                                programId,
                                sid,
                                shaderType,
                                isShader: true,
                                phase: "worker=source-compile-completion-poll",
                                out string? compilePollFailure))
                            {
                                double compileMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - compileStartTimestamp);
                                errorLog = compilePollFailure ?? "Shared-context shader compile completion poll aborted.";
                                LogRenderingQueueEvent(
                                    "WORKER_COMPILE_ABANDONED",
                                    programId,
                                    summary,
                                    $"compileMs={compileMilliseconds:F2} error={errorLog}");
                                PublishCompletedResult(
                                    programId,
                                    new CompileResult(
                                        CompileStatus.CompileFailed,
                                        errorLog,
                                        compileMilliseconds,
                                        0.0));
                                Interlocked.Increment(ref _failedCount);

                                if (hazardArbExt is not null)
                                {
                                    try
                                    {
                                        MeasureRenderingWorkerGlCall(
                                            "glMaxShaderCompilerThreadsARB",
                                            programId,
                                            0,
                                            null,
                                            () => hazardArbExt.MaxShaderCompilerThreads(0xFFFF_FFFFu),
                                            "worker=source-compile-thread-restore threads=implementation-max");
                                    }
                                    catch { /* best-effort restore */ }
                                }
                                return;
                            }

                            int status = 0;
                            MeasureRenderingWorkerGlCall(
                                "glGetShaderiv(GL_COMPILE_STATUS)",
                                programId,
                                sid,
                                shaderType,
                                () => gl.GetShader(sid, ShaderParameterName.CompileStatus, out status),
                                "worker=source-compile-status");
                            if (status == 0)
                            {
                                string? info = null;
                                MeasureRenderingWorkerGlCall(
                                    "glGetShaderInfoLog",
                                    programId,
                                    sid,
                                    shaderType,
                                    () => gl.GetShaderInfoLog(sid, out info),
                                    "worker=source-compile-log");
                                errorLog = info;
                                allCompiled = false;

                                // Clean up all created shaders so far.
                                for (int j = 0; j <= i; j++)
                                {
                                    uint deleteShaderId = shaderIds[j];
                                    MeasureRenderingWorkerGlCall(
                                        "glDeleteShader",
                                        programId,
                                        deleteShaderId,
                                        shaders[j].Type,
                                        () => gl.DeleteShader(deleteShaderId),
                                        "worker=compile-failed-cleanup");
                                }
                                break;
                            }
                        }

                        if (!allCompiled)
                        {
                            double compileMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - compileStartTimestamp);
                            LogRenderingQueueEvent(
                                "WORKER_COMPILE_FAILED",
                                programId,
                                summary,
                                $"compileMs={compileMilliseconds:F2} error={errorLog ?? "<none>"}");
                            PublishCompletedResult(
                                programId,
                                new CompileResult(CompileStatus.CompileFailed, errorLog, compileMilliseconds, 0.0));
                            Interlocked.Increment(ref _failedCount);

                            // Restore compiler thread count if the legacy suppression path changed it.
                            if (hazardArbExt is not null)
                            {
                                try
                                {
                                    MeasureRenderingWorkerGlCall(
                                        "glMaxShaderCompilerThreadsARB",
                                        programId,
                                        0,
                                        null,
                                        () => hazardArbExt.MaxShaderCompilerThreads(0xFFFF_FFFFu),
                                        "worker=source-compile-thread-restore threads=implementation-max");
                                }
                                catch { /* best-effort restore */ }
                            }
                            return;
                        }

                        double compileMillisecondsCompleted = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - compileStartTimestamp);

                        // Attach all compiled shaders to the program.
                        for (int i = 0; i < shaderIds.Length; i++)
                        {
                            uint shaderId = shaderIds[i];
                            ShaderType shaderType = shaders[i].Type;
                            MeasureRenderingWorkerGlCall(
                                "glAttachShader",
                                programId,
                                shaderId,
                                shaderType,
                                () => gl.AttachShader(programId, shaderId),
                                "worker=source-link-attach");
                        }

                        long linkStartTimestamp = Stopwatch.GetTimestamp();
                        MeasureRenderingWorkerGlCall(
                            "glLinkProgram",
                            programId,
                            0,
                            null,
                            () => gl.LinkProgram(programId),
                            workerCompilerThreadsSuppressed
                                ? "worker=source-link compiler-threads-suppressed"
                                : useWorkerCompletionPolling
                                    ? "worker=source-link completion-polling"
                                    : "worker=source-link completion-polling-disabled");

                        // Poll non-blocking GL_COMPLETION_STATUS_ARB before issuing the
                        // blocking GL_LINK_STATUS query. The blocking query holds a
                        // driver-wide lock on NVIDIA that prevents the main GL context
                        // from making any progress for the entire duration of a cold
                        // link (observed: 109+ seconds on a 387 KB single-stage
                        // separable fragment program), freezing the render thread.
                        if (useWorkerCompletionPolling && !PollCompletionStatusBlocking(
                            gl,
                            worker,
                            programId,
                            0,
                            null,
                            isShader: false,
                            phase: "worker=source-link-completion-poll",
                            out string? linkPollFailure))
                        {
                            double linkMillisecondsAbandoned = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - linkStartTimestamp);
                            string abandonedLinkError = linkPollFailure ?? "Shared-context program link completion poll aborted.";
                            LogRenderingQueueEvent(
                                "WORKER_LINK_ABANDONED",
                                programId,
                                summary,
                                $"compileMs={compileMillisecondsCompleted:F2} linkMs={linkMillisecondsAbandoned:F2} error={abandonedLinkError}");
                            PublishCompletedResult(
                                programId,
                                new CompileResult(
                                    CompileStatus.LinkFailed,
                                    abandonedLinkError,
                                    compileMillisecondsCompleted,
                                    linkMillisecondsAbandoned));
                            Interlocked.Increment(ref _failedCount);

                            if (hazardArbExt is not null)
                            {
                                try
                                {
                                    MeasureRenderingWorkerGlCall(
                                        "glMaxShaderCompilerThreadsARB",
                                        programId,
                                        0,
                                        null,
                                        () => hazardArbExt.MaxShaderCompilerThreads(0xFFFF_FFFFu),
                                        "worker=source-compile-thread-restore threads=implementation-max");
                                }
                                catch { /* best-effort restore */ }
                            }
                            return;
                        }

                        int linkStatus = 0;
                        MeasureRenderingWorkerGlCall(
                            "glGetProgramiv(GL_LINK_STATUS)",
                            programId,
                            0,
                            null,
                            () => gl.GetProgram(programId, ProgramPropertyARB.LinkStatus, out linkStatus),
                            "worker=source-link-status");

                        string? linkError = null;
                        if (linkStatus == 0)
                        {
                            MeasureRenderingWorkerGlCall(
                                "glGetProgramInfoLog",
                                programId,
                                0,
                                null,
                                () => gl.GetProgramInfoLog(programId, out linkError),
                                "worker=source-link-log");
                        }

                        // Restore compiler thread count if the legacy suppression path changed it.
                        if (hazardArbExt is not null)
                        {
                            try
                            {
                                MeasureRenderingWorkerGlCall(
                                    "glMaxShaderCompilerThreadsARB",
                                    programId,
                                    0,
                                    null,
                                    () => hazardArbExt.MaxShaderCompilerThreads(0xFFFF_FFFFu),
                                    "worker=source-compile-thread-restore threads=implementation-max");
                            }
                            catch { /* best-effort restore */ }
                        }

                        // Detach and delete shader objects — no longer needed after linking.
                        for (int i = 0; i < shaderIds.Length; i++)
                        {
                            uint shaderId = shaderIds[i];
                            ShaderType shaderType = shaders[i].Type;
                            MeasureRenderingWorkerGlCall(
                                "glDetachShader",
                                programId,
                                shaderId,
                                shaderType,
                                () => gl.DetachShader(programId, shaderId),
                                "worker=source-link-detach");
                            MeasureRenderingWorkerGlCall(
                                "glDeleteShader",
                                programId,
                                shaderId,
                                shaderType,
                                () => gl.DeleteShader(shaderId),
                                "worker=source-link-delete-shader");
                        }

                        // Synchronize so the linked program is usable on the main context.
                        MeasureRenderingWorkerGlCall(
                            "glFinish",
                            programId,
                            0,
                            null,
                            () => gl.Finish(),
                            "worker=source-link-handoff");

                        double linkMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - linkStartTimestamp);
                        LogRenderingQueueEvent(
                            linkStatus != 0 ? "WORKER_READY" : "WORKER_LINK_FAILED",
                            programId,
                            summary,
                            $"compileMs={compileMillisecondsCompleted:F2} linkMs={linkMilliseconds:F2} error={linkError ?? "<none>"}");

                        PublishCompletedResult(
                            programId,
                            new CompileResult(
                                linkStatus != 0 ? CompileStatus.Success : CompileStatus.LinkFailed,
                                linkError,
                                compileMillisecondsCompleted,
                                linkMilliseconds));
                        if (linkStatus != 0)
                            Interlocked.Increment(ref _completedCount);
                        else
                            Interlocked.Increment(ref _failedCount);
                    }
                    catch (Exception ex)
                    {
                        string error = $"Shared-context source compile/link worker threw: {ex.GetType().Name}: {ex.Message}";
                        LogRenderingQueueEvent("WORKER_EXCEPTION", programId, summary, error);
                        Debug.OpenGLWarning($"[ShaderLinkQueue] {error}");
                        PublishCompletedResult(
                            programId,
                            new CompileResult(
                                CompileStatus.LinkFailed,
                                error,
                                0.0,
                                0.0));
                        Interlocked.Increment(ref _failedCount);
                    }
                }, $"ProgramSourceCompile:{programId}", priority);
                return true;
            }

            /// <summary>
            /// Polls <c>GL_COMPLETION_STATUS_ARB</c> on the worker context until the
            /// driver reports the most recent compile or link is complete. Sleeps
            /// briefly between polls so the NVIDIA driver can release its internal
            /// shader-compiler lock and let the main GL context continue making
            /// progress. Without this, a blocking <c>glGetShaderiv(GL_COMPILE_STATUS)</c>
            /// or <c>glGetProgramiv(GL_LINK_STATUS)</c> on the worker can stall every
            /// render-thread GL call for the full duration of a cold compile/link
            /// (observed: 100+ seconds for large imported-model fragment shaders).
            /// </summary>
            private static bool PollCompletionStatusBlocking(
                GL gl,
                GLSharedContext worker,
                uint programId,
                uint shaderId,
                ShaderType? shaderType,
                bool isShader,
                string phase,
                out string? failureReason)
            {
                failureReason = null;
                // Initial fast-poll burst — most binary cache hits and warm links
                // complete in microseconds. After the burst, back off to a 1 ms
                // sleep to avoid spinning during long cold compiles.
                int iterations = 0;
                bool flushIssued = false;
                long startTimestamp = Stopwatch.GetTimestamp();
                string operation = isShader ? "shader compile" : "program link";
                while (true)
                {
                    if (worker.IsDisposeRequested)
                    {
                        failureReason = $"Shared-context {operation} completion poll aborted because worker disposal was requested.";
                        return false;
                    }

                    int complete = 0;
                    if (isShader)
                    {
                        MeasureRenderingWorkerGlCall(
                            "glGetShaderiv(GL_COMPLETION_STATUS)",
                            programId,
                            shaderId,
                            shaderType,
                            () => gl.GetShader(shaderId, (GLEnum)GLShader.GL_COMPLETION_STATUS_ARB, out complete),
                            phase);
                    }
                    else
                    {
                        MeasureRenderingWorkerGlCall(
                            "glGetProgramiv(GL_COMPLETION_STATUS)",
                            programId,
                            shaderId,
                            shaderType,
                            () => gl.GetProgram(programId, (GLEnum)GLShader.GL_COMPLETION_STATUS_ARB, out complete),
                            phase);
                    }

                    if (complete != 0)
                        return true;

                    if (worker.IsDisposeRequested)
                    {
                        failureReason = $"Shared-context {operation} completion poll aborted because worker disposal was requested.";
                        return false;
                    }

                    double elapsedMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - startTimestamp);
                    if (!flushIssued && elapsedMilliseconds >= WorkerCompletionStuckFlushMilliseconds)
                    {
                        flushIssued = true;
                        Debug.OpenGLWarning(
                            $"[ShaderLinkQueue] Worker {operation} programId={programId} still reports COMPLETION_STATUS=false " +
                            $"after {elapsedMilliseconds / 1000.0:F2}s; issuing glFlush() and continuing non-blocking poll.");
                        MeasureRenderingWorkerGlCall(
                            "glFlush",
                            programId,
                            shaderId,
                            shaderType,
                            () => gl.Flush(),
                            phase + "-stuck-nudge");
                    }

                    if (elapsedMilliseconds >= WorkerCompletionHardAbandonMilliseconds)
                    {
                        failureReason =
                            $"Shared-context {operation} did not report completion after {elapsedMilliseconds / 1000.0:F2}s; abandoned to keep the async link queue moving.";
                        Debug.OpenGLWarning(
                            $"[ShaderLinkQueue] Worker {operation} programId={programId} stuck with COMPLETION_STATUS=false " +
                            $"after {elapsedMilliseconds / 1000.0:F2}s; publishing a failed async result without querying final status.");
                        return false;
                    }

                    iterations++;
                    if (iterations < WorkerCompletionFastPollIterations)
                        Thread.Yield();
                    else
                        Thread.Sleep(1);
                }
            }

            /// <summary>
            /// Cancels ownership of a queued source compile/link result. If the worker
            /// already completed, the result is consumed immediately; otherwise the worker
            /// drops the result when it finishes so the in-flight slot is released.
            /// </summary>
            public bool CancelCompileAndLink(uint programId)
            {
                if (programId == 0 || !_inFlightProgramIds.ContainsKey(programId))
                    return false;

                _cancelledProgramIds[programId] = 0;
                if (_completed.TryRemove(programId, out _))
                {
                    _cancelledProgramIds.TryRemove(programId, out _);
                    CompleteCancelledCompile(programId);
                }
                return true;
            }

            /// <summary>
            /// Returns true once a worker has published a source compile/link result for
            /// <paramref name="programId"/> but before the owning program consumes it.
            /// </summary>
            public bool HasResult(uint programId)
                => programId != 0 && _completed.ContainsKey(programId);

            private void PublishCompletedResult(uint programId, CompileResult result)
            {
                _completed[programId] = result;
                if (_cancelledProgramIds.TryRemove(programId, out _))
                {
                    _completed.TryRemove(programId, out _);
                    CompleteCancelledCompile(programId);
                }
            }

            /// <summary>
            /// Checks whether an async compile+link has completed for the given program.
            /// The result is consumed (removed) on retrieval, freeing an in-flight slot.
            /// </summary>
            public bool TryGetResult(uint programId, out CompileResult result)
            {
                if (_completed.TryRemove(programId, out result))
                {
                    _cancelledProgramIds.TryRemove(programId, out _);
                    _inFlightProgramIds.TryRemove(programId, out _);
                    Interlocked.Decrement(ref _inFlight);
                    return true;
                }
                return false;
            }

            private void CompleteCancelledCompile(uint programId)
            {
                if (_inFlightProgramIds.TryRemove(programId, out _))
                    Interlocked.Decrement(ref _inFlight);
            }

            public static bool ContainsKnownAsyncLinkHazard(ReadOnlySpan<ShaderInput> shaders)
            {
                // No shader stages are unconditionally rejected here anymore.
                //
                // Compute programs were previously rejected because
                // glDispatchCompute implicitly waits for link completion on
                // NVIDIA, so a hung parallel link would deadlock the render
                // thread on the next dispatch. The synchronous render-thread
                // fallback that handled them, however, was itself a worse
                // hazard: it took the driver compile lock on the render
                // context for the full duration of a cold compute link, which
                // serialized against any in-flight worker links and (when
                // graphics single-stage links were still finishing) starved
                // the GPU command queue long enough to trip the Windows TDR
                // 2-second watchdog (observed 2026-05-07: WATCHDOG-141 +
                // STATUS_INVALID_HANDLE in ntdll right after a sync compute
                // fallback firing inside RenderCallback).
                //
                // Compute now goes through the same shared-context worker
                // path as graphics, but is treated as a single-stage hazard
                // (parallel compile suppressed on the worker context for the
                // entire compile+link block, then restored). The worker
                // already issues glFinish before publishing the result, so by
                // the time the render thread picks up the linked program the
                // link is fully complete and glDispatchCompute will not
                // serialize on it.
                _ = shaders;
                return false;
            }

            /// <summary>
            /// True when the legacy worker-side parallel compile suppression path is explicitly requested.
            /// </summary>
            private static bool ShouldSuppressParallelCompileForWorkerProgram(ReadOnlySpan<ShaderInput> shaders)
                => SuppressParallelCompileForSingleStageWorkerPrograms && IsSingleStageSeparableGraphicsHazard(shaders);

            private static bool IsSingleStageSeparableGraphicsHazard(ReadOnlySpan<ShaderInput> shaders)
            {
                if (shaders.Length != 1)
                    return false;

                ShaderType type = shaders[0].Type;
                return type == ShaderType.VertexShader
                    || type == ShaderType.FragmentShader
                    || type == ShaderType.ComputeShader;
            }

            /// <summary>
            /// Maps engine shader types to Silk.NET GL shader types.
            /// Duplicated from GLShader to avoid coupling the background queue to GL wrapper objects.
            /// </summary>
            public static ShaderType ToGLShaderType(EShaderType mode)
                => mode switch
                {
                    EShaderType.Vertex => ShaderType.VertexShader,
                    EShaderType.Fragment => ShaderType.FragmentShader,
                    EShaderType.Geometry => ShaderType.GeometryShader,
                    EShaderType.TessControl => ShaderType.TessControlShader,
                    EShaderType.TessEvaluation => ShaderType.TessEvaluationShader,
                    EShaderType.Compute => ShaderType.ComputeShader,
                    EShaderType.Task => (ShaderType)0x955A,
                    EShaderType.Mesh => (ShaderType)0x9559,
                    _ => ShaderType.FragmentShader
                };

            private readonly record struct ShaderInputSummary(
                int ShaderCount,
                long SourceBytes,
                int SourceLines,
                string StageList);

            private static ShaderInputSummary SummarizeShaderInputs(ReadOnlySpan<ShaderInput> shaders)
            {
                if (shaders.Length == 0)
                    return new ShaderInputSummary(0, 0, 0, "<none>");

                long bytes = 0;
                int lines = 0;
                var stages = new StringBuilder(shaders.Length * 16);
                for (int i = 0; i < shaders.Length; i++)
                {
                    if (i > 0)
                        stages.Append('|');

                    stages.Append(shaders[i].Type);
                    bytes += CountUtf8Bytes(shaders[i].ResolvedSource);
                    lines += CountLines(shaders[i].ResolvedSource);
                }

                return new ShaderInputSummary(shaders.Length, bytes, lines, stages.ToString());
            }

            private static double MeasureRenderingWorkerGlCall(
                string callName,
                uint programId,
                uint shaderId,
                ShaderType? shaderType,
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

                bool renderThread = RuntimeEngine.IsRenderThread;
                Debug.Rendering(
                    EOutputVerbosity.Verbose,
                    false,
                    "[ShaderGLCall] call={0} program='<shared-context-worker>' hash=0 programId={1} shaderId={2} shaderType={3} elapsedMs={4:F3} renderThread={5} renderThreadStallMs={6:F3}{7}.",
                    callName,
                    programId,
                    shaderId,
                    shaderType?.ToString() ?? "<program>",
                    elapsedMilliseconds,
                    renderThread,
                    renderThread ? elapsedMilliseconds : 0.0,
                    FormatRenderingDetail(detail));
                return elapsedMilliseconds;
            }

            private static void LogRenderingQueueEvent(
                string eventName,
                uint programId,
                ShaderInputSummary summary,
                string? detail)
            {
                if (!ShouldLogRenderingShaderLinkVerbose())
                    return;

                Debug.Rendering(
                    EOutputVerbosity.Verbose,
                    false,
                    "[ShaderLinkQueue] {0} programId={1} shaderCount={2} shaderTypes={3} sourceBytes={4} sourceLines={5} renderThread={6}{7}.",
                    eventName,
                    programId,
                    summary.ShaderCount,
                    summary.StageList,
                    summary.SourceBytes,
                    summary.SourceLines,
                    RuntimeEngine.IsRenderThread,
                    FormatRenderingDetail(detail));
            }

            private static bool ShouldLogRenderingShaderLinkVerbose()
                => Debug.AllowOutput && RuntimeDebugHostServices.Current.OutputVerbosity >= EOutputVerbosity.Verbose;

            private static string FormatRenderingDetail(string? detail)
                => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" detail='{detail.Replace('\'', '"')}'";

            private static int CountUtf8Bytes(string? source)
                => string.IsNullOrEmpty(source) ? 0 : Encoding.UTF8.GetByteCount(source);

            private static int CountLines(string? source)
            {
                if (string.IsNullOrEmpty(source))
                    return 0;

                int lines = 1;
                for (int i = 0; i < source.Length; i++)
                {
                    if (source[i] == '\n')
                        lines++;
                }
                return lines;
            }

            private static double StopwatchTicksToMilliseconds(long ticks)
                => ticks <= 0L ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;
        }
    }
}
