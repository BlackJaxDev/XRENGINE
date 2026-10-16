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

            /// <summary>
            /// Stops an unfinished native build without submitting a second compile job.
            /// The driver still owns the abandoned handles until process shutdown.
            /// </summary>
            private void AbandonSharedContextSourceBuild(uint abandonedProgramId)
            {
                MarkHashFailed("Shared-context source compilation did not complete. Restart required.");
                ReleaseCompletedCompilationClaim(Hash, abandonedProgramId);
                SynchronousSourceRetryHashes.TryRemove(Hash, out _);
                ClearCompletedBuildPendingState();

                if (_replacementProgramPending && _replacementProgramId == abandonedProgramId)
                {
                    _replacementProgramId = 0;
                    _replacementProgramPending = false;
                    _replacementSourceRevisionKey = 0;
                }
                else if (TryGetBindingId(out uint currentProgramId) && currentProgramId == abandonedProgramId)
                {
                    IsLinked = false;
                    OrphanForDeferredDelete();
                }

                PublishBackendStatus(
                    EShaderProgramBackendStage.Abandoned,
                    "SharedContextSource",
                    "native compilation did not complete; source compilation stopped until process restart",
                    "No retry or GL cleanup was submitted for the unfinished build.");
            }

            /// <summary>
            /// Ends a source request after the process source compiler stops accepting work.
            /// </summary>
            private bool StopSourceBuildAfterCompilerFault(uint programId)
            {
                if (!GLProgramCompileLinkQueue.SourceCompilationFaulted)
                    return false;

                const string reason = "Native source compilation did not complete. Restart the process before compiling more shaders.";
                MarkHashFailed(reason);
                ReleaseCompletedCompilationClaim(Hash, programId);
                ClearCompletedBuildPendingState();
                PublishBackendStatus(EShaderProgramBackendStage.Failed, "Source", reason, reason);
                MarkBuildFailed();
                return true;
            }

            /// <summary>
            /// Recovers when a source build completed on the shared context but its program
            /// binary cannot be loaded by the render context. The source was already linked
            /// successfully, so this is a cross-context binary handoff failure rather than a
            /// shader-source failure; poisoning the hash made the affected material program
            /// permanently pending. Retry once through the render-context source lane.
            /// </summary>
            private void RetryAfterSharedContextProgramBinaryHandoffFailure(
                uint programId,
                string failure)
            {
                if (Hash != 0)
                {
                    // The retry selector treats this marker as an explicit one-shot request
                    // for the render-context source lane. A successful link removes it.
                    SynchronousSourceRetryHashes.TryAdd(Hash, 0);
                    InFlightCompilations.TryRemove(Hash, out _);
                }

                _asyncBinaryUploadQueueWaitPending = false;
                _asyncCompileDuplicateHashWaitPending = false;
                _asyncCompileLinkQueueWaitPending = false;
                _asyncCompileLinkPending = false;
                _hashComputed = false;
                InvalidatePreparedLinkData();

                PublishBackendStatus(
                    EShaderProgramBackendStage.SynchronousFallback,
                    "SharedContextSource",
                    "shared-context program binary handoff failed; retrying source link on the render context",
                    failure);
                LogRenderingProgramBuildEvent(
                    "SOURCE_QUEUE_BINARY_HANDOFF_RETRY",
                    "SynchronousSource",
                    failure,
                    _activeBuildFingerprint,
                    programId,
                    _preparedCompileInputs);
                BeginPrepareLinkData();
                RegisterPendingAsyncProgram();
            }

        }
    }
}
