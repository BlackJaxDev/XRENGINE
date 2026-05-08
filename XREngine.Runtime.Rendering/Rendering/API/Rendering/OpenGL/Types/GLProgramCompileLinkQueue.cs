using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
            private readonly ConcurrentDictionary<uint, CompileResult> _completed = new();
            private int _roundRobinCursor;
            private int _inFlight;
            private long _completedCount;
            private long _failedCount;
            private long _rejectedCount;
            private long _backpressureCount;
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

            public GLProgramCompileLinkQueue(GLSharedContext sharedContext)
                : this(new[] { sharedContext })
            {
            }

            public GLProgramCompileLinkQueue(IReadOnlyList<GLSharedContext> sharedContexts)
            {
                if (sharedContexts is null || sharedContexts.Count == 0)
                    throw new ArgumentException("At least one shared context is required.", nameof(sharedContexts));

                _workers = new GLSharedContext[sharedContexts.Count];
                for (int i = 0; i < sharedContexts.Count; i++)
                    _workers[i] = sharedContexts[i] ?? throw new ArgumentNullException(nameof(sharedContexts));
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

            public bool CanEnqueue => Volatile.Read(ref _inFlight) < MaxInFlightTotal;
            public int InFlightCount => Volatile.Read(ref _inFlight);
            public long CompletedCount => Interlocked.Read(ref _completedCount);
            public long FailedCount => Interlocked.Read(ref _failedCount);
            public long RejectedCount => Interlocked.Read(ref _rejectedCount);
            public long BackpressureCount => Interlocked.Read(ref _backpressureCount);

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
                if (!TryEnqueueCompileAndLink(programId, shaders, out string? rejectReason))
                    throw new InvalidOperationException(rejectReason ?? "Unable to enqueue OpenGL compile/link job.");
            }

            public bool TryEnqueueCompileAndLink(uint programId, ShaderInput[] shaders, out string? rejectReason)
            {
                ShaderInputSummary summary = SummarizeShaderInputs(shaders);
                if (ContainsKnownAsyncLinkHazard(shaders))
                {
                    rejectReason = "known async-link hazard";
                    LogRenderingQueueEvent("REJECTED_HAZARD", programId, summary, rejectReason);
                    Interlocked.Increment(ref _rejectedCount);
                    return false;
                }

                if (!CanEnqueue)
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
                Interlocked.Increment(ref _inFlight);
                worker.Enqueue(gl =>
                {
                    LogRenderingQueueEvent("WORKER_BEGIN", programId, summary, null);

                    // Keep worker-side parallel shader compile enabled by default.
                    // The previous default suppressed it for single-stage programs,
                    // but cold uber fragment compiles then sat pending for minutes
                    // without ever publishing a WORKER_READY result. The render
                    // thread still never links synchronously; this only changes how
                    // the shared context worker lets the driver run its compiler.
                    // Set XRE_SHARED_CONTEXT_HAZARD_DISABLE_PARALLEL=1 to restore
                    // the old workaround for driver builds that specifically need it.
                    bool hazardSuppressParallel = ShouldSuppressParallelCompileForWorkerProgram(shaders);
                    ArbParallelShaderCompile? hazardArbExt = null;
                    if (hazardSuppressParallel && gl.TryGetExtension(out ArbParallelShaderCompile arbForHazard))
                    {
                        hazardArbExt = arbForHazard;
                        try { arbForHazard.MaxShaderCompilerThreads(0u); }
                        catch { hazardArbExt = null; }
                    }

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
                        if (!PollCompletionStatusBlocking(
                            gl,
                            worker,
                            programId,
                            sid,
                            shaderType,
                            isShader: true,
                            phase: "worker=source-compile-completion-poll"))
                        {
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
                        _completed[programId] = new CompileResult(CompileStatus.CompileFailed, errorLog, compileMilliseconds, 0.0);
                        Interlocked.Increment(ref _failedCount);

                        // Restore parallel compile on the worker context if we
                        // suppressed it for the hazardous single-stage shape.
                        if (hazardArbExt is not null)
                        {
                            try { hazardArbExt.MaxShaderCompilerThreads(0xFFFF_FFFFu); }
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
                        hazardSuppressParallel ? "worker=source-link parallel-suppressed" : "worker=source-link");

                    // Poll non-blocking GL_COMPLETION_STATUS_ARB before issuing the
                    // blocking GL_LINK_STATUS query. The blocking query holds a
                    // driver-wide lock on NVIDIA that prevents the main GL context
                    // from making any progress for the entire duration of a cold
                    // link (observed: 109+ seconds on a 387 KB single-stage
                    // separable fragment program), freezing the render thread.
                    if (!PollCompletionStatusBlocking(
                        gl,
                        worker,
                        programId,
                        0,
                        null,
                        isShader: false,
                        phase: "worker=source-link-completion-poll"))
                    {
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

                    // Restore parallel compile on the worker context if we
                    // suppressed it for the hazardous single-stage compile/link.
                    if (hazardArbExt is not null)
                    {
                        try { hazardArbExt.MaxShaderCompilerThreads(0xFFFF_FFFFu); }
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

                    _completed[programId] = new CompileResult(
                        linkStatus != 0 ? CompileStatus.Success : CompileStatus.LinkFailed,
                        linkError,
                        compileMillisecondsCompleted,
                        linkMilliseconds);
                    if (linkStatus != 0)
                        Interlocked.Increment(ref _completedCount);
                    else
                        Interlocked.Increment(ref _failedCount);
                }, $"ProgramSourceCompile:{programId}");
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
                string phase)
            {
                // Initial fast-poll burst — most binary cache hits and warm links
                // complete in microseconds. After the burst, back off to a 1 ms
                // sleep to avoid spinning during long cold compiles.
                const int FastPollIterations = 64;
                int iterations = 0;
                while (true)
                {
                    if (worker.IsDisposeRequested)
                        return false;

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
                        return false;

                    iterations++;
                    if (iterations < FastPollIterations)
                        Thread.Yield();
                    else
                        Thread.Sleep(1);
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
                    Interlocked.Decrement(ref _inFlight);
                    return true;
                }
                return false;
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
            /// True when the opt-in single-stage worker workaround is enabled
            /// for shapes that have historically been sensitive to threaded
            /// driver compilation.
            /// </summary>
            private static bool ShouldSuppressParallelCompileForWorkerProgram(ReadOnlySpan<ShaderInput> shaders)
                => SuppressParallelCompileForSingleStageWorkerPrograms &&
                   IsSingleStageSeparableGraphicsHazard(shaders);

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

                bool renderThread = Engine.IsRenderThread;
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
                    Engine.IsRenderThread,
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
