using XREngine.Extensions;
using Silk.NET.OpenGL;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using XREngine;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLRenderProgram
        {
            public void PrepareLinkData()
            {
                if (_linkDataPrepared || IsLinked || _shaderCache.IsEmpty)
                    return;

                long preparationStartTimestamp = Stopwatch.GetTimestamp();
                ulong hash;
                string? cacheKey = null;
                bool isCached = false;
                BinaryProgram binProg = default;
                GLProgramCompileLinkQueue.ShaderInput[]? compileInputs = null;
                using (RuntimeEngine.Profiler.Start("GLRenderProgram.Link.CacheLookup", ProfilerScopeKind.OneOffInvoke))
                {
                    using (RuntimeEngine.Profiler.Start("GLRenderProgram.Link.CalcHash", ProfilerScopeKind.OneOffInvoke))
                        hash = CalcShaderSourceHash();

                    bool bypassBinaryCache = ShouldBypassBinaryCacheForLiveUberVariant();
                    if (!bypassBinaryCache && RuntimeEngine.Rendering.Settings.AllowBinaryProgramCaching)
                    {
                        cacheKey = BuildBinaryCacheKey(hash);
                        if (!IsAsyncBinaryUploadTimedOutCacheKey(cacheKey))
                        {
                            using (RuntimeEngine.Profiler.Start("GLRenderProgram.Link.BinaryCacheLookup", ProfilerScopeKind.OneOffInvoke))
                                isCached = BinaryCache?.TryGetValue(cacheKey, out binProg) ?? false;
                        }
                    }
                }

                // Phase 2: skip full source materialization on the warm binary-cache fast
                // path. Binary cache hits do not need shader source text - the link path
                // and verbose telemetry both fall back to walking _shaderCache when
                // _preparedCompileInputs is null. Binary cache misses still get inputs
                // either here (below) or via the lazy fallback at the source-build site.
                // Phase 3: skip source materialization for hashes that have already failed
                // in this session - we will short-circuit the link with a rate-limited
                // SOURCE_FAILED_SKIPPED log instead of attempting another source build.
                if (!isCached && !Failed.ContainsKey(hash))
                    compileInputs = PrepareCompileInputs();

                _preparedHash = hash;
                _preparedCacheKey = cacheKey;
                _preparedIsCached = isCached;
                _preparedBinProg = binProg;
                _preparedCompileInputs = compileInputs;
                _linkPreparationFailure = null;
                _linkDataPrepared = true;

                double preparationMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - preparationStartTimestamp);
                if (preparationMilliseconds >= SlowLinkPreparationWarningMilliseconds)
                {
                    Debug.OpenGL($"[ShaderCache] Slow link preparation: hash={hash}, shaderCount={_shaderCache.Count}, cached={isCached}, compileInputs={compileInputs?.Length ?? 0}, elapsedMs={preparationMilliseconds:F2}.");
                    ShaderProgramLifecycleDiagnostics.RecordSlowLinkPreparation();
                }
            }

            public void BeginPrepareLinkData(bool registerPendingProgram = false)
            {
                if (_linkDataPrepared || IsLinked || _shaderCache.IsEmpty)
                {
                    if (registerPendingProgram && _linkDataPrepared && !IsLinked)
                        RegisterPendingAsyncProgram();
                    return;
                }

                if (registerPendingProgram)
                    RegisterPendingAsyncProgram();

                int generation = Volatile.Read(ref _linkPreparationGeneration);
                if (Volatile.Read(ref _linkPreparationPendingGeneration) == generation)
                    return;

                if (!RuntimeEngine.IsRenderThread)
                {
                    try
                    {
                        PrepareLinkData();
                    }
                    catch (Exception ex)
                    {
                        _linkPreparationFailure = ex;
                    }
                    return;
                }

                if (Interlocked.CompareExchange(ref _linkPreparationPendingGeneration, generation, -1) != -1)
                    return;

                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        PrepareLinkData();
                    }
                    catch (Exception ex)
                    {
                        _linkPreparationFailure = ex;
                    }
                    finally
                    {
                        if (Volatile.Read(ref _linkPreparationPendingGeneration) == generation)
                            Interlocked.CompareExchange(ref _linkPreparationPendingGeneration, -1, generation);
                    }
                });
            }

            private GLProgramCompileLinkQueue.ShaderInput[]? PrepareCompileInputs()
            {
                var shaderData = Data.Shaders;
                if (shaderData.Count == 0)
                    return [];

                var inputs = new GLProgramCompileLinkQueue.ShaderInput[shaderData.Count];
                for (int index = 0; index < shaderData.Count; index++)
                {
                    XRShader shaderDataItem = shaderData[index];
                    if (!_shaderCache.TryGetValue(shaderDataItem, out GLShader? shader) || shader is null)
                        return null;

                    shader.PrepareCompileVariant(Data.Separable);
                    shader.PrepareResolvedSourceVariant(Data.Separable);

                    string? resolved = shader.ResolveFullSource();
                    if (string.IsNullOrWhiteSpace(resolved))
                        return null;

                    inputs[index] = new GLProgramCompileLinkQueue.ShaderInput(
                        resolved,
                        GLProgramCompileLinkQueue.ToGLShaderType(shaderDataItem.Type));
                }

                return inputs;
            }

            private GLProgramCompileLinkQueue.TransformFeedbackLinkInfo BuildTransformFeedbackLinkInfo()
            {
                var feedbacks = Data.TransformFeedbacks;
                if (feedbacks.Count == 0)
                    return GLProgramCompileLinkQueue.TransformFeedbackLinkInfo.Empty;

                List<XRTransformFeedback> captures = [];
                foreach (XRTransformFeedback feedback in feedbacks)
                {
                    if (feedback.Names is { Length: > 0 })
                        captures.Add(feedback);
                }

                if (captures.Count == 0)
                    return GLProgramCompileLinkQueue.TransformFeedbackLinkInfo.Empty;

                captures.Sort(static (left, right) => left.BindingLocation.CompareTo(right.BindingLocation));

                EFeedbackType type = captures[0].Type;
                for (int i = 1; i < captures.Count; i++)
                {
                    if (captures[i].Type != type)
                    {
                        throw new InvalidOperationException(
                            "OpenGL transform feedback programs cannot mix PerVertex and OutValues captures in one link. " +
                            "Use one mode per XRRenderProgram.");
                    }
                }

                return type switch
                {
                    EFeedbackType.PerVertex => BuildInterleavedTransformFeedbackLinkInfo(captures),
                    EFeedbackType.OutValues => BuildSeparateTransformFeedbackLinkInfo(captures),
                    _ => GLProgramCompileLinkQueue.TransformFeedbackLinkInfo.Empty,
                };
            }

            private static GLProgramCompileLinkQueue.TransformFeedbackLinkInfo BuildInterleavedTransformFeedbackLinkInfo(List<XRTransformFeedback> captures)
            {
                List<string> varyings = [];
                HashSet<uint> seenBindings = [];
                uint currentBinding = 0;
                foreach (XRTransformFeedback feedback in captures)
                {
                    if (!seenBindings.Add(feedback.BindingLocation))
                    {
                        throw new InvalidOperationException(
                            $"OpenGL PerVertex transform feedback binding {feedback.BindingLocation} is used by more than one XRTransformFeedback object. " +
                            "Use one XRTransformFeedback per binding.");
                    }

                    while (currentBinding < feedback.BindingLocation)
                    {
                        varyings.Add("gl_NextBuffer");
                        currentBinding++;
                    }

                    if (currentBinding != feedback.BindingLocation)
                    {
                        throw new InvalidOperationException(
                            "OpenGL PerVertex transform feedback captures must be ordered by non-decreasing BindingLocation.");
                    }

                    foreach (string name in feedback.Names)
                    {
                        if (!string.IsNullOrWhiteSpace(name))
                            varyings.Add(name);
                    }
                }

                return varyings.Count == 0
                    ? GLProgramCompileLinkQueue.TransformFeedbackLinkInfo.Empty
                    : new GLProgramCompileLinkQueue.TransformFeedbackLinkInfo([.. varyings], GLEnum.InterleavedAttribs);
            }

            private static GLProgramCompileLinkQueue.TransformFeedbackLinkInfo BuildSeparateTransformFeedbackLinkInfo(List<XRTransformFeedback> captures)
            {
                List<string> varyings = [];
                uint expectedBinding = 0;
                foreach (XRTransformFeedback feedback in captures)
                {
                    if (feedback.Names.Length != 1)
                    {
                        throw new InvalidOperationException(
                            "OpenGL OutValues transform feedback captures require exactly one varying name per XRTransformFeedback, " +
                            "because GL_SEPARATE_ATTRIBS maps varyings to sequential buffer bindings.");
                    }

                    if (feedback.BindingLocation != expectedBinding)
                    {
                        throw new InvalidOperationException(
                            "OpenGL OutValues transform feedback captures must use dense BindingLocation values starting at zero.");
                    }

                    string name = feedback.Names[0];
                    if (!string.IsNullOrWhiteSpace(name))
                        varyings.Add(name);

                    expectedBinding++;
                }

                return varyings.Count == 0
                    ? GLProgramCompileLinkQueue.TransformFeedbackLinkInfo.Empty
                    : new GLProgramCompileLinkQueue.TransformFeedbackLinkInfo([.. varyings], GLEnum.SeparateAttribs);
            }

            private void ApplyTransformFeedbackVaryings(uint programId, GLProgramCompileLinkQueue.TransformFeedbackLinkInfo transformFeedback, string phase)
            {
                if (!transformFeedback.HasVaryings)
                    return;

                string[] varyings = transformFeedback.Varyings!;
                MeasureRenderingProgramGlCall(
                    "glTransformFeedbackVaryings",
                    programId,
                    () => Api.TransformFeedbackVaryings(programId, (uint)varyings.Length, varyings, transformFeedback.BufferMode),
                    phase);
            }

            private static bool ShouldPreferSharedContextForLargeSource(GLProgramCompileLinkQueue.ShaderInput[]? inputs)
            {
                if (inputs is not { Length: > 0 })
                    return false;

                long sourceBytes = 0;
                bool hasGraphicsStage = false;
                for (int index = 0; index < inputs.Length; index++)
                {
                    ShaderType type = inputs[index].Type;
                    if (type != ShaderType.ComputeShader)
                        hasGraphicsStage = true;

                    sourceBytes += CountUtf8Bytes(inputs[index].ResolvedSource);
                    if (hasGraphicsStage && sourceBytes >= LargeSourceSharedContextPreferenceThresholdBytes)
                        return true;
                }

                return false;
            }

            private static bool ShouldPreferSharedContextForLargeSource(ulong hash, GLProgramCompileLinkQueue.ShaderInput[]? inputs)
                => (hash == 0 || !SharedContextLargeSourceTimeouts.ContainsKey(hash)) &&
                   ShouldPreferSharedContextForLargeSource(inputs);

            private static bool IsSharedContextAbandonedLink(string? errorLog)
                => !string.IsNullOrWhiteSpace(errorLog) &&
                   errorLog.Contains(SharedContextAbandonedLinkMarker, StringComparison.Ordinal);

            private bool IsLinkPreparationPending
                => Volatile.Read(ref _linkPreparationPendingGeneration) == Volatile.Read(ref _linkPreparationGeneration);

            private bool ShouldDeferLinkPreparationOnRenderThread()
            {
                if (!RuntimeEngine.IsRenderThread || _linkDataPrepared || IsLinkPreparationPending || _shaderCache.IsEmpty)
                    return false;

                if (Renderer.ProgramCompileLinkQueue is { IsAvailable: true })
                    return true;

                if (Renderer.UseDriverParallelShaderCompile)
                    return true;

                return RuntimeEngine.Rendering.Settings.AsyncProgramBinaryUpload &&
                       Renderer.ProgramBinaryUploadQueue is { IsAvailable: true, IsDisabledAfterFailures: false };
            }

            private void InvalidatePreparedLinkData()
            {
                Interlocked.Increment(ref _linkPreparationGeneration);
                Volatile.Write(ref _linkPreparationPendingGeneration, -1);
                _linkPreparationFailure = null;
                _preparedCompileInputs = null;
                _preparedHash = 0;
                _preparedCacheKey = null;
                _preparedIsCached = false;
                _preparedBinProg = default;
                _linkDataPrepared = false;
            }

            //private static object HashLock = new();
            private static readonly ConcurrentDictionary<ulong, byte> Failed = new();
            private static readonly ConcurrentDictionary<ulong, byte> SharedContextLargeSourceTimeouts = new();
            private static readonly ConcurrentDictionary<ulong, byte> SynchronousSourceRetryHashes = new();
            private static readonly ConcurrentDictionary<ulong, byte> DriverParallelSourceTimeouts = new();
            private static readonly ConcurrentDictionary<string, byte> AsyncBinaryUploadTimeoutCacheKeys = new(StringComparer.Ordinal);
            private const string SharedContextAbandonedLinkMarker = "abandoned to keep the async link queue moving";
            private static readonly bool AllowSynchronousSourceRetryAfterAsyncTimeout = string.Equals(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.AllowSyncSourceRetryAfterAsyncTimeout),
                "1",
                StringComparison.Ordinal);
            private static bool IsAsyncBinaryUploadTimedOutCacheKey(string? cacheKey)
                => !string.IsNullOrWhiteSpace(cacheKey) && AsyncBinaryUploadTimeoutCacheKeys.ContainsKey(cacheKey);

            // Phase 3: per-hash diagnostic record captured the first time a hash fails,
            // and used to rate-limit follow-up SOURCE_FAILED_SKIPPED logs and supply
            // compact metadata without paying for a full ShaderProgramSourceSummary on
            // every retry.
            private readonly record struct FailedHashRecord(
                long FirstFailureTicks,
                long LastLogTicks,
                int SkipCount,
                string? Reason,
                string? Label,
                string? StageList,
                bool Separable);

            private static readonly ConcurrentDictionary<ulong, FailedHashRecord> FailedHashDiagnostics = new();
            private const double FailedHashSkipLogThrottleSeconds = 10.0;

            /// <summary>
            /// Tracks hashes currently being compiled from source so that duplicate
            /// GLRenderProgram instances with the same shader source can defer instead
            /// of redundantly compiling. Cleared when compilation succeeds (binary cached)
            /// or fails (added to <see cref="Failed"/>).
            /// </summary>
            private static readonly ConcurrentDictionary<ulong, byte> InFlightCompilations = new();
            private static readonly ConcurrentDictionary<GLRenderProgram, byte> PendingAsyncPrograms = new();

            /// <summary>
            /// True while one or more programs are mid-async-build. Used by the shadow
            /// pass to skip mesh draws that would otherwise force the render thread to
            /// synchronously start a new link (e.g. SeparatedVertex depth program) and
            /// implicitly serialize against the driver's in-flight async links.
            /// </summary>
            internal static bool HasPendingAsyncPrograms => !PendingAsyncPrograms.IsEmpty;
            private static readonly ConcurrentQueue<DeferredAsyncLinkCleanup> DeferredAsyncLinkCleanups = new();
            private static readonly ConcurrentQueue<DeferredProgramHandleDelete> DeferredProgramHandleDeletes = new();

            private readonly record struct DeferredProgramHandleDelete(OpenGLRenderer Renderer, uint ProgramId, ulong EarliestFrameId);

            private sealed class DeferredAsyncLinkCleanup(OpenGLRenderer renderer, uint programId, uint[] shaderIds)
            {
                public bool TryProcess()
                {
                    if (renderer.ShouldOrphanGLHandlesForShutdown)
                        return true;

                    GL api = renderer.Api;

                    if (programId != 0 && api.IsProgram(programId))
                    {
                        int complete = 0;
                        MeasureRenderingProgramGlCallStatic(
                            "glGetProgramiv(GL_COMPLETION_STATUS)",
                            programId,
                            () => api.GetProgram(programId, (GLEnum)GLShader.GL_COMPLETION_STATUS_ARB, out complete),
                            "phase=deferred-cleanup");
                        if (complete == 0)
                            return false;

                        DetachShaders(api, programId, shaderIds);
                        MeasureRenderingProgramGlCallStatic(
                            "glDeleteProgram",
                            programId,
                            () => api.DeleteProgram(programId),
                            "phase=deferred-cleanup");
                    }

                    DeleteShaders(api, shaderIds);
                    return true;
                }
            }

            private bool IsRegisteredPendingAsyncProgram
                => PendingAsyncPrograms.ContainsKey(this);

            private bool HasQueuedOrRunningAsyncWork
                => IsLinkPreparationPending ||
                   _replacementProgramPending ||
                   _asyncBinaryUploadQueueWaitPending ||
                   _asyncBinaryUploadPending ||
                   _asyncCompileLinkPending ||
                   _asyncCompileLinkQueueWaitPending ||
                   _asyncCompileDuplicateHashWaitPending ||
                   _asyncLinkPhase != EAsyncLinkPhase.Idle;

            private bool HasPendingAsyncWork
                => HasQueuedOrRunningAsyncWork ||
                   (_linkDataPrepared && IsRegisteredPendingAsyncProgram);

            private bool HasCompletedBuildPendingState
                => _asyncBinaryUploadQueueWaitPending ||
                   _asyncBinaryUploadPending ||
                   _asyncCompileLinkPending ||
                   _asyncCompileLinkQueueWaitPending ||
                   _asyncCompileDuplicateHashWaitPending ||
                   _asyncPendingStartTimestamp != 0;

            /// <summary>
            /// True while this program has no usable linked build and is still being built asynchronously.
            /// Linked programs may keep drawing their current build while a replacement build is in flight.
            /// </summary>
            internal bool IsAsyncBuildPending => !IsLinked && HasPendingAsyncWork;

            private void RegisterPendingAsyncProgram()
            {
                if (_asyncPendingStartTimestamp == 0)
                    _asyncPendingStartTimestamp = Stopwatch.GetTimestamp();

                PendingAsyncPrograms[this] = 0;
            }

            private void UnregisterPendingAsyncProgram()
            {
                PendingAsyncPrograms.TryRemove(this, out _);
                ResetAsyncPendingDiagnostics();
            }

            private void ClearCompletedBuildPendingState()
            {
                _asyncBinaryUploadQueueWaitPending = false;
                _asyncBinaryUploadPending = false;
                _asyncCompileLinkPending = false;
                _asyncCompileLinkQueueWaitPending = false;
                _asyncCompileDuplicateHashWaitPending = false;
                UnregisterPendingAsyncProgram();
            }

            internal static void CancelPendingAsyncProgramsForShaderPipelineModeChange(bool allowShaderPipelines)
            {
                if (PendingAsyncPrograms.IsEmpty)
                    return;

                if (!RuntimeEngine.IsRenderThread)
                {
                    RuntimeEngine.EnqueueMainThreadTask(
                        () => CancelPendingAsyncProgramsForShaderPipelineModeChange(allowShaderPipelines));
                    return;
                }

                int drained = DrainReadyPendingAsyncProgramResults();
                GLRenderProgram[] snapshot = [.. PendingAsyncPrograms.Keys];
                int cancelled = 0;

                foreach (GLRenderProgram program in snapshot)
                {
                    if (!program.HasPendingAsyncWork)
                    {
                        program.UnregisterPendingAsyncProgram();
                        continue;
                    }

                    program.CancelPendingAsyncBuildForShaderPipelineModeChange();
                    cancelled++;
                }

                if (drained == 0 && cancelled == 0)
                    return;

                Debug.OpenGL(
                    $"[ShaderPipelineToggle] AllowShaderPipelines={allowShaderPipelines}; " +
                    $"drainedReadyPrograms={drained} cancelledPendingPrograms={cancelled}.");
            }

            private static int DrainReadyPendingAsyncProgramResults()
            {
                GLRenderProgram[] snapshot = [.. PendingAsyncPrograms.Keys];
                int drained = 0;

                foreach (GLRenderProgram program in snapshot)
                {
                    if (!program.HasPendingAsyncWork)
                    {
                        program.UnregisterPendingAsyncProgram();
                        continue;
                    }

                    if (!program.CanCompleteFromSharedBinaryCache() &&
                        !program.CanCompleteFromSharedContextCompileQueue())
                    {
                        continue;
                    }

                    program.Link(nonBlocking: true);
                    if (!program.HasPendingAsyncWork)
                    {
                        program.UnregisterPendingAsyncProgram();
                        drained++;
                    }
                }

                return drained;
            }

            private void CancelPendingAsyncBuildForShaderPipelineModeChange()
            {
                ReleaseAsyncLinkState();
                ResetProgramInterfaceCaches();
                _linkDataPrepared = false;
                _hashComputed = false;
            }

            private void RestartAsyncPendingDiagnostics()
            {
                _asyncPendingStartTimestamp = Stopwatch.GetTimestamp();
                _lastAsyncPendingWarningTimestamp = 0;
                _asyncLinkStuckFlushIssued = false;
            }

            private void ResetAsyncPendingDiagnostics()
            {
                _asyncPendingStartTimestamp = 0;
                _lastAsyncPendingWarningTimestamp = 0;
                _asyncLinkStuckFlushIssued = false;
            }

            private void ReportSlowAsyncPending(string phase)
            {
                long now = Stopwatch.GetTimestamp();
                if (_asyncPendingStartTimestamp == 0)
                    _asyncPendingStartTimestamp = now;

                double elapsedSeconds = StopwatchTicksToSeconds(now - _asyncPendingStartTimestamp);
                if (elapsedSeconds < AsyncShaderSlowWarningSeconds)
                    return;

                double warningElapsedSeconds = _lastAsyncPendingWarningTimestamp == 0
                    ? double.PositiveInfinity
                    : StopwatchTicksToSeconds(now - _lastAsyncPendingWarningTimestamp);
                if (warningElapsedSeconds < AsyncShaderSlowWarningSeconds)
                    return;

                _lastAsyncPendingWarningTimestamp = now;
                Debug.OpenGL($"[ShaderAsync] Program '{GetProgramDebugName()}' hash={Hash} phase={phase} still pending after {elapsedSeconds:F2}s; continuing non-blocking poll.");
            }

            // Soft time budget for synchronous link work performed inline by
            // PollPendingAsyncPrograms. When the shared-context compile/link queue is
            // unavailable and pending programs are forced down the synchronous /
            // hazard-sync-link path, glLinkProgram runs on the render thread and a
            // backlog can accumulate (e.g. on first frame after model import or
            // window resize). This budget caps how much render-thread time we spend
            // chewing through that backlog per frame so any single frame stall stays
            // bounded; remaining programs are picked up next frame.
        }
    }
}
