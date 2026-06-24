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
            private BinaryProgram? _cachedProgram = null;
            private SharedLinkedProgram? _sharedLinkedProgram;
            private readonly object _materialUniformSourceLock = new();
            private XRMaterialBase? _lastMaterialUniformSource;
            private ulong _lastMaterialUniformSourceLayoutVersion;

            private readonly record struct SharedLinkedProgramKey(OpenGLRenderer Renderer, string CacheKey);

            private sealed class SharedLinkedProgram(OpenGLRenderer renderer, string cacheKey, uint programId, ulong hash, GLEnum format, bool separable, UniformMetadataEntry[] uniforms, GLRenderProgram ownerProgram)
            {
                private readonly object _lock = new();
                private int _referenceCount = 1;
                private bool _deleteQueued;
                private XRMaterialBase? _lastUniformSource;
                private ulong _lastUniformSourceLayoutVersion;

                public OpenGLRenderer Renderer { get; } = renderer;
                public string CacheKey { get; } = cacheKey;
                public uint ProgramId { get; } = programId;
                public ulong Hash { get; } = hash;
                public GLEnum Format { get; } = format;
                public bool Separable { get; } = separable;
                public UniformMetadataEntry[] Uniforms { get; } = uniforms;
                public GLRenderProgram OwnerProgram { get; } = ownerProgram;
                public int ReferenceCount
                {
                    get
                    {
                        lock (_lock)
                            return _referenceCount;
                    }
                }

                public bool DeleteQueued
                {
                    get
                    {
                        lock (_lock)
                            return _deleteQueued;
                    }
                }

                public bool TryAddReference()
                {
                    lock (_lock)
                    {
                        if (_deleteQueued)
                            return false;

                        _referenceCount++;
                        ShaderProgramLifecycleDiagnostics.RecordSharedProgramAttach(_referenceCount);
                        return true;
                    }
                }

                public bool MarkUniformSource(XRMaterialBase source)
                {
                    ulong layoutVersion = source.BindingLayoutVersion;
                    lock (_lock)
                    {
                        if (ReferenceEquals(_lastUniformSource, source) &&
                            _lastUniformSourceLayoutVersion == layoutVersion)
                            return false;

                        _lastUniformSource = source;
                        _lastUniformSourceLayoutVersion = layoutVersion;
                        return true;
                    }
                }

                public bool ReleaseReference()
                {
                    lock (_lock)
                    {
                        if (_referenceCount <= 0)
                            return false;

                        _referenceCount--;
                        ShaderProgramLifecycleDiagnostics.RecordSharedProgramDetach();
                        if (_referenceCount > 0 || _deleteQueued)
                            return false;

                        _deleteQueued = true;
                        return true;
                    }
                }
            }

            private static readonly ConcurrentDictionary<SharedLinkedProgramKey, SharedLinkedProgram> SharedLinkedPrograms = new();

            // Pre-computed link data: populated by PrepareLinkData() on any thread,
            // consumed by Link() on the GL thread to skip the expensive CacheLookup phase.
            private volatile bool _linkDataPrepared;
            private ulong _preparedHash;
            private string? _preparedCacheKey;
            private bool _preparedIsCached;
            private BinaryProgram _preparedBinProg;
            private GLProgramCompileLinkQueue.ShaderInput[]? _preparedCompileInputs;
            private int _linkPreparationPendingGeneration = -1;
            private int _linkPreparationGeneration;
            private Exception? _linkPreparationFailure;

            // Async binary upload state: set when a glProgramBinary call has been
            // dispatched to the shared context thread and we are waiting for completion.
            private volatile bool _asyncBinaryUploadPending;
            private volatile bool _asyncBinaryUploadQueueWaitPending;

            // Async compile+link state: set when shader compilation and program linking
            // have been dispatched to the shared context thread.
            private volatile bool _asyncCompileLinkPending;
            private volatile bool _asyncCompileLinkQueueWaitPending;
            private volatile bool _asyncCompileDuplicateHashWaitPending;

            /// <summary>
            /// Async link phases used when GL_ARB_parallel_shader_compile is active.
            /// </summary>
            private enum EAsyncLinkPhase : byte
            {
                /// <summary>No async operation in progress.</summary>
                Idle,
                /// <summary>Shaders dispatched for async compilation; polling COMPLETION_STATUS_ARB.</summary>
                Compiling,
                /// <summary>glLinkProgram dispatched; polling COMPLETION_STATUS_ARB on the program.</summary>
                Linking,
            }

            private EAsyncLinkPhase _asyncLinkPhase;
            private uint[]? _asyncAttachedShaderIds;
            private uint _asyncLinkedProgramId;
            private ulong _uberVariantHash;
            private long _uberCompileStartTimestamp;
            private long _uberLinkStartTimestamp;
            private double _uberCompileMilliseconds;
            private string? _linkRequestStackTrace;
            private long _asyncPendingStartTimestamp;
            private long _lastAsyncPendingWarningTimestamp;
            private bool _asyncLinkStuckFlushIssued;
            private string? _activeBuildBackend;
            private string? _activeBuildFingerprint;
            private long _activeBuildQueueTimestamp;
            private const double AsyncShaderSlowWarningSeconds = 15.0;
            // After this long without COMPLETION_STATUS_ARB == 1, issue a single glFlush()
            // to nudge the driver in case its parallel-link worker is starved on a missing fence.
            private const double AsyncShaderStuckFlushSeconds = 5.0;
            // After this long the link is treated as failed and cleaned up. We deliberately do
            // NOT call glGetProgramiv(GL_LINK_STATUS) on a still-pending program because that
            // call is documented to implicitly wait for completion and is known to hang
            // indefinitely on NVIDIA's threaded driver when the parallel-link worker is stuck.
            private const double AsyncShaderHardAbandonSeconds = 30.0;
            private const double SlowLinkPreparationWarningMilliseconds = 100.0;
            private const double SlowShaderLinkSourceDumpMilliseconds = 5000.0;
            private const double ShaderCompletionPollGlCallSlowLogMilliseconds = 1.0;
            private static readonly bool DumpSlowShaderSources = string.Equals(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.DumpSlowShaderSource),
                "1",
                StringComparison.Ordinal);
            private static readonly bool TraceShaderCompletionPollGlCalls = string.Equals(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.TraceShaderCompletionPollGlCalls),
                "1",
                StringComparison.Ordinal);
            private static readonly bool AllowRenderThreadDriverParallelSourceLinks = string.Equals(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.AllowRenderThreadDriverParallelSource),
                "1",
                StringComparison.Ordinal);
            private static readonly bool SharedLinkedProgramReuseEnabled = string.Equals(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.EnableSharedLinkedProgramReuse),
                "1",
                StringComparison.Ordinal);
            private static readonly ProgramBinaryRetrievableHintMode BinaryRetrievableHintMode = ResolveBinaryRetrievableHintMode();

            private enum ProgramBinaryRetrievableHintMode : byte
            {
                Always,
                SourceBuildOnly,
                Never,
            }

            private static ProgramBinaryRetrievableHintMode ResolveBinaryRetrievableHintMode()
            {
                string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProgramBinaryRetrievableHint);
                if (string.IsNullOrWhiteSpace(value))
                    return ProgramBinaryRetrievableHintMode.SourceBuildOnly;

                return value.Trim().ToLowerInvariant() switch
                {
                    "1" or "true" or "on" or "always" => ProgramBinaryRetrievableHintMode.Always,
                    "0" or "false" or "off" or "never" => ProgramBinaryRetrievableHintMode.Never,
                    "source" or "source-only" or "source_only" or "sourcebuild" or "source-build-only" => ProgramBinaryRetrievableHintMode.SourceBuildOnly,
                    _ => ProgramBinaryRetrievableHintMode.SourceBuildOnly,
                };
            }

            private static bool ShouldSetProgramBinaryRetrievableHintOnCreate()
                => BinaryRetrievableHintMode == ProgramBinaryRetrievableHintMode.Always;

            private static bool ShouldSetProgramBinaryRetrievableHintForSourceBuild()
                => BinaryRetrievableHintMode is ProgramBinaryRetrievableHintMode.Always or ProgramBinaryRetrievableHintMode.SourceBuildOnly;
            private const int LargeSourceSharedContextPreferenceThresholdBytes = 128 * 1024;

            /// <summary>
            /// Pre-computes the shader source hash and binary cache lookup.
            /// Safe to call from any thread. The result is consumed once by the next <see cref="Link"/> call.
            /// Saves ~2-5ms of CPU work that would otherwise block the GL thread.
            /// </summary>
            private bool TryValidateProgramBinaryLoad(uint programId, string cacheKey, GLEnum format, out string? failureReason)
            {
                int linkStatus = 0;
                MeasureRenderingProgramGlCall(
                    "glGetProgramiv(GL_LINK_STATUS)",
                    programId,
                    () => Api.GetProgram(programId, GLEnum.LinkStatus, out linkStatus),
                    $"phase=binary-load-validation cacheKey={cacheKey} binaryFormat={format}");
                if (linkStatus != 0)
                {
                    failureReason = null;
                    return true;
                }

                string? infoLog = null;
                MeasureRenderingProgramGlCall(
                    "glGetProgramInfoLog",
                    programId,
                    () => Api.GetProgramInfoLog(programId, out infoLog),
                    $"phase=binary-load-validation-log cacheKey={cacheKey} binaryFormat={format}");
                failureReason = string.IsNullOrWhiteSpace(infoLog)
                    ? $"glProgramBinary produced an unlinked program for key {cacheKey} format {format}"
                    : infoLog;
                return false;
            }

            /// <summary>
            /// Resets async link state and destroys any attached shader objects.
            /// </summary>
            private void DetachShaders(uint programId, ReadOnlySpan<uint> attachedShaderIds)
            {
                if (programId == 0 || attachedShaderIds.Length == 0)
                    return;

                HashSet<uint> detachedShaderIds = [];
                foreach (uint shaderId in attachedShaderIds)
                {
                    if (shaderId == 0 || !detachedShaderIds.Add(shaderId))
                        continue;

                    MeasureRenderingProgramGlCall(
                        "glDetachShader",
                        programId,
                        () => Api.DetachShader(programId, shaderId),
                        $"shaderId={shaderId}");
                }
            }

            private static void DetachShaders(GL api, uint programId, ReadOnlySpan<uint> attachedShaderIds)
            {
                if (programId == 0 || attachedShaderIds.Length == 0)
                    return;

                HashSet<uint> detachedShaderIds = [];
                foreach (uint shaderId in attachedShaderIds)
                {
                    if (shaderId == 0 || !detachedShaderIds.Add(shaderId))
                        continue;

                    MeasureRenderingProgramGlCallStatic(
                        "glDetachShader",
                        programId,
                        () => api.DetachShader(programId, shaderId),
                        $"shaderId={shaderId}");
                }
            }

            private static void DeleteShaders(GL api, ReadOnlySpan<uint> shaderIds)
            {
                if (shaderIds.Length == 0)
                    return;

                HashSet<uint> deletedShaderIds = [];
                foreach (uint shaderId in shaderIds)
                {
                    if (shaderId == 0 || !deletedShaderIds.Add(shaderId))
                        continue;

                    MeasureRenderingProgramGlCallStatic(
                        "glDeleteShader",
                        0,
                        () => api.DeleteShader(shaderId),
                        $"shaderId={shaderId}");
                }
            }

            private bool TryGetBuildBindingId(out uint bindingId)
            {
                if (_replacementProgramPending && _replacementProgramId != 0)
                {
                    bindingId = _replacementProgramId;
                    return true;
                }

                return TryGetBindingId(out bindingId);
            }

            private uint GetBuildBindingId()
            {
                if (_replacementProgramPending && _replacementProgramId != 0)
                    return _replacementProgramId;
                return BindingId;
            }

            private void BeginReplacementProgramBuild()
            {
                if (_replacementProgramPending)
                    return;

                _replacementProgramId = CreateConfiguredProgramHandle();
                if (_replacementProgramId == 0)
                    return;

                _replacementProgramPending = true;
                _hashComputed = false;
                InvalidatePreparedLinkData();
                PublishBackendStatus(
                    EShaderProgramBackendStage.SourceQueued,
                    "CloneAndSwap",
                    "hot-reload replacement program allocated");
                BeginPrepareLinkData();
                RegisterPendingAsyncProgram();
            }

            private void AdoptLinkedBuildProgram(uint linkedProgramId)
            {
                if (!_replacementProgramPending || linkedProgramId == 0 || linkedProgramId != _replacementProgramId)
                    return;

                uint previousProgramId = 0;
                SharedLinkedProgram? previousSharedProgram = _sharedLinkedProgram;
                _sharedLinkedProgram = null;
                if (TryGetBindingId(out uint oldProgramId))
                {
                    previousProgramId = oldProgramId;
                    RemoveCacheEntry(oldProgramId);
                }

                _bindingId = linkedProgramId;
                Cache[linkedProgramId] = this;
                _replacementProgramId = 0;
                _replacementProgramPending = false;

                ResetProgramInterfaceCaches();

                if (previousSharedProgram is not null)
                    ReleaseSharedLinkedProgram(previousSharedProgram);
                else if (previousProgramId != 0)
                    EnqueueDeferredProgramHandleDelete(Renderer, previousProgramId);
            }

            private void AbandonReplacementProgram()
            {
                if (!_replacementProgramPending)
                    return;

                uint replacementId = _replacementProgramId;
                _replacementProgramId = 0;
                _replacementProgramPending = false;
                if (replacementId != 0)
                    EnqueueDeferredProgramHandleDelete(Renderer, replacementId);
            }

            private SharedLinkedProgramKey BuildSharedLinkedProgramKey(string cacheKey)
                => new(Renderer, cacheKey);

            private bool TryAcquireSharedLinkedProgram(string cacheKey, out SharedLinkedProgram sharedProgram)
            {
                sharedProgram = null!;
                if (string.IsNullOrWhiteSpace(cacheKey))
                    return false;

                SharedLinkedProgramKey key = BuildSharedLinkedProgramKey(cacheKey);
                if (!SharedLinkedPrograms.TryGetValue(key, out SharedLinkedProgram? candidate))
                    return false;

                if (candidate.TryAddReference())
                {
                    sharedProgram = candidate;
                    ShaderProgramLifecycleDiagnostics.RecordSharedProgramReuse();
                    return true;
                }

                SharedLinkedPrograms.TryRemove(key, out _);
                return false;
            }

            private void AttachSharedLinkedProgram(SharedLinkedProgram sharedProgram)
            {
                uint previousProgramId = 0;
                if (TryGetBindingId(out uint currentProgramId))
                    previousProgramId = currentProgramId;

                SharedLinkedProgram? previousSharedProgram = _sharedLinkedProgram;
                _sharedLinkedProgram = sharedProgram;
                _bindingId = sharedProgram.ProgramId;

                if (previousProgramId != 0 && previousProgramId != sharedProgram.ProgramId)
                {
                    RemoveCacheEntry(previousProgramId);
                    if (previousSharedProgram is null)
                        EnqueueDeferredProgramHandleDelete(Renderer, previousProgramId);
                }

                if (previousSharedProgram is not null && !ReferenceEquals(previousSharedProgram, sharedProgram))
                    ReleaseSharedLinkedProgram(previousSharedProgram);

                ResetProgramInterfaceCaches();
            }

            private void RegisterCurrentLinkedProgramForSharing(string? cacheKey, GLEnum format, uint programId)
            {
                if (!SharedLinkedProgramReuseEnabled || _replacementProgramPending || string.IsNullOrWhiteSpace(cacheKey) || programId == 0)
                    return;

                if (_sharedLinkedProgram is not null && _sharedLinkedProgram.ProgramId == programId)
                    return;

                UniformMetadataEntry[] uniforms = SnapshotUniformMetadata();
                if (uniforms.Length == 0 && _cachedProgram?.Uniforms is { Length: > 0 } cachedUniforms)
                    uniforms = cachedUniforms;
                var sharedProgram = new SharedLinkedProgram(Renderer, cacheKey, programId, Hash, format, Data.Separable, uniforms, this);
                SharedLinkedProgramKey key = BuildSharedLinkedProgramKey(cacheKey);
                if (SharedLinkedPrograms.TryAdd(key, sharedProgram))
                {
                    _sharedLinkedProgram = sharedProgram;
                    ShaderProgramLifecycleDiagnostics.RecordSharedProgramCreate();
                    ShaderProgramLifecycleDiagnostics.RecordSharedProgramAttach(1);
                    LogRenderingProgramBuildEvent(
                        "BINARY_SHARED_REGISTERED",
                        _activeBuildBackend ?? "SharedProgram",
                        "linked program object registered for fingerprint reuse",
                        cacheKey,
                        programId,
                        binaryBytes: _cachedProgram?.Length ?? 0,
                        binaryFormat: format.ToString());
                    return;
                }

                if (TryAcquireSharedLinkedProgram(cacheKey, out SharedLinkedProgram existingSharedProgram))
                    AttachSharedLinkedProgram(existingSharedProgram);
            }

            private bool TryUseSharedLinkedProgram(BinaryProgram binProg)
            {
                if (!SharedLinkedProgramReuseEnabled)
                    return false;

                if (_replacementProgramPending || !TryAcquireSharedLinkedProgram(binProg.CacheKey, out SharedLinkedProgram sharedProgram))
                    return false;

                BeginBuildTelemetry("BinaryProgramShared", binProg.CacheKey);
                _cachedProgram = binProg.Uniforms is not { Length: > 0 } && sharedProgram.Uniforms.Length > 0
                    ? binProg with { Uniforms = sharedProgram.Uniforms }
                    : binProg;
                AttachSharedLinkedProgram(sharedProgram);
                IsLinked = true;

                double reflectionMilliseconds = RestoreRuntimeBindingStateAfterBinaryLoad();
                PublishBackendStatus(
                    EShaderProgramBackendStage.Ready,
                    "BinaryProgramShared",
                    "reused shared linked program object",
                    fingerprint: binProg.CacheKey);
                LogRenderingProgramBuildEvent(
                    "BINARY_SHARED_READY",
                    "BinaryProgramShared",
                    "reused shared linked program object",
                    binProg.CacheKey,
                    sharedProgram.ProgramId,
                    binaryBytes: binProg.Length,
                    binaryFormat: binProg.Format.ToString());
                RuntimeEngine.Rendering.Stats.RecordShaderVariant(linked: true, loadedFromDiskCache: true);
                CompleteBuildTelemetry(true, binaryLoadMilliseconds: 0.0, reflectionMilliseconds: reflectionMilliseconds);
                ClearCompletedBuildPendingState();
                return true;
            }

            private bool CanCompleteFromSharedBinaryCache()
            {
                if (!SharedLinkedProgramReuseEnabled ||
                    !_asyncBinaryUploadQueueWaitPending ||
                    _replacementProgramPending ||
                    _cachedProgram is not { } cachedProgram)
                    return false;

                if (string.IsNullOrWhiteSpace(cachedProgram.CacheKey))
                    return false;

                return SharedLinkedPrograms.ContainsKey(BuildSharedLinkedProgramKey(cachedProgram.CacheKey));
            }

            private bool CanCompleteFromSharedContextCompileQueue()
                => _asyncCompileLinkPending &&
                   TryGetBuildBindingId(out uint programId) &&
                   Renderer.ProgramCompileLinkQueue?.HasResult(programId) == true;

            private void ReleaseSharedLinkedProgramReference()
            {
                SharedLinkedProgram? sharedProgram = _sharedLinkedProgram;
                if (sharedProgram is null)
                    return;

                _sharedLinkedProgram = null;
                ReleaseSharedLinkedProgram(sharedProgram);
            }

            private static void ReleaseSharedLinkedProgram(SharedLinkedProgram sharedProgram)
            {
                if (!sharedProgram.ReleaseReference())
                    return;

                SharedLinkedPrograms.TryRemove(new SharedLinkedProgramKey(sharedProgram.Renderer, sharedProgram.CacheKey), out _);
                Cache.Remove(sharedProgram.ProgramId);
                if (!sharedProgram.Renderer.ShouldOrphanGLHandlesForShutdown)
                    EnqueueDeferredProgramHandleDelete(sharedProgram.Renderer, sharedProgram.ProgramId);
                ShaderProgramLifecycleDiagnostics.RecordSharedProgramDelete();
            }

            internal bool MarkSharedMaterialUniformSource(XRMaterialBase source)
            {
                bool changedForThisProgram = MarkMaterialUniformSource(source);
                bool changedForSharedHandle = _sharedLinkedProgram is not null && _sharedLinkedProgram.MarkUniformSource(source);
                return changedForThisProgram || changedForSharedHandle;
            }

            private bool MarkMaterialUniformSource(XRMaterialBase source)
            {
                ulong layoutVersion = source.BindingLayoutVersion;
                lock (_materialUniformSourceLock)
                {
                    if (ReferenceEquals(_lastMaterialUniformSource, source) &&
                        _lastMaterialUniformSourceLayoutVersion == layoutVersion)
                    {
                        return false;
                    }

                    _lastMaterialUniformSource = source;
                    _lastMaterialUniformSourceLayoutVersion = layoutVersion;
                    return true;
                }
            }

            private void EnsureProgramBinaryRetrievableHintForSourceBuild(uint programId, string backend)
            {
                if (programId == 0 || !ShouldSetProgramBinaryRetrievableHintForSourceBuild())
                    return;

                MeasureRenderingProgramGlCall(
                    "glProgramParameteri(GL_PROGRAM_BINARY_RETRIEVABLE_HINT)",
                    programId,
                    () => Api.ProgramParameter(programId, GLEnum.ProgramBinaryRetrievableHint, 1),
                    $"value=1 phase=source-build backend={backend}");
            }

            private void ResetProgramInterfaceCaches()
            {
                _attribCache.Clear();
                _uniformCache.Clear();
                _failedAttributes.Clear();
                _failedUniforms.Clear();
                _locationNameCache.Clear();
                _uniformMetadata.Clear();
                _activeSamplerUniforms = [];
                _activeEngineUniformRequirements = EUniformRequirements.None;
                _explicitAttributeLocations.Clear();
                _explicitAttributeLocationsResolved = false;
            }

        }
    }
}
