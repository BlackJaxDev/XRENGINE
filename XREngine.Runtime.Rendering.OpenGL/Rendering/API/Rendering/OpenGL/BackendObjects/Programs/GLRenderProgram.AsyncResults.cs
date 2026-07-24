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
            private const double PollPendingAsyncProgramsSyncBudgetMilliseconds = 4.0;
            private const int PollPendingAsyncProgramsReadyResultBudget = 2048;

            internal static void PollPendingAsyncPrograms(int maxPrograms)
            {
                int remaining = Math.Max(1, maxPrograms);
                int readyResultResolveRemaining = Math.Max(remaining, PollPendingAsyncProgramsReadyResultBudget);
                long frameStartTicks = Stopwatch.GetTimestamp();
                double budgetMs = PollPendingAsyncProgramsSyncBudgetMilliseconds;
                // Snapshot + sort by priority so main-pass programs are polled (and any
                // outstanding sync hazard work runs) before shadow / VR / compute work
                // when the per-frame sync budget is tight. The dictionary's natural
                // iteration order is arbitrary, so without this reorder a backlog of
                // shadow links could starve the user-visible main-pass programs.
                GLRenderProgram[] snapshot = [.. PendingAsyncPrograms.Keys];
                if (snapshot.Length > 1)
                    Array.Sort(snapshot, _pendingAsyncPriorityComparer);
                foreach (GLRenderProgram program in snapshot)
                {
                    bool fastResultResolve = program.CanCompleteFromSharedBinaryCache() ||
                        program.CanCompleteFromSharedContextCompileQueue();
                    if (fastResultResolve)
                    {
                        if (readyResultResolveRemaining <= 0)
                        {
                            if (remaining <= 0)
                                break;

                            continue;
                        }

                        readyResultResolveRemaining--;
                    }
                    else if (remaining <= 0)
                    {
                        if (readyResultResolveRemaining <= 0)
                            break;

                        continue;
                    }
                    else
                    {
                        remaining--;
                    }

                    if (!program.HasPendingAsyncWork)
                    {
                        program.UnregisterPendingAsyncProgram();
                        continue;
                    }

                    program.Link(nonBlocking: true);
                    long now = Stopwatch.GetTimestamp();
                    if (!program.HasPendingAsyncWork)
                        program.UnregisterPendingAsyncProgram();
                    // If this Link() ran a synchronous / hazard-sync path (no async
                    // work outstanding when it returned) and consumed any meaningful
                    // wall time, honor a per-frame budget so we don't drain a large
                    // backlog inline. Programs still in async phases don't count
                    // against the budget because their poll cost is negligible.
                    bool ranSyncWork = !fastResultResolve &&
                        (!program.HasPendingAsyncWork || program._asyncLinkPhase == EAsyncLinkPhase.Idle);
                    if (ranSyncWork)
                    {
                        double frameElapsedMs = StopwatchTicksToSeconds(now - frameStartTicks) * 1000.0;
                        if (frameElapsedMs >= budgetMs)
                            break;
                    }
                }

                ProcessDeferredAsyncLinkCleanups(maxPrograms);
                ProcessDeferredProgramHandleDeletes(maxPrograms);
            }

            private sealed class PendingAsyncPriorityComparer : IComparer<GLRenderProgram>
            {
                public int Compare(GLRenderProgram? x, GLRenderProgram? y)
                {
                    byte xp = (byte)(x?.Data?.Priority ?? EProgramPriority.Main);
                    byte yp = (byte)(y?.Data?.Priority ?? EProgramPriority.Main);
                    return xp.CompareTo(yp);
                }
            }

            private static readonly PendingAsyncPriorityComparer _pendingAsyncPriorityComparer = new();

            private static void ProcessDeferredProgramHandleDeletes(int maxPrograms)
            {
                int remaining = Math.Max(1, maxPrograms);
                while (remaining-- > 0 && DeferredProgramHandleDeletes.TryDequeue(out var delete))
                {
                    if (delete.Renderer.ShouldOrphanGLHandlesForShutdown)
                        continue;

                    if (RuntimeEngine.Rendering.State.RenderFrameId < delete.EarliestFrameId)
                    {
                        DeferredProgramHandleDeletes.Enqueue(delete);
                        break;
                    }

                    try
                    {
                        if (delete.ProgramId != 0)
                        {
                            MeasureRenderingProgramGlCallStatic(
                                "glDeleteProgram",
                                delete.ProgramId,
                                () => delete.Renderer.Api.DeleteProgram(delete.ProgramId),
                                "phase=deferred-handle-delete");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.OpenGLWarning($"Deferred shader program handle delete failed: {ex.Message}");
                    }
                }
            }

            private static void EnqueueDeferredProgramHandleDelete(OpenGLRenderer renderer, uint programId)
            {
                if (programId == 0 || renderer.ShouldOrphanGLHandlesForShutdown)
                    return;

                DeferredProgramHandleDeletes.Enqueue(new DeferredProgramHandleDelete(
                    renderer,
                    programId,
                    RuntimeEngine.Rendering.State.RenderFrameId + 2UL));
            }

            private static void ProcessDeferredAsyncLinkCleanups(int maxPrograms)
            {
                int remaining = Math.Max(1, maxPrograms);
                while (remaining-- > 0 && DeferredAsyncLinkCleanups.TryDequeue(out DeferredAsyncLinkCleanup? cleanup))
                {
                    try
                    {
                        if (!cleanup.TryProcess())
                            DeferredAsyncLinkCleanups.Enqueue(cleanup);
                    }
                    catch (Exception ex)
                    {
                        Debug.OpenGLWarning($"Deferred async shader cleanup failed: {ex.Message}");
                    }
                }
            }

            /// <summary>
            /// True once <see cref="Hash"/> has been computed for this instance.
            /// Avoids redundant CalcHash calls while the instance is deferring.
            /// Reset in <see cref="Reset"/>.
            /// </summary>
            private bool _hashComputed;

            /// <summary>
            /// Continues an in-progress driver async compile/link operation.
            /// Called from <see cref="Link"/> when <see cref="_asyncLinkPhase"/> is not Idle.
            /// </summary>
            private bool ContinueAsyncLink()
            {
                if (!TryGetBuildBindingId(out uint bindingId))
                    return ReturnPendingBuildResult();

                switch (_asyncLinkPhase)
                {
                    case EAsyncLinkPhase.Compiling:
                    {
                        using var prof = RuntimeEngine.Profiler.Start("GLRenderProgram.ContinueAsyncLink.PollCompile", ProfilerScopeKind.ConditionalLoop);

                        bool anyPending = false;
                        bool anyFailed = false;
                        foreach (GLShader shader in _shaderCache.Values)
                        {
                            if (shader.IsCompilePending)
                            {
                                if (!shader.PollCompileCompletion())
                                    anyPending = true;
                                else if (!shader.IsCompiled)
                                    anyFailed = true;
                            }
                            else if (!shader.IsCompiled)
                            {
                                anyFailed = true;
                            }
                        }

                        if (anyPending && !anyFailed)
                        {
                            PublishBackendStatus(
                                EShaderProgramBackendStage.DriverParallelPending,
                                "DriverParallelSource",
                                "shader compile completion pending");
                            ReportSlowAsyncPending("compile");
                            return ReturnPendingBuildResult(); // Still compiling - retry next frame
                        }

                        double compileMilliseconds = CompleteUberBackendCompileTracking();

                        if (anyFailed)
                        {
                            ShaderProgramLifecycleDiagnostics.RecordSourceFailure();
                            Debug.OpenGLWarning($"Failed to compile program with hash {Hash}.");
                            CompleteUberBackendTracking(false, "Backend shader compile failed.", compileMilliseconds, 0.0);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Failed,
                                "DriverParallelSource",
                                "shader compile failed",
                                "Backend shader compile failed.",
                                compileMilliseconds,
                                0.0);
                            CompleteBuildTelemetry(false, compileMilliseconds, failureReason: "Backend shader compile failed.");
                            MarkHashFailed("Backend shader compile failed.");
                            InFlightCompilations.TryRemove(Hash, out _);
                            CleanupAsyncLink();
                            MarkBuildFailed();
                            return IsLinked;
                        }

                        // All shaders compiled â€” attach and dispatch link
                        var shaderCache = _shaderCache.Values;
                        List<uint> attachedShaderIds = [];
                        bool noErrors = true;
                        foreach (GLShader shader in shaderCache)
                        {
                            if (shader.IsCompiled)
                            {
                                uint shaderId = shader.BindingId;
                                MeasureRenderingProgramGlCall(
                                    "glAttachShader",
                                    bindingId,
                                    () => Api.AttachShader(bindingId, shaderId),
                                    $"shaderId={shaderId} shaderType={shader.Data.Type} phase=driver-parallel-dispatch");
                                attachedShaderIds.Add(shaderId);
                            }
                            else
                            {
                                if (noErrors)
                                {
                                    noErrors = false;
                                    Debug.OpenGLWarning("One or more shaders failed to compile, can't link program.");
                                }
                                string? text = shader.Data.Source.Text;
                                if (text is not null)
                                    Debug.OpenGL(text);
                            }
                        }

                        if (!noErrors)
                        {
                            MarkHashFailed("Driver-parallel attach observed shader compile errors.");
                            InFlightCompilations.TryRemove(Hash, out _);
                            CleanupAsyncLink();
                            MarkBuildFailed();
                            return IsLinked;
                        }

                        BeginUberBackendLinkTracking(compileMilliseconds);
                        PublishBackendStatus(
                            EShaderProgramBackendStage.Linking,
                            "DriverParallelSource",
                            "driver-parallel link dispatched",
                            compileMilliseconds: compileMilliseconds);
                        EnsureProgramBinaryRetrievableHintForSourceBuild(bindingId, "DriverParallelSource");
                        GLProgramCompileLinkQueue.TransformFeedbackLinkInfo transformFeedback;
                        try
                        {
                            transformFeedback = BuildTransformFeedbackLinkInfo();
                        }
                        catch (Exception ex)
                        {
                            string message = "Invalid OpenGL transform feedback layout: " + ex.Message;
                            Debug.OpenGLWarning(message);
                            CompleteUberBackendTracking(false, message, compileMilliseconds, 0.0);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Failed,
                                "TransformFeedback",
                                "transform feedback layout validation failed",
                                message,
                                compileMilliseconds,
                                0.0);
                            CompleteBuildTelemetry(false, compileMilliseconds, failureReason: message);
                            MarkHashFailed(message);
                            InFlightCompilations.TryRemove(Hash, out _);
                            CleanupAsyncLink();
                            MarkBuildFailed();
                            return IsLinked;
                        }
                        ApplyTransformFeedbackVaryings(
                            bindingId,
                            transformFeedback,
                            "backend=DriverParallelSource phase=driver-parallel-transform-feedback-varyings");
                        MeasureRenderingProgramGlCall(
                            "glLinkProgram",
                            bindingId,
                            () => Api.LinkProgram(bindingId),
                            "backend=DriverParallelSource");
                        _asyncLinkedProgramId = bindingId;
                        _asyncAttachedShaderIds = [.. attachedShaderIds];
                        _asyncLinkPhase = EAsyncLinkPhase.Linking;
                        RestartAsyncPendingDiagnostics();
                        RegisterPendingAsyncProgram();
                        return ReturnPendingBuildResult(); // Will poll link completion next frame
                    }
                    case EAsyncLinkPhase.Linking:
                    {
                        using var prof = RuntimeEngine.Profiler.Start("GLRenderProgram.ContinueAsyncLink.PollLink", ProfilerScopeKind.ConditionalLoop);

                        uint linkedProgramId = _asyncLinkedProgramId != 0 ? _asyncLinkedProgramId : bindingId;

                        int complete = 0;
                        MeasureRenderingProgramGlCall(
                            "glGetProgramiv(GL_COMPLETION_STATUS)",
                            linkedProgramId,
                            () => Api.GetProgram(linkedProgramId, (GLEnum)GLShader.GL_COMPLETION_STATUS_ARB, out complete),
                            "phase=driver-parallel-link-poll");
                        if (complete == 0)
                        {
                            PublishBackendStatus(
                                EShaderProgramBackendStage.DriverParallelPending,
                                "DriverParallelSource",
                                "program link completion pending");
                            if (TryAbandonStuckAsyncLink(linkedProgramId))
                                return IsLinked;

                            ReportSlowAsyncPending("link");
                            return ReturnPendingBuildResult(); // Still linking - retry next frame
                        }

                        int status = 0;
                        MeasureRenderingProgramGlCall(
                            "glGetProgramiv(GL_LINK_STATUS)",
                            linkedProgramId,
                            () => Api.GetProgram(linkedProgramId, GLEnum.LinkStatus, out status),
                            "phase=driver-parallel-final-status");
                        bool linked = status != 0;
                        string? linkError = null;
                        double linkMilliseconds = _uberLinkStartTimestamp == 0
                            ? 0.0
                            : StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - _uberLinkStartTimestamp);
                        if (linked)
                        {
                            AdoptLinkedBuildProgram(linkedProgramId);
                            IsLinked = true;
                            long reflectionStart = Stopwatch.GetTimestamp();
                            CacheActiveUniforms();
                            double reflectionMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - reflectionStart);
                            CacheBinary(linkedProgramId);
                            if (_cachedProgram is { } cachedDriverProgram)
                                RegisterCurrentLinkedProgramForSharing(cachedDriverProgram.CacheKey, cachedDriverProgram.Format, linkedProgramId);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Ready,
                                "DriverParallelSource",
                                "driver-parallel link completed",
                                compileMilliseconds: _uberCompileMilliseconds,
                                linkMilliseconds: linkMilliseconds);
                            RuntimeEngine.Rendering.Stats.RecordShaderVariant(linked: true, generatedThisRun: true);
                            CompleteBuildTelemetry(true, _uberCompileMilliseconds, linkMilliseconds, reflectionMilliseconds: reflectionMilliseconds);
                        }
                        else
                        {
                            MeasureRenderingProgramGlCall(
                                "glGetProgramInfoLog",
                                linkedProgramId,
                                () => Api.GetProgramInfoLog(linkedProgramId, out linkError),
                                "phase=driver-parallel-final-log");
                        }

                        return CompleteAsyncLink(linkedProgramId, linked, linkError, "Async link failed", _uberCompileMilliseconds, linkMilliseconds);
                    }
                    default:
                        return ReturnPendingBuildResult();
                }
            }

            /// <summary>
            /// Non-blocking recovery for an async link whose <c>COMPLETION_STATUS_ARB</c>
            /// has not flipped to 1 within the expected window.
            /// </summary>
            /// <remarks>
            /// Two-stage strategy:
            /// <list type="number">
            /// <item>After <see cref="AsyncShaderStuckFlushSeconds"/>, issue a single
            /// <c>glFlush</c> to nudge the driver. Some drivers (notably NVIDIA's
            /// threaded path) can leave a parallel-link worker waiting on a fence
            /// from another context; flushing forces the command stream out.</item>
            /// <item>After <see cref="AsyncShaderHardAbandonSeconds"/>, abandon the
            /// wedged GL handles and keep the fallback visible. A synchronous source
            /// retry is only scheduled when explicitly enabled for driver experiments.
            /// We deliberately do NOT call <c>glGetProgramiv(GL_LINK_STATUS)</c> on a
            /// still-pending program because that call implicitly waits for completion
            /// and is known to hang indefinitely when the driver's link worker is stuck.</item>
            /// </list>
            /// Returns true when the stuck GL handles have been abandoned and the caller should stop polling them.
            /// </remarks>
            private bool TryAbandonStuckAsyncLink(uint linkedProgramId)
            {
                long now = Stopwatch.GetTimestamp();
                if (_asyncPendingStartTimestamp == 0)
                    _asyncPendingStartTimestamp = now;

                double elapsedSeconds = StopwatchTicksToSeconds(now - _asyncPendingStartTimestamp);

                if (!_asyncLinkStuckFlushIssued && elapsedSeconds >= AsyncShaderStuckFlushSeconds)
                {
                    _asyncLinkStuckFlushIssued = true;
                    Debug.OpenGLWarning(
                        $"[ShaderAsync] Program '{GetProgramDebugName()}' hash={Hash} still reports COMPLETION_STATUS=false " +
                        $"after {elapsedSeconds:F2}s; issuing glFlush() to nudge the driver. Continuing non-blocking poll.");
                    MeasureRenderingProgramGlCall(
                        "glFlush",
                        linkedProgramId,
                        () => Api.Flush(),
                        "phase=driver-parallel-stuck-nudge");
                    return false;
                }

                if (elapsedSeconds < AsyncShaderHardAbandonSeconds)
                    return false;

                Debug.OpenGLWarning(
                    $"[ShaderAsync] Program '{GetProgramDebugName()}' hash={Hash} stuck with COMPLETION_STATUS=false " +
                    $"after {elapsedSeconds:F2}s; abandoning link without querying GL_LINK_STATUS to avoid a driver hang.");

                AbandonStuckAsyncLink(linkedProgramId);
                return true;
            }

            /// <summary>
            /// Cleanup path for a parallel-link that the driver has effectively wedged.
            /// We must not issue any GL call that implicitly waits for link completion
            /// (notably <c>glGetProgramiv(GL_LINK_STATUS)</c>, <c>glDetachShader</c>,
            /// <c>glGetProgramInfoLog</c>, <c>glDeleteProgram</c>, <c>glDeleteShader</c>)
            /// because every one of those will block indefinitely on a stuck NVIDIA
            /// parallel-link worker. The program and shader GL objects are intentionally
            /// leaked into the driver â€” recovery from a driver-side hang is more
            /// important than reclaiming a few handles.
            /// </summary>
            private void AbandonStuckAsyncLink(uint linkedProgramId)
            {
                Debug.OpenGLWarning(
                    $"Abandoning async link for program '{GetProgramDebugName()}' hash={Hash}: programId={linkedProgramId} " +
                    $"and {(_asyncAttachedShaderIds?.Length ?? 0)} shader(s) leaked to avoid blocking GL calls.");

                if (Hash != 0)
                {
                    DriverParallelSourceTimeouts.TryAdd(Hash, 0);
                    if (AllowSynchronousSourceRetryAfterAsyncTimeout)
                        SynchronousSourceRetryHashes.TryAdd(Hash, 0);
                    else
                        MarkHashFailed("Driver-parallel source link timed out.");
                }
                CompleteUberBackendTracking(false, "Async link timed out (driver never reported completion).");
                PublishBackendStatus(
                    AllowSynchronousSourceRetryAfterAsyncTimeout
                        ? EShaderProgramBackendStage.SynchronousFallback
                        : EShaderProgramBackendStage.Abandoned,
                    _activeBuildBackend ?? "DriverParallelSource",
                    AllowSynchronousSourceRetryAfterAsyncTimeout
                        ? "driver never reported completion; retrying with synchronous source link"
                        : "driver never reported completion; leaving source build pending with fallback material",
                    "Async link timed out.");
                RuntimeEngine.Rendering.Stats.RecordShaderVariant(failed: true);
                CompleteBuildTelemetry(false, failureReason: "Async link timed out.");

                InFlightCompilations.TryRemove(Hash, out _);
                _asyncCompileDuplicateHashWaitPending = false;
                _asyncCompileLinkQueueWaitPending = false;
                _asyncCompileLinkPending = false;
                _asyncBinaryUploadQueueWaitPending = false;

                // Orphan program/shader handles locally so nothing in this engine ever
                // touches them again. We do NOT enqueue a deferred cleanup because the
                // queue polls COMPLETION_STATUS_ARB which will never flip for this program.
                if (_replacementProgramPending && linkedProgramId == _replacementProgramId)
                {
                    _replacementProgramId = 0;
                    _replacementProgramPending = false;
                    PublishBackendStatus(
                        EShaderProgramBackendStage.Abandoned,
                        _activeBuildBackend ?? "DriverParallelSource",
                        "replacement program leaked after stuck async link",
                        "Async link timed out.");

                    uint retryReplacementProgramId = CreateConfiguredProgramHandle();
                    if (retryReplacementProgramId != 0)
                    {
                        _replacementProgramId = retryReplacementProgramId;
                        _replacementProgramPending = true;
                        PublishBackendStatus(
                            EShaderProgramBackendStage.SynchronousFallback,
                            "SynchronousSource",
                            "replacement retry program allocated after async link timeout");
                    }
                }
                else if (TryGetBindingId(out _))
                {
                    IsLinked = false;
                    OrphanForDeferredDelete();
                }

                foreach (GLShader shader in _shaderCache.Values)
                    shader.OrphanForDeferredDelete();

                _asyncAttachedShaderIds = null;
                _asyncLinkedProgramId = 0;
                _asyncLinkPhase = EAsyncLinkPhase.Idle;
                UnregisterPendingAsyncProgram();
                _hashComputed = false;
                InvalidatePreparedLinkData();
                BeginPrepareLinkData();
                RegisterPendingAsyncProgram();
            }

            private bool CompleteAsyncLink(
                uint linkedProgramId,
                bool linked,
                string? linkError,
                string? failureKind,
                double compileMilliseconds = 0.0,
                double linkMilliseconds = 0.0)
            {
                if (!linked)
                {
                    if (string.IsNullOrWhiteSpace(linkError))
                    {
                        MeasureRenderingProgramGlCall(
                            "glGetProgramInfoLog",
                            linkedProgramId,
                            () => Api.GetProgramInfoLog(linkedProgramId, out linkError),
                            "phase=async-link-failure-log");
                    }

                    PrintLinkDebug(linkedProgramId, linkError, failureKind);
                    PublishBackendStatus(
                        EShaderProgramBackendStage.Failed,
                        _activeBuildBackend ?? "DriverParallelSource",
                        failureKind,
                        linkError,
                        compileMilliseconds,
                        linkMilliseconds);
                    RuntimeEngine.Rendering.Stats.RecordShaderVariant(failed: true);
                    CompleteBuildTelemetry(false, compileMilliseconds, linkMilliseconds, failureReason: linkError);
                    MarkHashFailed(linkError);
                }

                CompleteUberBackendTracking(linked, linkError, compileMilliseconds, linkMilliseconds);
                if (linked)
                {
                    SynchronousSourceRetryHashes.TryRemove(Hash, out _);
                    DriverParallelSourceTimeouts.TryRemove(Hash, out _);
                }
                InFlightCompilations.TryRemove(Hash, out _);

                if (_asyncAttachedShaderIds is not null)
                {
                    DetachShaders(linkedProgramId, _asyncAttachedShaderIds);
                    _asyncAttachedShaderIds = null;
                }

                _asyncLinkedProgramId = 0;
                _shaderCache.ForEach(x => x.Value.Destroy());
                _asyncLinkPhase = EAsyncLinkPhase.Idle;
                UnregisterPendingAsyncProgram();
                if (!linked)
                    MarkBuildFailed();
                return IsLinked;
            }

            private void CleanupAsyncLink()
            {
                if (_asyncAttachedShaderIds is not null)
                {
                    uint linkedProgramId = _asyncLinkedProgramId != 0 ? _asyncLinkedProgramId : GetBuildBindingId();
                    DetachShaders(linkedProgramId, _asyncAttachedShaderIds);
                    _asyncAttachedShaderIds = null;
                }
                _asyncLinkedProgramId = 0;
                _shaderCache.ForEach(x => x.Value.Destroy());
                _asyncLinkPhase = EAsyncLinkPhase.Idle;
                ResetUberBackendTracking();
                UnregisterPendingAsyncProgram();
            }

            private bool TryDeferAsyncLinkCleanupForDestroy()
            {
                if (_asyncLinkPhase != EAsyncLinkPhase.Linking ||
                    _asyncAttachedShaderIds is not { Length: > 0 } attachedShaderIds ||
                    _asyncLinkedProgramId == 0)
                {
                    return false;
                }

                uint programId = _asyncLinkedProgramId;
                uint[] shaderIds = [.. attachedShaderIds];
                bool isReplacement = _replacementProgramPending && programId == _replacementProgramId;

                // Detaching while ARB_parallel_shader_compile is still linking can force a driver fence.
                // Orphan the ids now and detach/delete after COMPLETION_STATUS reports finished.
                if (isReplacement)
                {
                    _replacementProgramId = 0;
                    _replacementProgramPending = false;
                }
                else
                {
                    OrphanForDeferredDelete();
                }

                foreach (GLShader shader in _shaderCache.Values)
                    shader.OrphanForDeferredDelete();

                DeferredAsyncLinkCleanups.Enqueue(new DeferredAsyncLinkCleanup(Renderer, programId, shaderIds));
                _asyncAttachedShaderIds = null;
                _asyncLinkedProgramId = 0;
                _asyncLinkPhase = EAsyncLinkPhase.Idle;
                return true;
            }

            private bool TryDeferSharedContextBuildCleanupForDestroy()
            {
                if (!_asyncCompileLinkPending && !_asyncBinaryUploadPending)
                    return false;

                if (!TryGetBindingId(out uint programId))
                    return false;

                // glDeleteProgram can serialize with another shared context while
                // that context is still linking or loading the same program handle.
                // Orphan the handle now and let the normal deferred cleanup pump
                // delete it after the driver reports completion.
                OrphanForDeferredDelete();
                DeferredAsyncLinkCleanups.Enqueue(new DeferredAsyncLinkCleanup(Renderer, programId, []));
                if (_asyncBinaryUploadPending)
                    Renderer.ProgramBinaryUploadQueue?.CancelUpload(programId);
                if (_asyncCompileLinkPending)
                    Renderer.ProgramCompileLinkQueue?.CancelCompileAndLink(programId);
                foreach (GLShader shader in _shaderCache.Values)
                    shader.Destroy();
                return true;
            }

            private void AbandonAsyncLinkStateForShutdown()
            {
                if (Hash != 0)
                    InFlightCompilations.TryRemove(Hash, out _);

                ReleaseSharedLinkedProgramReference();

                if (_asyncBinaryUploadPending && TryGetBindingId(out uint pendingBinaryProgramId))
                    Renderer.ProgramBinaryUploadQueue?.CancelUpload(pendingBinaryProgramId);

                if (_asyncCompileLinkPending && TryGetBindingId(out uint pendingSourceProgramId))
                    Renderer.ProgramCompileLinkQueue?.CancelCompileAndLink(pendingSourceProgramId);

                if (TryGetBindingId(out _))
                    OrphanForDeferredDelete();

                foreach (GLShader shader in _shaderCache.Values)
                    shader.OrphanForDeferredDelete();

                _replacementProgramId = 0;
                _replacementProgramPending = false;
                _asyncAttachedShaderIds = null;
                _asyncLinkedProgramId = 0;
                _asyncLinkPhase = EAsyncLinkPhase.Idle;
                _asyncBinaryUploadQueueWaitPending = false;
                _asyncBinaryUploadPending = false;
                _asyncCompileLinkPending = false;
                _asyncCompileLinkQueueWaitPending = false;
                _asyncCompileDuplicateHashWaitPending = false;
                _activeBuildBackend = null;
                _activeBuildFingerprint = null;
                _activeBuildQueueTimestamp = 0;
                InvalidatePreparedLinkData();
                ResetUberBackendTracking();
                UnregisterPendingAsyncProgram();
            }

            private void ReleaseAsyncLinkState()
            {
                if (Renderer.ShouldOrphanGLHandlesForShutdown)
                {
                    AbandonAsyncLinkStateForShutdown();
                    return;
                }

                if (Hash != 0)
                    InFlightCompilations.TryRemove(Hash, out _);

                if (!TryDeferAsyncLinkCleanupForDestroy() &&
                    !TryDeferSharedContextBuildCleanupForDestroy())
                {
                    CleanupAsyncLink();
                }
                _asyncBinaryUploadQueueWaitPending = false;
                _asyncBinaryUploadPending = false;
                _asyncCompileLinkPending = false;
                _asyncCompileLinkQueueWaitPending = false;
                AbandonReplacementProgram();
                ResetUberBackendTracking();
                UnregisterPendingAsyncProgram();
            }

            private void AbandonAsyncBinaryUpload(GLProgramBinaryUploadQueue? queue, uint programId, string reason)
            {
                BinaryProgram? cachedProgram = _cachedProgram;
                string? cacheKey = cachedProgram?.CacheKey ?? _activeBuildFingerprint;
                if (!string.IsNullOrWhiteSpace(cacheKey))
                    AsyncBinaryUploadTimeoutCacheKeys.TryAdd(cacheKey, 0);

                queue?.AbandonUpload(programId, cacheKey);
                _asyncBinaryUploadPending = false;
                _asyncBinaryUploadQueueWaitPending = false;

                if (cachedProgram is { } cachedBinaryProgram)
                    DeleteFromBinaryShaderCache(cachedBinaryProgram.CacheKey, cachedBinaryProgram.Format);

                if (programId != 0 && TryGetBindingId(out uint currentProgramId) && currentProgramId == programId)
                    OrphanForDeferredDelete();

                _cachedProgram = null;
                PublishBackendStatus(
                    EShaderProgramBackendStage.BinaryUploadFailed,
                    "BinaryUploadAsync",
                    reason,
                    reason,
                    fingerprint: cacheKey);
                LogRenderingProgramBuildEvent(
                    "BINARY_UPLOAD_ASYNC_ABANDONED",
                    "BinaryUploadAsync",
                    reason,
                    cacheKey,
                    programId,
                    binaryBytes: cachedProgram?.Length ?? 0,
                    binaryFormat: cachedProgram?.Format.ToString());
                CompleteBuildTelemetry(false, failureReason: reason);
            }

        }
    }
}
