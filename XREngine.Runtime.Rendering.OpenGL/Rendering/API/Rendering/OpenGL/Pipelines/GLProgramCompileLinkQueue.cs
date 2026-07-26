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
        /// compiler work on background threads, then publishing completed program
        /// handles after the driver reports link completion.
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
            private readonly SemaphoreSlim _programLinkGate;
            private readonly bool _serializeProgramLinkDriverCalls;
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
            private const double WorkerLinkDeferralMilliseconds = 5000.0;
            private const long LargeSourceLinkDeferralThresholdBytes = 64 * 1024;
            private const int DeferredLinkRepollDelayMilliseconds = 50;
            private const double DeferredLinkProgressLogMilliseconds = 15000.0;
            private const double ShaderCompletionPollGlCallSlowLogMilliseconds = 1.0;
            private const double DefaultWorkerCompletionHardAbandonMilliseconds = 180000.0;
            private const string SharedContextAbandonedLinkMarker = "abandoned to keep the async link queue moving";
            private static readonly double WorkerCompletionHardAbandonMilliseconds = ResolveWorkerCompletionHardAbandonMilliseconds();
            private static readonly bool TraceShaderCompletionPollGlCalls = string.Equals(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.TraceShaderCompletionPollGlCalls),
                "1",
                StringComparison.Ordinal);
            private static readonly bool TraceShaderLinkQueueGateEvents = string.Equals(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.TraceShaderLinkQueueGates),
                "1",
                StringComparison.Ordinal);
            private static readonly bool DisableCompletionPollingForSharedContextWorkerPrograms = string.Equals(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.SharedContextDisableCompletionPolling),
                "1",
                StringComparison.Ordinal);
            private static readonly bool SuppressParallelCompileForSingleStageWorkerPrograms =
                string.Equals(
                    Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.SharedContextHazardDisableParallel),
                    "1",
                    StringComparison.Ordinal);
            private static readonly bool DisableProgramLinkSerialization =
                string.Equals(
                    Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.SharedContextDisableLinkSerialization),
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
                _serializeProgramLinkDriverCalls = _workers.Length > 1 && !DisableProgramLinkSerialization;
                _programLinkGate = new SemaphoreSlim(_serializeProgramLinkDriverCalls ? 1 : Math.Max(1, _workers.Length));
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
                string? configured = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.SharedContextLinkTimeoutMs);
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

            public readonly record struct TransformFeedbackLinkInfo(string[]? Varyings, GLEnum BufferMode)
            {
                public bool HasVaryings => Varyings is { Length: > 0 };

                public static TransformFeedbackLinkInfo Empty => new(Array.Empty<string>(), GLEnum.InterleavedAttribs);

                public string ToIdentityString()
                {
                    if (!HasVaryings)
                        return "TransformFeedback=<none>";

                    return "TransformFeedback=" + BufferMode + ":" + string.Join("|", Varyings!);
                }
            }

            public readonly record struct ProgramBinarySnapshot(byte[] Binary, GLEnum Format, uint Length);

            public readonly record struct CompileResult(
                CompileStatus Status,
                string? ErrorLog,
                double CompileMilliseconds,
                double LinkMilliseconds,
                ProgramBinarySnapshot? ProgramBinary = null);

            private enum CompletionPollResult : byte
            {
                Completed,
                Deferred,
                Abandoned,
            }

            private ProgramBinarySnapshot CaptureProgramBinary(GL gl, uint programId, string phase)
            {
                int length = 0;
                MeasureRenderingWorkerGlCall(
                    "glGetProgramiv(GL_PROGRAM_BINARY_LENGTH)",
                    programId,
                    0,
                    null,
                    () => gl.GetProgram(programId, GLEnum.ProgramBinaryLength, out length),
                    phase);
                if (length <= 0)
                    return new ProgramBinarySnapshot(Array.Empty<byte>(), GLEnum.None, 0);

                byte[] binary = GC.AllocateUninitializedArray<byte>(length);
                GLEnum format = GLEnum.None;
                uint capturedLength = 0;
                fixed (byte* ptr = binary)
                {
                    long binaryStartTimestamp = Stopwatch.GetTimestamp();
                    gl.GetProgramBinary(programId, (uint)length, &capturedLength, &format, ptr);
                    double binaryMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - binaryStartTimestamp);
                    if (ShouldLogRenderingShaderLinkVerbose() &&
                        ShouldLogRenderingShaderGlCall("glGetProgramBinary", phase, binaryMilliseconds))
                    {
                        Debug.Rendering(
                            EOutputVerbosity.Verbose,
                            false,
                            "[ShaderGLCall] call={0} program='<shared-context-worker>' hash=0 programId={1} shaderId={2} shaderType={3} elapsedMs={4:F3} renderThread={5} renderThreadStallMs={6:F3}{7}.",
                            "glGetProgramBinary",
                            programId,
                            0,
                            "<program>",
                            binaryMilliseconds,
                            RuntimeEngine.IsRenderThread,
                            RuntimeEngine.IsRenderThread ? binaryMilliseconds : 0.0,
                            FormatRenderingDetail(phase));
                    }
                }

                int actualLength = (int)Math.Min(capturedLength, (uint)binary.Length);
                if (actualLength <= 0 || format == GLEnum.None)
                    return new ProgramBinarySnapshot(Array.Empty<byte>(), GLEnum.None, 0);

                if (actualLength != binary.Length)
                    Array.Resize(ref binary, actualLength);

                return new ProgramBinarySnapshot(binary, format, (uint)actualLength);
            }

            private sealed class DeferredProgramLinkState(
                uint programId,
                ShaderInputSummary summary,
                uint[] shaderIds,
                ShaderType[] shaderTypes,
                double compileMilliseconds,
                long linkStartTimestamp,
                bool setBinaryRetrievableHint)
            {
                public uint ProgramId { get; } = programId;
                public ShaderInputSummary Summary { get; } = summary;
                public uint[] ShaderIds { get; } = shaderIds;
                public ShaderType[] ShaderTypes { get; } = shaderTypes;
                public double CompileMilliseconds { get; } = compileMilliseconds;
                public long LinkStartTimestamp { get; } = linkStartTimestamp;
                public bool SetBinaryRetrievableHint { get; } = setBinaryRetrievableHint;
                public bool FlushIssued { get; set; } = true;
                public long LastProgressLogTimestamp { get; set; } = Stopwatch.GetTimestamp();
            }

            private sealed class DeferredProgramLinkPollRequest(
                GLProgramCompileLinkQueue owner,
                GLSharedContext worker,
                DeferredProgramLinkState state)
            {
                public GLProgramCompileLinkQueue Owner { get; } = owner;
                public GLSharedContext Worker { get; } = worker;
                public DeferredProgramLinkState State { get; } = state;
            }

            /// <summary>
            /// Queues a full compile → attach → link pipeline on the shared context thread.
            /// The program object <paramref name="programId"/> must already be created on the main thread.
            /// Shader objects are created and destroyed on the shared context; only the linked
            /// program survives.
            /// </summary>
            public void EnqueueCompileAndLink(uint programId, ShaderInput[] shaders)
            {
                if (!TryEnqueueCompileAndLink(programId, shaders, EProgramPriority.Main, setBinaryRetrievableHint: false, TransformFeedbackLinkInfo.Empty, out string? rejectReason))
                    throw new InvalidOperationException(rejectReason ?? "Unable to enqueue OpenGL compile/link job.");
            }

            public bool TryEnqueueCompileAndLink(uint programId, ShaderInput[] shaders, out string? rejectReason)
                => TryEnqueueCompileAndLink(programId, shaders, EProgramPriority.Main, out rejectReason);

            /// <summary>
            /// Queues a compile/attach/link job tagged with a priority bucket. Lower-valued
            /// priorities (<see cref="EProgramPriority.Interactive"/>, then <see cref="EProgramPriority.Main"/>)
            /// are linked before higher-valued background work (<see cref="EProgramPriority.Shadow"/>, <see cref="EProgramPriority.VR"/>,
            /// <see cref="EProgramPriority.Compute"/>, <see cref="EProgramPriority.Deferred"/>) inside the shared-context worker.
            /// </summary>
            public bool TryEnqueueCompileAndLink(uint programId, ShaderInput[] shaders, EProgramPriority priority, out string? rejectReason)
                => TryEnqueueCompileAndLink(programId, shaders, priority, setBinaryRetrievableHint: false, TransformFeedbackLinkInfo.Empty, out rejectReason);

            public bool TryEnqueueCompileAndLink(
                uint programId,
                ShaderInput[] shaders,
                EProgramPriority priority,
                bool setBinaryRetrievableHint,
                TransformFeedbackLinkInfo transformFeedback,
                out string? rejectReason)
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
                            if (useWorkerCompletionPolling && PollCompletionStatusBlocking(
                                gl,
                                worker,
                                programId,
                                sid,
                                shaderType,
                                true,
                                "worker=source-compile-completion-poll",
                                Stopwatch.GetTimestamp(),
                                false,
                                out string? compilePollFailure) != CompletionPollResult.Completed)
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
                        ShaderType[] shaderTypes = new ShaderType[shaderIds.Length];
                        for (int i = 0; i < shaderTypes.Length; i++)
                            shaderTypes[i] = shaders[i].Type;

                        // Attach all compiled shaders to the program.
                        for (int i = 0; i < shaderIds.Length; i++)
                        {
                            uint shaderId = shaderIds[i];
                            ShaderType shaderType = shaderTypes[i];
                            MeasureRenderingWorkerGlCall(
                                "glAttachShader",
                                programId,
                                shaderId,
                                shaderType,
                                () => gl.AttachShader(programId, shaderId),
                                "worker=source-link-attach");
                        }

                        bool programLinkGateHeld = false;
                        if (_serializeProgramLinkDriverCalls)
                        {
                            LogRenderingQueueEvent("WORKER_LINK_GATE_WAIT", programId, summary, "serialized shared-context program link/status");
                            _programLinkGate.Wait();
                            programLinkGateHeld = true;
                            LogRenderingQueueEvent("WORKER_LINK_GATE_ENTER", programId, summary, "serialized shared-context program link/status");
                        }

                        try
                        {
                            if (setBinaryRetrievableHint)
                            {
                                MeasureRenderingWorkerGlCall(
                                    "glProgramParameteri(GL_PROGRAM_BINARY_RETRIEVABLE_HINT)",
                                    programId,
                                    0,
                                    null,
                                    () => gl.ProgramParameter(programId, GLEnum.ProgramBinaryRetrievableHint, 1),
                                    "worker=source-link-binary-retrievable-hint value=1");
                            }

                            long linkStartTimestamp = Stopwatch.GetTimestamp();
                            ApplyTransformFeedbackVaryings(gl, programId, transformFeedback, "worker=source-link-transform-feedback-varyings");
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
                            if (useWorkerCompletionPolling)
                            {
                                // Deferring leaves the driver compiling/linking the program while
                                // the worker starts newer jobs. Keep that only for small programs;
                                // large uber variants must drain serially or NVIDIA can stack
                                // several compiler jobs and trip the watchdog during startup.
                                bool allowLinkDeferral = ShouldAllowLinkDeferral(
                                    summary,
                                    workerCompilerThreadsSuppressed,
                                    _serializeProgramLinkDriverCalls);
                                CompletionPollResult linkPollResult = PollCompletionStatusBlocking(
                                    gl,
                                    worker,
                                    programId,
                                    0,
                                    null,
                                    isShader: false,
                                    phase: "worker=source-link-completion-poll",
                                    operationStartTimestamp: linkStartTimestamp,
                                    allowDeferred: allowLinkDeferral,
                                    out string? linkPollFailure);

                                if (linkPollResult == CompletionPollResult.Deferred)
                                {
                                    LogRenderingQueueEvent(
                                        "WORKER_LINK_DEFERRED",
                                        programId,
                                        summary,
                                        $"compileMs={compileMillisecondsCompleted:F2} linkMs={StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - linkStartTimestamp):F2}");
                                    ScheduleDeferredProgramLinkCompletion(
                                        worker,
                                        new DeferredProgramLinkState(
                                            programId,
                                            summary,
                                            shaderIds,
                                            shaderTypes,
                                            compileMillisecondsCompleted,
                                            linkStartTimestamp,
                                            setBinaryRetrievableHint));
                                    return;
                                }

                                if (linkPollResult == CompletionPollResult.Abandoned)
                                {
                                    double linkMillisecondsAbandoned = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - linkStartTimestamp);
                                    string abandonedLinkError = linkPollFailure ?? $"Shared-context program link completion poll aborted; {SharedContextAbandonedLinkMarker}.";
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

                            ProgramBinarySnapshot? programBinary = null;
                            if (linkStatus != 0 && setBinaryRetrievableHint)
                                programBinary = CaptureProgramBinary(gl, programId, "worker=source-link-binary-cache-capture");

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
                                ShaderType shaderType = shaderTypes[i];
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

                            // Completion and link-status queries above prove the link is done.
                            // Flush submits shared-context object state without forcing a
                            // driver-wide idle on the render context.
                            MeasureRenderingWorkerGlCall(
                                "glFlush",
                                programId,
                                0,
                                null,
                                () => gl.Flush(),
                                "worker=source-link-handoff-flush");

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
                                    linkMilliseconds,
                                    programBinary));
                            if (linkStatus != 0)
                                Interlocked.Increment(ref _completedCount);
                            else
                                Interlocked.Increment(ref _failedCount);
                        }
                        finally
                        {
                            if (programLinkGateHeld)
                            {
                                _programLinkGate.Release();
                                LogRenderingQueueEvent("WORKER_LINK_GATE_EXIT", programId, summary, "serialized shared-context program link/status");
                            }
                        }
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

            private static void ApplyTransformFeedbackVaryings(GL gl, uint programId, TransformFeedbackLinkInfo transformFeedback, string phase)
            {
                if (!transformFeedback.HasVaryings)
                    return;

                string[] varyings = transformFeedback.Varyings!;
                MeasureRenderingWorkerGlCall(
                    "glTransformFeedbackVaryings",
                    programId,
                    0,
                    null,
                    () => gl.TransformFeedbackVaryings(programId, (uint)varyings.Length, varyings, transformFeedback.BufferMode),
                    phase);
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
            private CompletionPollResult PollCompletionStatusBlocking(
                GL gl,
                GLSharedContext worker,
                uint programId,
                uint shaderId,
                ShaderType? shaderType,
                bool isShader,
                string phase,
                long operationStartTimestamp,
                bool allowDeferred,
                out string? failureReason)
            {
                failureReason = null;
                // Initial fast-poll burst — most binary cache hits and warm links
                // complete in microseconds. After the burst, back off to a 1 ms
                // sleep to avoid spinning during long cold compiles.
                int iterations = 0;
                bool flushIssued = false;
                string operation = isShader ? "shader compile" : "program link";
                while (true)
                {
                    if (worker.IsDisposeRequested)
                    {
                        failureReason = $"Shared-context {operation} completion poll aborted because worker disposal was requested.";
                        return CompletionPollResult.Abandoned;
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
                        return CompletionPollResult.Completed;

                    if (worker.IsDisposeRequested)
                    {
                        failureReason = $"Shared-context {operation} completion poll aborted because worker disposal was requested.";
                        return CompletionPollResult.Abandoned;
                    }

                    double elapsedMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - operationStartTimestamp);
                    if (!flushIssued && elapsedMilliseconds >= WorkerCompletionStuckFlushMilliseconds)
                    {
                        flushIssued = true;
                        Debug.OpenGL(
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

                    if (allowDeferred && elapsedMilliseconds >= WorkerLinkDeferralMilliseconds)
                    {
                        Debug.OpenGL(
                            $"[ShaderLinkQueue] Worker {operation} programId={programId} exceeded {WorkerLinkDeferralMilliseconds / 1000.0:F2}s; " +
                            "deferring completion polling at background priority so faster shader programs can link first.");
                        return CompletionPollResult.Deferred;
                    }

                    if (elapsedMilliseconds >= WorkerCompletionHardAbandonMilliseconds)
                    {
                        failureReason =
                            $"Shared-context {operation} did not report completion after {elapsedMilliseconds / 1000.0:F2}s; {SharedContextAbandonedLinkMarker}.";
                        Debug.OpenGLWarning(
                            $"[ShaderLinkQueue] Worker {operation} programId={programId} stuck with COMPLETION_STATUS=false " +
                            $"after {elapsedMilliseconds / 1000.0:F2}s; publishing a failed async result without querying final status.");
                        return CompletionPollResult.Abandoned;
                    }

                    iterations++;
                    if (iterations < WorkerCompletionFastPollIterations)
                        Thread.Yield();
                    else
                        Thread.Sleep(1);
                }
            }

            private void ScheduleDeferredProgramLinkCompletion(GLSharedContext worker, DeferredProgramLinkState state)
            {
                if (worker.IsDisposeRequested)
                {
                    PublishDeferredProgramLinkFailure(
                        state,
                        "Shared-context program link completion poll aborted because worker disposal was requested.");
                    return;
                }

                ThreadPool.QueueUserWorkItem(static queuedState =>
                {
                    var request = (DeferredProgramLinkPollRequest)queuedState!;
                    Thread.Sleep(DeferredLinkRepollDelayMilliseconds);
                    request.Owner.EnqueueDeferredProgramLinkCompletion(request.Worker, request.State);
                }, new DeferredProgramLinkPollRequest(this, worker, state));
            }

            private void EnqueueDeferredProgramLinkCompletion(GLSharedContext worker, DeferredProgramLinkState state)
            {
                if (worker.IsDisposeRequested)
                {
                    PublishDeferredProgramLinkFailure(
                        state,
                        "Shared-context program link completion poll aborted because worker disposal was requested.");
                    return;
                }

                worker.Enqueue(
                    gl => ContinueDeferredProgramLinkCompletion(gl, worker, state),
                    $"ProgramDeferredLinkPoll:{state.ProgramId}",
                    EProgramPriority.Deferred);
            }

            private void ContinueDeferredProgramLinkCompletion(GL gl, GLSharedContext worker, DeferredProgramLinkState state)
            {
                bool programLinkGateHeld = false;
                try
                {
                    if (worker.IsDisposeRequested)
                    {
                        PublishDeferredProgramLinkFailure(
                            state,
                            "Shared-context program link completion poll aborted because worker disposal was requested.");
                        return;
                    }

                    if (_serializeProgramLinkDriverCalls)
                    {
                        LogRenderingQueueEvent("WORKER_DEFERRED_LINK_GATE_WAIT", state.ProgramId, state.Summary, "serialized shared-context program link/status");
                        _programLinkGate.Wait();
                        programLinkGateHeld = true;
                        LogRenderingQueueEvent("WORKER_DEFERRED_LINK_GATE_ENTER", state.ProgramId, state.Summary, "serialized shared-context program link/status");
                    }

                    int complete = 0;
                    MeasureRenderingWorkerGlCall(
                        "glGetProgramiv(GL_COMPLETION_STATUS)",
                        state.ProgramId,
                        0,
                        null,
                        () => gl.GetProgram(state.ProgramId, (GLEnum)GLShader.GL_COMPLETION_STATUS_ARB, out complete),
                        "worker=deferred-source-link-completion-poll");

                    if (complete == 0)
                    {
                        if (worker.IsDisposeRequested)
                        {
                            PublishDeferredProgramLinkFailure(
                                state,
                                "Shared-context program link completion poll aborted because worker disposal was requested.");
                            return;
                        }

                        double elapsedMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - state.LinkStartTimestamp);
                        if (!state.FlushIssued && elapsedMilliseconds >= WorkerCompletionStuckFlushMilliseconds)
                        {
                            state.FlushIssued = true;
                            Debug.OpenGL(
                                $"[ShaderLinkQueue] Deferred program link programId={state.ProgramId} still reports COMPLETION_STATUS=false " +
                                $"after {elapsedMilliseconds / 1000.0:F2}s; issuing glFlush() and continuing background poll.");
                            MeasureRenderingWorkerGlCall(
                                "glFlush",
                                state.ProgramId,
                                0,
                                null,
                                () => gl.Flush(),
                                "worker=deferred-source-link-stuck-nudge");
                        }

                        if (elapsedMilliseconds >= WorkerCompletionHardAbandonMilliseconds)
                        {
                            string failureReason =
                                $"Shared-context program link did not report completion after {elapsedMilliseconds / 1000.0:F2}s; {SharedContextAbandonedLinkMarker}.";
                            Debug.OpenGLWarning(
                                $"[ShaderLinkQueue] Deferred program link programId={state.ProgramId} stuck with COMPLETION_STATUS=false " +
                                $"after {elapsedMilliseconds / 1000.0:F2}s; publishing a failed async result without querying final status.");
                            PublishDeferredProgramLinkFailure(state, failureReason);
                            return;
                        }

                        long now = Stopwatch.GetTimestamp();
                        if (StopwatchTicksToMilliseconds(now - state.LastProgressLogTimestamp) >= DeferredLinkProgressLogMilliseconds)
                        {
                            state.LastProgressLogTimestamp = now;
                            Debug.OpenGL(
                                $"[ShaderLinkQueue] Deferred program link programId={state.ProgramId} still pending after {elapsedMilliseconds / 1000.0:F2}s; " +
                                "leaving it at background priority.");
                        }

                        ScheduleDeferredProgramLinkCompletion(worker, state);
                        return;
                    }

                    CompleteDeferredProgramLink(gl, state);
                }
                catch (Exception ex)
                {
                    string error = $"Deferred shared-context program link completion threw: {ex.GetType().Name}: {ex.Message}";
                    LogRenderingQueueEvent("WORKER_DEFERRED_LINK_EXCEPTION", state.ProgramId, state.Summary, error);
                    Debug.OpenGLWarning($"[ShaderLinkQueue] {error}");
                    PublishCompletedResult(
                        state.ProgramId,
                        new CompileResult(
                            CompileStatus.LinkFailed,
                            error,
                            state.CompileMilliseconds,
                            StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - state.LinkStartTimestamp)));
                    Interlocked.Increment(ref _failedCount);
                }
                finally
                {
                    if (programLinkGateHeld)
                    {
                        _programLinkGate.Release();
                        LogRenderingQueueEvent("WORKER_DEFERRED_LINK_GATE_EXIT", state.ProgramId, state.Summary, "serialized shared-context program link/status");
                    }
                }
            }

            private void CompleteDeferredProgramLink(GL gl, DeferredProgramLinkState state)
            {
                int linkStatus = 0;
                MeasureRenderingWorkerGlCall(
                    "glGetProgramiv(GL_LINK_STATUS)",
                    state.ProgramId,
                    0,
                    null,
                    () => gl.GetProgram(state.ProgramId, ProgramPropertyARB.LinkStatus, out linkStatus),
                    "worker=deferred-source-link-status");

                string? linkError = null;
                if (linkStatus == 0)
                {
                    MeasureRenderingWorkerGlCall(
                        "glGetProgramInfoLog",
                        state.ProgramId,
                        0,
                        null,
                        () => gl.GetProgramInfoLog(state.ProgramId, out linkError),
                        "worker=deferred-source-link-log");
                }

                ProgramBinarySnapshot? programBinary = null;
                if (linkStatus != 0 && state.SetBinaryRetrievableHint)
                    programBinary = CaptureProgramBinary(gl, state.ProgramId, "worker=deferred-source-link-binary-cache-capture");

                for (int i = 0; i < state.ShaderIds.Length; i++)
                {
                    uint shaderId = state.ShaderIds[i];
                    ShaderType shaderType = state.ShaderTypes[i];
                    MeasureRenderingWorkerGlCall(
                        "glDetachShader",
                        state.ProgramId,
                        shaderId,
                        shaderType,
                        () => gl.DetachShader(state.ProgramId, shaderId),
                        "worker=deferred-source-link-detach");
                    MeasureRenderingWorkerGlCall(
                        "glDeleteShader",
                        state.ProgramId,
                        shaderId,
                        shaderType,
                        () => gl.DeleteShader(shaderId),
                        "worker=deferred-source-link-delete-shader");
                }

                // Completion and link-status queries above prove the link is done.
                // Flush submits shared-context object state without forcing a
                // driver-wide idle on the render context.
                MeasureRenderingWorkerGlCall(
                    "glFlush",
                    state.ProgramId,
                    0,
                    null,
                    () => gl.Flush(),
                    "worker=deferred-source-link-handoff-flush");

                double linkMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - state.LinkStartTimestamp);
                LogRenderingQueueEvent(
                    linkStatus != 0 ? "WORKER_DEFERRED_READY" : "WORKER_DEFERRED_LINK_FAILED",
                    state.ProgramId,
                    state.Summary,
                    $"compileMs={state.CompileMilliseconds:F2} linkMs={linkMilliseconds:F2} error={linkError ?? "<none>"}");
                Debug.OpenGL(
                    $"[ShaderLinkQueue] Deferred program link {(linkStatus != 0 ? "completed" : "failed")} programId={state.ProgramId} " +
                    $"compileMs={state.CompileMilliseconds:F2} linkMs={linkMilliseconds:F2}" +
                    (string.IsNullOrWhiteSpace(linkError) ? "." : $" error='{linkError}'."));

                PublishCompletedResult(
                    state.ProgramId,
                    new CompileResult(
                        linkStatus != 0 ? CompileStatus.Success : CompileStatus.LinkFailed,
                        linkError,
                        state.CompileMilliseconds,
                        linkMilliseconds,
                        programBinary));
                if (linkStatus != 0)
                    Interlocked.Increment(ref _completedCount);
                else
                    Interlocked.Increment(ref _failedCount);
            }

            private void PublishDeferredProgramLinkFailure(DeferredProgramLinkState state, string failureReason)
            {
                double linkMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - state.LinkStartTimestamp);
                LogRenderingQueueEvent(
                    "WORKER_DEFERRED_LINK_ABANDONED",
                    state.ProgramId,
                    state.Summary,
                    $"compileMs={state.CompileMilliseconds:F2} linkMs={linkMilliseconds:F2} error={failureReason}");
                PublishCompletedResult(
                    state.ProgramId,
                    new CompileResult(
                        CompileStatus.LinkFailed,
                        failureReason,
                        state.CompileMilliseconds,
                        linkMilliseconds));
                Interlocked.Increment(ref _failedCount);
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
                // entire compile+link block, then restored). The worker waits
                // for the link to complete before publishing the result, so by
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

            private static bool ShouldAllowLinkDeferral(
                ShaderInputSummary summary,
                bool workerCompilerThreadsSuppressed,
                bool serializedProgramLinkDriverCalls)
                => !workerCompilerThreadsSuppressed &&
                   !serializedProgramLinkDriverCalls &&
                   summary.SourceBytes < LargeSourceLinkDeferralThresholdBytes;

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
                if (!ShouldLogRenderingShaderGlCall(callName, detail, elapsedMilliseconds))
                    return elapsedMilliseconds;

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
                if (!TraceShaderLinkQueueGateEvents && eventName.Contains("GATE", StringComparison.OrdinalIgnoreCase))
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

            private static bool ShouldLogRenderingShaderGlCall(string callName, string? detail, double elapsedMilliseconds)
            {
                if (!IsShaderCompletionPollGlCall(callName, detail))
                    return true;

                return TraceShaderCompletionPollGlCalls ||
                       elapsedMilliseconds >= ShaderCompletionPollGlCallSlowLogMilliseconds;
            }

            private static bool IsShaderCompletionPollGlCall(string callName, string? detail)
                => callName.Contains("GL_COMPLETION_STATUS", StringComparison.OrdinalIgnoreCase) ||
                   (detail?.Contains("completion-poll", StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (detail?.Contains("deferred-cleanup", StringComparison.OrdinalIgnoreCase) ?? false);

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
