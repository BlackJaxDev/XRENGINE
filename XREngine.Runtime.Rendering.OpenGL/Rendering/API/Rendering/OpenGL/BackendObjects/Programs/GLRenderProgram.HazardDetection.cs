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
            private bool ReturnPendingBuildResult()
                => IsLinked && _replacementProgramPending;

            // Phase 3: capture compact diagnostic metadata the first time a hash fails
            // and bump the failed-hash dictionary. Cheap, non-allocating in the steady
            // state because subsequent calls just hit the AddOrUpdate update branch.
            private void MarkHashFailed(string? reason)
            {
                Failed.TryAdd(Hash, 0);
                long now = Stopwatch.GetTimestamp();
                string label = GetProgramDebugName();
                string stageList = BuildFailedHashStageListSnapshot();
                bool separable = Data.Separable;
                FailedHashDiagnostics.AddOrUpdate(
                    Hash,
                    static (_, args) => new FailedHashRecord(
                        args.now,
                        0L,
                        0,
                        args.reason,
                        args.label,
                        args.stageList,
                        args.separable),
                    static (_, existing, args) => existing with
                    {
                        Reason = existing.Reason ?? args.reason,
                        Label = existing.Label ?? args.label,
                        StageList = existing.StageList ?? args.stageList,
                    },
                    (now, reason, label, stageList, separable));
            }

            private string BuildFailedHashStageListSnapshot()
            {
                int count = Data.Shaders.Count;
                if (count == 0)
                    return "<none>";
                var sb = new StringBuilder(count * 16);
                for (int i = 0; i < count; i++)
                {
                    if (i > 0)
                        sb.Append('|');
                    sb.Append(Data.Shaders[i].Type);
                }
                return sb.ToString();
            }

            // Phase 3: rate-limited follow-up log for known-failed hashes encountered
            // during link. The first failure already produced a full diagnostic via
            // PrintLinkDebug / PublishBackendStatus(Failed); this path emits compact
            // SOURCE_FAILED_SKIPPED entries throttled to one per hash per
            // FailedHashSkipLogThrottleSeconds, so a flood of retries does not spam
            // logs or regenerate ShaderProgramSourceSummary every frame.
            private void EmitFailedHashSkipLog(string? cacheKey, uint bindingId)
            {
                long now = Stopwatch.GetTimestamp();
                bool emit = false;
                FailedHashRecord snapshot = default;
                FailedHashDiagnostics.AddOrUpdate(
                    Hash,
                    _ =>
                    {
                        emit = true;
                        snapshot = new FailedHashRecord(now, now, 1, null, GetProgramDebugName(), BuildFailedHashStageListSnapshot(), Data.Separable);
                        return snapshot;
                    },
                    (_, existing) =>
                    {
                        int newSkip = existing.SkipCount + 1;
                        double secondsSinceLast = StopwatchTicksToMilliseconds(now - existing.LastLogTicks) / 1000.0;
                        if (existing.LastLogTicks == 0L || secondsSinceLast >= FailedHashSkipLogThrottleSeconds)
                        {
                            emit = true;
                            snapshot = existing with { LastLogTicks = now, SkipCount = newSkip };
                            return snapshot;
                        }
                        snapshot = existing with { SkipCount = newSkip };
                        return snapshot;
                    });

                if (!emit)
                    return;

                ShaderProgramLifecycleDiagnostics.RecordFailedHashSkip();
                long start = snapshot.FirstFailureTicks > 0L ? snapshot.FirstFailureTicks : now;
                double elapsedMs = StopwatchTicksToMilliseconds(now - start);
                Debug.OpenGLWarning(
                    $"[ShaderLink] SOURCE_FAILED_SKIPPED hash={Hash} label='{snapshot.Label ?? GetProgramDebugName()}' " +
                    $"separable={snapshot.Separable} stages={snapshot.StageList ?? "<none>"} skipCount={snapshot.SkipCount} " +
                    $"elapsedMs={elapsedMs:F0} reason='{snapshot.Reason ?? "<unknown>"}' " +
                    $"fingerprint={cacheKey ?? "<none>"} programId={bindingId}.");
            }

            private static string GetFailedHashFailureReason(ulong hash)
            {
                if (!FailedHashDiagnostics.TryGetValue(hash, out FailedHashRecord record) ||
                    string.IsNullOrWhiteSpace(record.Reason))
                {
                    return "Hash is marked failed.";
                }

                return record.Reason!;
            }

            private void MarkBuildFailed()
            {
                _asyncBinaryUploadQueueWaitPending = false;
                _asyncCompileDuplicateHashWaitPending = false;
                _asyncCompileLinkQueueWaitPending = false;

                if (_replacementProgramPending)
                {
                    AbandonReplacementProgram();
                    PublishBackendStatus(
                        EShaderProgramBackendStage.Abandoned,
                        _activeBuildBackend,
                        "replacement failed; keeping previous program active",
                        "Replacement build failed.");
                    return;
                }

                IsLinked = false;
            }

            private void DeferRetryAfterSharedContextSourceTimeout(uint abandonedProgramId)
            {
                bool retrySynchronously = AllowSynchronousSourceRetryAfterAsyncTimeout;
                bool retryDriverParallel = !retrySynchronously &&
                    Renderer.UseDriverParallelShaderCompile &&
                    !IsKnownAsyncLinkHazard;
                if (Hash != 0)
                {
                    SharedContextLargeSourceTimeouts.TryAdd(Hash, 0);
                    if (retrySynchronously)
                        SynchronousSourceRetryHashes.TryAdd(Hash, 0);
                    else if (retryDriverParallel)
                        DriverParallelSourceTimeouts.TryRemove(Hash, out _);
                    else
                        MarkHashFailed("Shared-context source link timed out.");
                    InFlightCompilations.TryRemove(Hash, out _);
                }

                _asyncBinaryUploadQueueWaitPending = false;
                _asyncCompileDuplicateHashWaitPending = false;
                _asyncCompileLinkQueueWaitPending = false;
                _asyncCompileLinkPending = false;
                IsLinked = false;

                if (abandonedProgramId != 0)
                {
                    bool abandonedReplacement = _replacementProgramPending && _replacementProgramId == abandonedProgramId;
                    if (abandonedReplacement)
                    {
                        _replacementProgramId = 0;
                        _replacementProgramPending = false;
                    }
                    else if (TryGetBindingId(out uint currentProgramId) && currentProgramId == abandonedProgramId)
                    {
                        OrphanForDeferredDelete();
                    }

                    Debug.OpenGLWarning(
                        $"Abandoning shared-context source link for program '{GetProgramDebugName()}' hash={Hash}: " +
                        $"programId={abandonedProgramId} leaked to avoid blocking GL cleanup calls.");
                }

                if (retrySynchronously)
                {
                    _hashComputed = false;
                    InvalidatePreparedLinkData();
                    PublishBackendStatus(
                        EShaderProgramBackendStage.SynchronousFallback,
                        "SharedContextSource",
                        "shared-context source link stalled; retrying with synchronous source link",
                        "Shared-context source link timed out.");
                    BeginPrepareLinkData();
                    RegisterPendingAsyncProgram();
                    return;
                }

                if (retryDriverParallel)
                {
                    _hashComputed = false;
                    InvalidatePreparedLinkData();
                    PublishBackendStatus(
                        EShaderProgramBackendStage.DriverParallelPending,
                        "SharedContextSource",
                        "shared-context source link stalled; retrying driver-parallel source lane",
                        "Shared-context source link timed out.");
                    BeginPrepareLinkData();
                    RegisterPendingAsyncProgram();
                    return;
                }

                PublishBackendStatus(
                    EShaderProgramBackendStage.Failed,
                    "SharedContextSource",
                    "shared-context source link stalled; leaving fallback material active",
                    "Shared-context source link timed out.");
                MarkBuildFailed();
                UnregisterPendingAsyncProgram();
            }

        }
    }
}
