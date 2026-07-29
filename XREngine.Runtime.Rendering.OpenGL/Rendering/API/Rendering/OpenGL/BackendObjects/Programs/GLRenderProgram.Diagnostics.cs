using System.Diagnostics;
using XREngine;
using XREngine.Rendering;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLRenderProgram
        {
            public ShaderProgramLinkDiagnosticsSnapshot GetLinkDiagnosticsSnapshot()
            {
                TryGetBuildBindingId(out uint programId);

                ShaderProgramBackendStatus backend = Data.ShaderMetadata.Backend;
                ShaderProgramBuildTelemetry lastBuild = Data.ShaderMetadata.LastBuild;
                GLProgramCompileLinkQueue? sourceQueue = Renderer.ProgramCompileLinkQueue;
                GLProgramBinaryUploadQueue? binaryQueue = Renderer.ProgramBinaryUploadQueue;
                var settings = RuntimeEngine.Rendering.Settings;
                SharedLinkedProgram? sharedProgram = _sharedLinkedProgram;
                string? binaryCacheKey = _cachedProgram?.CacheKey ?? _preparedCacheKey;
                ulong effectiveSourceHash = Hash != 0 ? Hash : _preparedHash;

                double activeBuildElapsedMilliseconds = _activeBuildQueueTimestamp == 0
                    ? 0.0
                    : StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - _activeBuildQueueTimestamp);

                return new ShaderProgramLinkDiagnosticsSnapshot(
                    ProgramName: GetProgramDebugName(),
                    Hash: Hash,
                    PreparedHash: _preparedHash,
                    EffectiveSourceHash: effectiveSourceHash,
                    ProgramId: programId,
                    ReplacementProgramId: _replacementProgramId,
                    SharedLinkedProgramId: sharedProgram?.ProgramId ?? 0,
                    SharedLinkedProgramReferenceCount: sharedProgram?.ReferenceCount ?? 0,
                    OwnsCurrentProgramHandle: sharedProgram is null
                        ? programId != 0
                        : ReferenceEquals(sharedProgram.OwnerProgram, this),
                    HandleSource: ResolveLinkedProgramHandleSource(sharedProgram),
                    ProgramDescriptorKey: Data.ProgramDescriptor.StableKey,
                    BinaryCacheKey: binaryCacheKey,
                    IsGenerated: IsGenerated,
                    IsLinked: IsLinked,
                    LinkReady: LinkReady,
                    IsAsyncBuildPending: IsAsyncBuildPending,
                    HasPendingAsyncWork: HasPendingAsyncWork,
                    HasQueuedOrRunningAsyncWork: HasQueuedOrRunningAsyncWork,
                    PendingAsyncProgramRegistered: IsRegisteredPendingAsyncProgram,
                    ReplacementProgramPending: _replacementProgramPending,
                    AsyncLinkPhase: _asyncLinkPhase.ToString(),
                    AsyncPendingSeconds: _asyncPendingStartTimestamp == 0
                        ? 0.0
                        : StopwatchTicksToSeconds(Stopwatch.GetTimestamp() - _asyncPendingStartTimestamp),
                    LinkDataPrepared: _linkDataPrepared,
                    LinkPreparationPending: IsLinkPreparationPending,
                    PreparedBinaryCacheHit: _preparedIsCached,
                    PreparedCacheKey: _preparedCacheKey,
                    HasCachedProgram: _cachedProgram is not null,
                    HasSharedLinkedProgram: _sharedLinkedProgram is not null,
                    IsKnownAsyncLinkHazard: IsKnownAsyncLinkHazard,
                    ShaderCount: Data.Shaders.Count,
                    ShaderStages: BuildFailedHashStageListSnapshot(),
                    Separable: Data.Separable,
                    BackendStage: backend.Stage,
                    BackendName: backend.Backend,
                    BackendDetail: backend.Detail,
                    BackendFailureReason: backend.FailureReason,
                    BackendCompileMilliseconds: backend.CompileMilliseconds,
                    BackendLinkMilliseconds: backend.LinkMilliseconds,
                    BackendFingerprint: backend.Fingerprint,
                    ActiveBuildBackend: _activeBuildBackend,
                    ActiveBuildFingerprint: _activeBuildFingerprint,
                    ActiveBuildElapsedMilliseconds: activeBuildElapsedMilliseconds,
                    LastBuildBackend: lastBuild.Backend,
                    LastBuildFingerprint: lastBuild.Fingerprint,
                    LastBuildQueueLatencyMilliseconds: lastBuild.QueueLatencyMilliseconds,
                    LastBuildCompileMilliseconds: lastBuild.CompileMilliseconds,
                    LastBuildLinkMilliseconds: lastBuild.LinkMilliseconds,
                    LastBuildBinaryLoadMilliseconds: lastBuild.BinaryLoadMilliseconds,
                    LastBuildReflectionMilliseconds: lastBuild.ReflectionMilliseconds,
                    LastBuildFailureReason: lastBuild.FailureReason,
                    ConfiguredStrategy: settings.OpenGLShaderLinkStrategy.ToString(),
                    AsyncProgramCompilation: settings.AsyncProgramCompilation,
                    AsyncProgramBinaryUpload: settings.AsyncProgramBinaryUpload,
                    AllowBinaryProgramCaching: settings.AllowBinaryProgramCaching,
                    DriverParallelAvailable: Renderer.UseDriverParallelShaderCompile,
                    OpenGLShaderCompilerThreadCount: settings.OpenGLShaderCompilerThreadCount,
                    SharedContextQueueAvailable: sourceQueue is { IsAvailable: true },
                    SharedContextQueueCanEnqueue: sourceQueue is { CanEnqueue: true },
                    SharedContextQueueUnhealthy: sourceQueue is { IsWorkerUnhealthy: true },
                    SharedContextInFlight: sourceQueue?.InFlightCount ?? 0,
                    SharedContextMaxInFlight: sourceQueue?.MaxInFlightTotal ?? 0,
                    SharedContextWorkerCount: sourceQueue?.WorkerCount ?? 0,
                    SharedContextOldestPendingSeconds: sourceQueue?.OldestPendingAgeSeconds ?? 0.0,
                    BinaryUploadQueueAvailable: binaryQueue is { IsAvailable: true },
                    BinaryUploadQueueCanEnqueue: binaryQueue is { CanEnqueue: true },
                    BinaryUploadQueueUnhealthy: binaryQueue is { IsWorkerUnhealthy: true },
                    BinaryUploadInFlight: binaryQueue?.InFlightCount ?? 0,
                    BinaryUploadMaxInFlight: GLProgramBinaryUploadQueue.MaxInFlight,
                    BinaryUploadInFlightCacheKeys: binaryQueue?.InFlightCacheKeyCount ?? 0,
                    BinaryUploadOldestPendingSeconds: binaryQueue?.OldestPendingAgeSeconds ?? 0.0,
                    AsyncBinaryUploadQueueWaitPending: _asyncBinaryUploadQueueWaitPending,
                    AsyncBinaryUploadPending: _asyncBinaryUploadPending,
                    AsyncCompileLinkPending: _asyncCompileLinkPending,
                    AsyncCompileLinkQueueWaitPending: _asyncCompileLinkQueueWaitPending,
                    AsyncCompileDuplicateHashWaitPending: _asyncCompileDuplicateHashWaitPending);
            }

            private ShaderProgramLinkHandleSource ResolveLinkedProgramHandleSource(SharedLinkedProgram? sharedProgram)
            {
                if (sharedProgram is not null)
                    return ShaderProgramLinkHandleSource.SharedLinkedProgram;
                if (_cachedProgram is not null || _preparedIsCached)
                    return ShaderProgramLinkHandleSource.OwnedBinary;
                return IsLinked ? ShaderProgramLinkHandleSource.OwnedSource : ShaderProgramLinkHandleSource.None;
            }
        }
    }
}
