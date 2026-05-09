using XREngine.Extensions;
using Silk.NET.OpenGL;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using XREngine;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLRenderProgram
        {
            private readonly ConcurrentDictionary<XRShader, GLShader> _shaderCache = [];

            private void ShaderRemoved(XRShader item)
            {
                if (!_shaderCache.TryRemove(item, out var shader) || shader is null)
                    return;

                shader.Destroy();
                ShaderUncached(shader);
            }

            private void ShaderAdded(XRShader item)
            {
                _shaderCache.TryAdd(item, GetAndGenerate(item));
            }
            private GLShader GetAndGenerate(XRShader data)
            {
                GLShader shader = Renderer.GenericToAPI<GLShader>(data)!;
                //Engine.EnqueueMainThreadTask(shader.Generate);
                ShaderCached(shader);
                return shader;
            }

            private void ShaderCached(GLShader shader)
            {
                shader.ActivePrograms.Add(this);
                shader.SourceChanged += Value_SourceChanged;
            }

            private void ShaderUncached(GLShader shader)
            {
                shader.ActivePrograms.Remove(this);
                shader.SourceChanged -= Value_SourceChanged;
            }

            protected override void LinkData()
            {
                //data.UniformLocationRequested = GetUniformLocation;

                Data.UniformSetVector2Requested += Uniform;
                Data.UniformSetVector3Requested += Uniform;
                Data.UniformSetVector4Requested += Uniform;
                Data.UniformSetQuaternionRequested += Uniform;
                Data.UniformSetIntRequested += Uniform;
                Data.UniformSetFloatRequested += Uniform;
                Data.UniformSetUIntRequested += Uniform;
                Data.UniformSetDoubleRequested += Uniform;
                Data.UniformSetMatrix4x4Requested += Uniform;

                Data.UniformSetVector2ArrayRequested += Uniform;
                Data.UniformSetVector3ArrayRequested += Uniform;
                Data.UniformSetVector4ArrayRequested += Uniform;
                Data.UniformSetQuaternionArrayRequested += Uniform;
                Data.UniformSetIntArrayRequested += Uniform;
                Data.UniformSetFloatArrayRequested += Uniform;
                Data.UniformSetFloatSpanRequested += Uniform;
                Data.UniformSetUIntArrayRequested += Uniform;
                Data.UniformSetDoubleArrayRequested += Uniform;
                Data.UniformSetMatrix4x4ArrayRequested += Uniform;

                Data.UniformSetIVector2Requested += Uniform;
                Data.UniformSetIVector3Requested += Uniform;
                Data.UniformSetIVector4Requested += Uniform;
                Data.UniformSetIVector2ArrayRequested += Uniform;
                Data.UniformSetIVector3ArrayRequested += Uniform;
                Data.UniformSetIVector4ArrayRequested += Uniform;

                Data.UniformSetBoolRequested += Uniform;
                Data.UniformSetBoolArrayRequested += Uniform;
                
                Data.UniformSetBoolVector2Requested += Uniform;
                Data.UniformSetBoolVector3Requested += Uniform;
                Data.UniformSetBoolVector4Requested += Uniform;

                Data.UniformSetBoolVector2ArrayRequested += Uniform;
                Data.UniformSetBoolVector3ArrayRequested += Uniform;
                Data.UniformSetBoolVector4ArrayRequested += Uniform;

                Data.SamplerRequested += Sampler;
                Data.SamplerRequestedByLocation += Sampler;
                Data.SuppressFallbackSamplerWarningRequested += SuppressFallbackSamplerWarning;
                Data.BindImageTextureRequested += BindImageTexture;
                Data.DispatchComputeRequested += DispatchCompute;
                Data.BindBufferRequested += BindBuffer;

                Data.LinkRequested += LinkRequested;
                Data.UseRequested += UseRequested;

                foreach (XRShader shader in Data.Shaders)
                    ShaderAdded(shader);
                Data.Shaders.PostAnythingAdded += ShaderAdded;
                Data.Shaders.PostAnythingRemoved += ShaderRemoved;
            }

            private void UseRequested(XRRenderProgram program)
            {
                if (Engine.InvokeOnMainThread(() => UseRequested(program), "GLRenderProgram.UseRequested"))
                    return;

                if (!IsLinked)
                {
                    if (!Data.LinkReady || !Link())
                        return;
                }

                Api.UseProgram(BindingId);
            }

            private void LinkRequested(XRRenderProgram program)
            {
                if (Engine.InvokeOnMainThread(() => LinkRequested(program), "GLRenderProgram.LinkRequested"))
                    return;

                if (!Link())
                {
                    //Debug.LogWarning($"Failed to link program {Data.Name} with hash {Hash}.");
                }
            }

            private void BindBuffer(uint index, XRDataBuffer buffer)
            {
                var glObj = Renderer.GetOrCreateAPIRenderObject(buffer, generateNow: true);
                if (glObj is not GLDataBuffer glBuf)
                    return;

                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, index, glBuf.BindingId);
            }

            private void BindImageTexture(uint unit, IRenderTextureResource texture, int level, bool layered, int layer, EImageAccess access, EImageFormat format)
            {
                if (texture is not XRTexture xrTexture)
                    return;

                var glObj = Renderer.GetOrCreateAPIRenderObject(xrTexture);
                if (glObj is not IGLTexture glTex)
                    return;
                Api.BindImageTexture(unit, glTex.BindingId, level, layered, layer, ToGLEnum(access), ToGLEnum(format));
            }

            private static GLEnum ToGLEnum(EImageFormat format) => format switch
            {
                EImageFormat.R8 => GLEnum.R8,
                EImageFormat.R16 => GLEnum.R16,
                EImageFormat.R16F => GLEnum.R16f,
                EImageFormat.R32F => GLEnum.R32f,
                EImageFormat.RG8 => GLEnum.RG8,
                EImageFormat.RG16 => GLEnum.RG16,
                EImageFormat.RG16F => GLEnum.RG16f,
                EImageFormat.RG32F => GLEnum.RG32f,
                EImageFormat.RGB8 => GLEnum.Rgb8,
                EImageFormat.RGB16 => GLEnum.Rgb16,
                EImageFormat.RGB16F => GLEnum.Rgb16f,
                EImageFormat.RGB32F => GLEnum.Rgb32f,
                EImageFormat.RGBA8 => GLEnum.Rgba8,
                EImageFormat.RGBA16 => GLEnum.Rgba16,
                EImageFormat.RGBA16F => GLEnum.Rgba16f,
                EImageFormat.RGBA32F => GLEnum.Rgba32f,
                EImageFormat.R8I => GLEnum.R8i,
                EImageFormat.R8UI => GLEnum.R8ui,
                EImageFormat.R16I => GLEnum.R16i,
                EImageFormat.R16UI => GLEnum.R16ui,
                EImageFormat.R32I => GLEnum.R32i,
                EImageFormat.R32UI => GLEnum.R32ui,
                EImageFormat.RG8I => GLEnum.RG8i,
                EImageFormat.RG8UI => GLEnum.RG8ui,
                EImageFormat.RG16I => GLEnum.RG16i,
                EImageFormat.RG16UI => GLEnum.RG16ui,
                EImageFormat.RG32I => GLEnum.RG32i,
                EImageFormat.RG32UI => GLEnum.RG32ui,
                EImageFormat.RGB8I => GLEnum.Rgb8i,
                EImageFormat.RGB8UI => GLEnum.Rgb8ui,
                EImageFormat.RGB16I => GLEnum.Rgb16i,
                EImageFormat.RGB16UI => GLEnum.Rgb16ui,
                EImageFormat.RGB32I => GLEnum.Rgb32i,
                EImageFormat.RGB32UI => GLEnum.Rgb32ui,
                EImageFormat.RGBA8I => GLEnum.Rgba8i,
                EImageFormat.RGBA8UI => GLEnum.Rgba8ui,
                EImageFormat.RGBA16I => GLEnum.Rgba16i,
                EImageFormat.RGBA16UI => GLEnum.Rgba16ui,
                EImageFormat.RGBA32I => GLEnum.Rgba32i,
                EImageFormat.RGBA32UI => GLEnum.Rgba32ui,
                _ => GLEnum.Rgba32f,
            };

            private static GLEnum ToGLEnum(EImageAccess access) => access switch
            {
                EImageAccess.ReadOnly => GLEnum.ReadOnly,
                EImageAccess.WriteOnly => GLEnum.WriteOnly,
                EImageAccess.ReadWrite => GLEnum.ReadWrite,
                _ => GLEnum.ReadWrite,
            };

            private void DispatchCompute(
                uint x,
                uint y,
                uint z,
                IEnumerable<(uint unit, IRenderTextureResource texture, int level, int? layer, EImageAccess access, EImageFormat format)>? textures = null)
            {
                if (!IsLinked)
                {
                    if (Data.LinkReady)
                    {
                        if (!Link())
                        {
                            //Debug.LogWarning($"Failed to link program {Data.Name} with hash {Hash}.");
                            return;
                        }
                    }
                    else
                    {
                        Debug.OpenGLWarning("Cannot dispatch compute shader, program is not linked.");
                        return;
                    }
                }
                Api.UseProgram(BindingId);
                if (textures is not null)
                    foreach (var (unit, texture, level, layer, access, format) in textures)
                        BindImageTexture(unit, texture, level, layer.HasValue, layer ?? 0, access, format);
                Api.DispatchCompute(x, y, z);
            }

            protected override void UnlinkData()
            {
                Data.UniformSetVector2Requested -= Uniform;
                Data.UniformSetVector3Requested -= Uniform;
                Data.UniformSetVector4Requested -= Uniform;
                Data.UniformSetQuaternionRequested -= Uniform;
                Data.UniformSetIntRequested -= Uniform;
                Data.UniformSetFloatRequested -= Uniform;
                Data.UniformSetUIntRequested -= Uniform;
                Data.UniformSetDoubleRequested -= Uniform;
                Data.UniformSetMatrix4x4Requested -= Uniform;

                Data.UniformSetVector2ArrayRequested -= Uniform;
                Data.UniformSetVector3ArrayRequested -= Uniform;
                Data.UniformSetVector4ArrayRequested -= Uniform;
                Data.UniformSetQuaternionArrayRequested -= Uniform;
                Data.UniformSetIntArrayRequested -= Uniform;
                Data.UniformSetFloatArrayRequested -= Uniform;
                Data.UniformSetFloatSpanRequested -= Uniform;
                Data.UniformSetUIntArrayRequested -= Uniform;
                Data.UniformSetDoubleArrayRequested -= Uniform;
                Data.UniformSetMatrix4x4ArrayRequested -= Uniform;

                Data.UniformSetIVector2Requested -= Uniform;
                Data.UniformSetIVector3Requested -= Uniform;
                Data.UniformSetIVector4Requested -= Uniform;
                Data.UniformSetIVector2ArrayRequested -= Uniform;
                Data.UniformSetIVector3ArrayRequested -= Uniform;
                Data.UniformSetIVector4ArrayRequested -= Uniform;

                //Data.UniformSetUVector2Requested -= Uniform;
                //Data.UniformSetUVector3Requested -= Uniform;
                //Data.UniformSetUVector4Requested -= Uniform;

                Data.UniformSetBoolRequested -= Uniform;
                Data.UniformSetBoolArrayRequested -= Uniform;

                Data.UniformSetBoolVector2Requested -= Uniform;
                Data.UniformSetBoolVector3Requested -= Uniform;
                Data.UniformSetBoolVector4Requested -= Uniform;

                Data.UniformSetBoolVector2ArrayRequested -= Uniform;
                Data.UniformSetBoolVector3ArrayRequested -= Uniform;
                Data.UniformSetBoolVector4ArrayRequested -= Uniform;

                Data.SamplerRequested -= Sampler;
                Data.SamplerRequestedByLocation -= Sampler;
                Data.SuppressFallbackSamplerWarningRequested -= SuppressFallbackSamplerWarning;
                Data.BindImageTextureRequested -= BindImageTexture;
                Data.DispatchComputeRequested -= DispatchCompute;
                Data.BindBufferRequested -= BindBuffer;

                Data.LinkRequested -= LinkRequested;
                Data.UseRequested -= UseRequested;

                Data.Shaders.PostAnythingAdded -= ShaderAdded;
                Data.Shaders.PostAnythingRemoved -= ShaderRemoved;
                foreach (XRShader shader in Data.Shaders)
                    ShaderRemoved(shader);
            }

            public ulong Hash { get; private set; }
            private BinaryProgram? _cachedProgram = null;
            private SharedLinkedProgram? _sharedLinkedProgram;

            private readonly record struct SharedLinkedProgramKey(OpenGLRenderer Renderer, string CacheKey);

            private sealed class SharedLinkedProgram(OpenGLRenderer renderer, string cacheKey, uint programId, ulong hash, GLEnum format, bool separable, UniformMetadataEntry[] uniforms)
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

                public bool TryAddReference()
                {
                    lock (_lock)
                    {
                        if (_deleteQueued)
                            return false;

                        _referenceCount++;
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
            private const double AsyncShaderSlowWarningSeconds = 2.0;
            // After this long without COMPLETION_STATUS_ARB == 1, issue a single glFlush()
            // to nudge the driver in case its parallel-link worker is starved on a missing fence.
            private const double AsyncShaderStuckFlushSeconds = 5.0;
            // After this long the link is treated as failed and cleaned up. We deliberately do
            // NOT call glGetProgramiv(GL_LINK_STATUS) on a still-pending program because that
            // call is documented to implicitly wait for completion and is known to hang
            // indefinitely on NVIDIA's threaded driver when the parallel-link worker is stuck.
            private const double AsyncShaderHardAbandonSeconds = 30.0;
            private const double SlowLinkPreparationWarningMilliseconds = 25.0;
            private const double SlowShaderLinkSourceDumpMilliseconds = 500.0;
            private static readonly ProgramBinaryRetrievableHintMode BinaryRetrievableHintMode = ResolveBinaryRetrievableHintMode();

            private enum ProgramBinaryRetrievableHintMode : byte
            {
                Always,
                SourceBuildOnly,
                Never,
            }

            private static ProgramBinaryRetrievableHintMode ResolveBinaryRetrievableHintMode()
            {
                string? value = Environment.GetEnvironmentVariable("XRE_PROGRAM_BINARY_RETRIEVABLE_HINT");
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

            /// <summary>
            /// Pre-computes the shader source hash and binary cache lookup.
            /// Safe to call from any thread. The result is consumed once by the next <see cref="Link"/> call.
            /// Saves ~2-5ms of CPU work that would otherwise block the GL thread.
            /// </summary>
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
                using (Engine.Profiler.Start("GLRenderProgram.Link.CacheLookup", ProfilerScopeKind.OneOffInvoke))
                {
                    using (Engine.Profiler.Start("GLRenderProgram.Link.CalcHash", ProfilerScopeKind.OneOffInvoke))
                        hash = CalcShaderSourceHash();

                    bool bypassBinaryCache = ShouldBypassBinaryCacheForLiveUberVariant();
                    if (!bypassBinaryCache && Engine.Rendering.Settings.AllowBinaryProgramCaching)
                    {
                        cacheKey = BuildBinaryCacheKey(hash);
                        using (Engine.Profiler.Start("GLRenderProgram.Link.BinaryCacheLookup", ProfilerScopeKind.OneOffInvoke))
                            isCached = BinaryCache?.TryGetValue(cacheKey, out binProg) ?? false;
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
                    Debug.OpenGLWarning($"[ShaderCache] Slow link preparation: hash={hash}, shaderCount={_shaderCache.Count}, cached={isCached}, compileInputs={compileInputs?.Length ?? 0}, elapsedMs={preparationMilliseconds:F2}.");
                    ShaderProgramLifecycleDiagnostics.RecordSlowLinkPreparation();
                }
            }

            public void BeginPrepareLinkData()
            {
                if (_linkDataPrepared || IsLinked || _shaderCache.IsEmpty)
                    return;

                int generation = Volatile.Read(ref _linkPreparationGeneration);
                if (Volatile.Read(ref _linkPreparationPendingGeneration) == generation)
                    return;

                if (!Engine.IsRenderThread)
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

            private bool IsLinkPreparationPending
                => Volatile.Read(ref _linkPreparationPendingGeneration) == Volatile.Read(ref _linkPreparationGeneration);

            private bool ShouldDeferLinkPreparationOnRenderThread()
            {
                if (!Engine.IsRenderThread || _linkDataPrepared || IsLinkPreparationPending || _shaderCache.IsEmpty)
                    return false;

                if (Renderer.ProgramCompileLinkQueue is { IsAvailable: true })
                    return true;

                if (Renderer.UseDriverParallelShaderCompile)
                    return true;

                return Engine.Rendering.Settings.AsyncProgramBinaryUpload &&
                       Renderer.ProgramBinaryUploadQueue is { IsAvailable: true };
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

            private bool HasPendingAsyncWork
                => IsLinkPreparationPending ||
                   _linkDataPrepared ||
                   _replacementProgramPending ||
                   _asyncBinaryUploadPending ||
                   _asyncCompileLinkPending ||
                   _asyncCompileLinkQueueWaitPending ||
                   _asyncCompileDuplicateHashWaitPending ||
                   _asyncLinkPhase != EAsyncLinkPhase.Idle;

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
                Debug.OpenGLWarning($"[ShaderAsync] Program '{Data.Name ?? "<unnamed>"}' hash={Hash} phase={phase} still pending after {elapsedSeconds:F2}s; continuing non-blocking poll.");
            }

            // Soft time budget for synchronous link work performed inline by
            // PollPendingAsyncPrograms. When the shared-context compile/link queue is
            // unavailable and pending programs are forced down the synchronous /
            // hazard-sync-link path, glLinkProgram runs on the render thread and a
            // backlog can accumulate (e.g. on first frame after model import or
            // window resize). This budget caps how much render-thread time we spend
            // chewing through that backlog per frame so any single frame stall stays
            // bounded; remaining programs are picked up next frame.
            private const double PollPendingAsyncProgramsSyncBudgetMilliseconds = 4.0;

            internal static void PollPendingAsyncPrograms(int maxPrograms)
            {
                int remaining = Math.Max(1, maxPrograms);
                long frameStartTicks = Stopwatch.GetTimestamp();
                double budgetMs = PollPendingAsyncProgramsSyncBudgetMilliseconds;
                foreach (GLRenderProgram program in PendingAsyncPrograms.Keys)
                {
                    if (remaining-- <= 0)
                        break;

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
                    bool ranSyncWork = !program.HasPendingAsyncWork
                        || program._asyncLinkPhase == EAsyncLinkPhase.Idle;
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

            private static void ProcessDeferredProgramHandleDeletes(int maxPrograms)
            {
                int remaining = Math.Max(1, maxPrograms);
                while (remaining-- > 0 && DeferredProgramHandleDeletes.TryDequeue(out var delete))
                {
                    if (Engine.Rendering.State.RenderFrameId < delete.EarliestFrameId)
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
                if (programId == 0)
                    return;

                DeferredProgramHandleDeletes.Enqueue(new DeferredProgramHandleDelete(
                    renderer,
                    programId,
                    Engine.Rendering.State.RenderFrameId + 2UL));
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
                        using var prof = Engine.Profiler.Start("GLRenderProgram.ContinueAsyncLink.PollCompile", ProfilerScopeKind.ConditionalLoop);

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

                        // All shaders compiled — attach and dispatch link
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
                        using var prof = Engine.Profiler.Start("GLRenderProgram.ContinueAsyncLink.PollLink", ProfilerScopeKind.ConditionalLoop);

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
            /// <item>After <see cref="AsyncShaderHardAbandonSeconds"/>, treat the
            /// link as failed, mark the hash as failed, and clean up. We deliberately
            /// do NOT call <c>glGetProgramiv(GL_LINK_STATUS)</c> on a still-pending
            /// program because that call implicitly waits for completion and is
            /// known to hang indefinitely when the driver's link worker is stuck.</item>
            /// </list>
            /// Returns true when the program has been force-failed (caller should stop polling).
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
                        $"[ShaderAsync] Program '{Data.Name ?? "<unnamed>"}' hash={Hash} still reports COMPLETION_STATUS=false " +
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
                    $"[ShaderAsync] Program '{Data.Name ?? "<unnamed>"}' hash={Hash} stuck with COMPLETION_STATUS=false " +
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
            /// leaked into the driver — recovery from a driver-side hang is more
            /// important than reclaiming a few handles.
            /// </summary>
            private void AbandonStuckAsyncLink(uint linkedProgramId)
            {
                Debug.OpenGLWarning(
                    $"Abandoning async link for program '{Data.Name ?? "<unnamed>"}' hash={Hash}: programId={linkedProgramId} " +
                    $"and {(_asyncAttachedShaderIds?.Length ?? 0)} shader(s) leaked to avoid blocking GL calls.");

                MarkHashFailed("Async link timed out (driver never reported completion).");
                CompleteUberBackendTracking(false, "Async link timed out (driver never reported completion).");
                PublishBackendStatus(
                    EShaderProgramBackendStage.Abandoned,
                    _activeBuildBackend ?? "DriverParallelSource",
                    "driver never reported completion",
                    "Async link timed out.");
                CompleteBuildTelemetry(false, failureReason: "Async link timed out.");

                InFlightCompilations.TryRemove(Hash, out _);

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
                    CompleteBuildTelemetry(false, compileMilliseconds, linkMilliseconds, failureReason: linkError);
                    MarkHashFailed(linkError);
                }

                CompleteUberBackendTracking(linked, linkError, compileMilliseconds, linkMilliseconds);
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

            private void ReleaseAsyncLinkState()
            {
                if (Hash != 0)
                    InFlightCompilations.TryRemove(Hash, out _);

                if (!TryDeferAsyncLinkCleanupForDestroy())
                    CleanupAsyncLink();
                _asyncBinaryUploadPending = false;
                _asyncCompileLinkPending = false;
                _asyncCompileLinkQueueWaitPending = false;
                AbandonReplacementProgram();
                ResetUberBackendTracking();
                UnregisterPendingAsyncProgram();
            }

            private bool TryResolveUberVariantHash(out ulong variantHash)
            {
                if (_preparedCompileInputs is { Length: > 0 } preparedInputs && TryResolveUberVariantHash(preparedInputs, out variantHash))
                    return true;

                foreach (GLShader shader in _shaderCache.Values)
                {
                    if (UberShaderVariantTelemetry.TryParseVariantHash(shader.Data.Source?.Text, out variantHash))
                        return true;
                }

                variantHash = 0;
                return false;
            }

            private bool ShouldBypassBinaryCacheForLiveUberVariant()
                => Renderer.UseDriverParallelShaderCompile &&
                   !IsKnownAsyncLinkHazard &&
                   TryResolveUberVariantHash(out _);

            /// <summary>
            /// Programs known to hang or stall NVIDIA's
            /// <c>GL_ARB_parallel_shader_compile</c> link worker on the main
            /// context. Covers:
            ///  * Single-stage separable programs (imported model materials whose
            ///    vertex/fragment stages are split into individual programs).
            ///  * Compute programs (always single-stage; NVIDIA's parallel-link
            ///    worker can leave the program waiting forever, and the first
            ///    <c>glUseProgram</c>/<c>glDispatchCompute</c> implicitly waits for
            ///    completion which deadlocks the render thread — observed during
            ///    BVH/physics-chain dispatch in <c>GlobalPreRender</c>).
            ///  * Any program with a single attached shader, which exhibits the
            ///    same hazard regardless of the <c>Separable</c> flag.
            /// For these we always bypass the driver-parallel lane. Single-stage
            /// graphics programs are still routed to the shared-context source
            /// lane (when available) so their cold link runs on a worker thread
            /// on a separate GL context instead of stalling the render thread.
            /// Compute programs are denied the shared-context lane as well — the
            /// queue's <c>ContainsKnownAsyncLinkHazard</c> filter still rejects
            /// them — and fall back to the guarded synchronous path which
            /// temporarily disables driver compiler threads, links inline on the
            /// render thread under the per-frame shader-work budget, and leaves
            /// any previously linked hot-reload program visible.
            /// </summary>
            private bool IsKnownAsyncLinkHazard
            {
                get
                {
                    if (_shaderCache.Count <= 1)
                        return true;
                    foreach (GLShader shader in _shaderCache.Values)
                    {
                        if (shader.Data.Type == EShaderType.Compute)
                            return true;
                    }
                    return false;
                }
            }

            private static bool TryResolveUberVariantHash(IEnumerable<GLProgramCompileLinkQueue.ShaderInput> inputs, out ulong variantHash)
            {
                foreach (GLProgramCompileLinkQueue.ShaderInput input in inputs)
                {
                    if (UberShaderVariantTelemetry.TryParseVariantHash(input.ResolvedSource, out variantHash))
                        return true;
                }

                variantHash = 0;
                return false;
            }

            private void BeginUberBackendCompileTracking(ulong variantHash)
            {
                if (variantHash == 0)
                    return;

                _uberVariantHash = variantHash;
                _uberCompileMilliseconds = 0.0;
                _uberCompileStartTimestamp = Stopwatch.GetTimestamp();
                _uberLinkStartTimestamp = 0;
                UberShaderVariantTelemetry.RecordBackendCompileStarted(variantHash);
            }

            private double CompleteUberBackendCompileTracking()
            {
                if (_uberVariantHash == 0 || _uberCompileStartTimestamp == 0)
                    return _uberCompileMilliseconds;

                _uberCompileMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - _uberCompileStartTimestamp);
                _uberCompileStartTimestamp = 0;
                return _uberCompileMilliseconds;
            }

            private void BeginUberBackendLinkTracking(double compileMilliseconds)
            {
                if (_uberVariantHash == 0)
                    return;

                _uberCompileMilliseconds = compileMilliseconds > 0.0 ? compileMilliseconds : _uberCompileMilliseconds;
                _uberLinkStartTimestamp = Stopwatch.GetTimestamp();
                UberShaderVariantTelemetry.RecordBackendLinkStarted(_uberVariantHash, _uberCompileMilliseconds);
            }

            private void CompleteUberBackendTracking(bool linked, string? failureReason = null, double? compileMilliseconds = null, double? linkMilliseconds = null)
            {
                if (_uberVariantHash == 0)
                    return;

                double resolvedCompileMilliseconds = compileMilliseconds ?? _uberCompileMilliseconds;
                double resolvedLinkMilliseconds = linkMilliseconds ??
                    (_uberLinkStartTimestamp == 0 ? 0.0 : StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - _uberLinkStartTimestamp));

                if (linked)
                    UberShaderVariantTelemetry.RecordBackendSuccess(_uberVariantHash, resolvedCompileMilliseconds, resolvedLinkMilliseconds);
                else
                    UberShaderVariantTelemetry.RecordBackendFailure(_uberVariantHash, failureReason, resolvedCompileMilliseconds, resolvedLinkMilliseconds);

                ResetUberBackendTracking();
            }

            private void ResetUberBackendTracking()
            {
                _uberVariantHash = 0;
                _uberCompileStartTimestamp = 0;
                _uberLinkStartTimestamp = 0;
                _uberCompileMilliseconds = 0.0;
            }

            private void PublishBackendStatus(
                EShaderProgramBackendStage stage,
                string? backend,
                string? detail = null,
                string? failureReason = null,
                double compileMilliseconds = 0.0,
                double linkMilliseconds = 0.0,
                string? fingerprint = null)
            {
                Data.SetShaderBackendStatus(new ShaderProgramBackendStatus(
                    stage,
                    compileMilliseconds,
                    linkMilliseconds,
                    failureReason,
                    backend,
                    detail,
                    fingerprint ?? _activeBuildFingerprint));
            }

            private void BeginBuildTelemetry(string backend, string? fingerprint)
            {
                _activeBuildBackend = backend;
                _activeBuildFingerprint = fingerprint;
                _activeBuildQueueTimestamp = Stopwatch.GetTimestamp();
            }

            private void CompleteBuildTelemetry(
                bool success,
                double compileMilliseconds = 0.0,
                double linkMilliseconds = 0.0,
                double binaryLoadMilliseconds = 0.0,
                double reflectionMilliseconds = 0.0,
                string? failureReason = null)
            {
                double queueLatencyMilliseconds = _activeBuildQueueTimestamp == 0
                    ? 0.0
                    : StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - _activeBuildQueueTimestamp);

                Data.SetShaderBuildTelemetry(new ShaderProgramBuildTelemetry(
                    Data.Name,
                    _activeBuildFingerprint,
                    GetShaderStageTopology(),
                    Data.Separable,
                    _activeBuildBackend,
                    queueLatencyMilliseconds,
                    compileMilliseconds,
                    linkMilliseconds,
                    binaryLoadMilliseconds,
                    reflectionMilliseconds,
                    failureReason));

                string result = success ? "READY" : "FAILED";
                Debug.OpenGL(
                    $"[ShaderBackend] {result} program='{Data.Name ?? "<unnamed>"}' hash={Hash} " +
                    $"backend={_activeBuildBackend ?? "<unknown>"} fingerprint={_activeBuildFingerprint ?? "<none>"} " +
                    $"queueMs={queueLatencyMilliseconds:F2} compileMs={compileMilliseconds:F2} linkMs={linkMilliseconds:F2} " +
                    $"binaryMs={binaryLoadMilliseconds:F2} reflectionMs={reflectionMilliseconds:F2}" +
                    (string.IsNullOrWhiteSpace(failureReason) ? "." : $" failure='{failureReason}'."));
                LogSlowLinkShaderSources(linkMilliseconds, result, failureReason);
                if (ShouldLogRenderingShaderLinkVerbose())
                {
                    ShaderProgramSourceSummary sourceSummary = CollectShaderProgramSourceSummary(_preparedCompileInputs);
                    Debug.Rendering(
                        EOutputVerbosity.Verbose,
                        false,
                        "[ShaderBackend] {0} program='{1}' hash={2} backend={3} fingerprint={4} separable={5} hazard={6} shaderCount={7} shaderTypes={8} sourceBytes={9} sourceLines={10} queueMs={11:F2} compileMs={12:F2} linkMs={13:F2} binaryMs={14:F2} reflectionMs={15:F2}{16}.",
                        result,
                        Data.Name ?? "<unnamed>",
                        Hash,
                        _activeBuildBackend ?? "<unknown>",
                        _activeBuildFingerprint ?? "<none>",
                        Data.Separable,
                        IsKnownAsyncLinkHazard,
                        sourceSummary.ShaderCount,
                        sourceSummary.StageList,
                        sourceSummary.SourceBytes,
                        sourceSummary.SourceLines,
                        queueLatencyMilliseconds,
                        compileMilliseconds,
                        linkMilliseconds,
                        binaryLoadMilliseconds,
                        reflectionMilliseconds,
                        FormatRenderingDetail(string.IsNullOrWhiteSpace(failureReason) ? null : $"failure={failureReason}"));
                }

                _activeBuildBackend = null;
                _activeBuildFingerprint = null;
                _activeBuildQueueTimestamp = 0;
            }

            private void LogSlowLinkShaderSources(double linkMilliseconds, string result, string? failureReason)
            {
                if (linkMilliseconds <= SlowShaderLinkSourceDumpMilliseconds)
                    return;

                GLProgramCompileLinkQueue.ShaderInput[]? inputs = _preparedCompileInputs;
                string programName = Data.Name ?? "<unnamed>";
                string backend = _activeBuildBackend ?? "<unknown>";
                string fingerprint = _activeBuildFingerprint ?? "<none>";
                int shaderCount = inputs is { Length: > 0 } ? inputs.Length : Data.Shaders.Count;
                Debug.OpenGL(
                    $"[ShaderSourceDump] BEGIN reason=slow-link thresholdMs={SlowShaderLinkSourceDumpMilliseconds:F2} " +
                    $"linkMs={linkMilliseconds:F2} result={result} program='{programName}' hash={Hash} backend={backend} " +
                    $"fingerprint={fingerprint} separable={Data.Separable} hazard={IsKnownAsyncLinkHazard} shaderCount={shaderCount}" +
                    (string.IsNullOrWhiteSpace(failureReason) ? "." : $" failure='{failureReason}'."));

                if (inputs is { Length: > 0 })
                {
                    for (int i = 0; i < inputs.Length; i++)
                        LogSlowLinkShaderSource(i, inputs.Length, inputs[i].Type.ToString(), inputs[i].ResolvedSource);
                }
                else
                {
                    for (int i = 0; i < Data.Shaders.Count; i++)
                    {
                        XRShader shaderData = Data.Shaders[i];
                        string? source = ResolveShaderSourceForDump(shaderData);
                        LogSlowLinkShaderSource(i, Data.Shaders.Count, shaderData.Type.ToString(), source);
                    }
                }

                Debug.OpenGL($"[ShaderSourceDump] END reason=slow-link program='{programName}' hash={Hash}.");
            }

            private void LogSlowLinkShaderSource(int index, int count, string stage, string? source)
            {
                source ??= string.Empty;
                Debug.OpenGL(
                    $"[ShaderSourceDump] SOURCE_BEGIN index={index} count={count} stage={stage} " +
                    $"bytes={CountUtf8Bytes(source)} lines={CountLines(source)}{Environment.NewLine}" +
                    source +
                    $"{Environment.NewLine}[ShaderSourceDump] SOURCE_END index={index} stage={stage}.");
            }

            private string? ResolveShaderSourceForDump(XRShader shaderData)
            {
                if (_shaderCache.TryGetValue(shaderData, out GLShader? shader) && shader is not null)
                    return shader.ResolveFullSource();

                return shaderData.TryGetResolvedSource(out string resolvedSource, logFailures: false)
                    ? GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(resolvedSource, shaderData.Type, Data.Separable)
                    : null;
            }

            private readonly record struct ShaderProgramSourceSummary(
                int ShaderCount,
                long SourceBytes,
                int SourceLines,
                string StageList);

            private void LogRenderingProgramBuildEvent(
                string eventName,
                string? backend,
                string? detail = null,
                string? fingerprint = null,
                uint programId = 0,
                GLProgramCompileLinkQueue.ShaderInput[]? inputs = null,
                long binaryBytes = 0,
                string? binaryFormat = null)
            {
                if (!ShouldLogRenderingShaderLinkVerbose())
                    return;

                if (programId == 0 && !TryGetBuildBindingId(out programId))
                    programId = 0;

                ShaderProgramSourceSummary sourceSummary = CollectShaderProgramSourceSummary(inputs);
                Debug.Rendering(
                    EOutputVerbosity.Verbose,
                    false,
                    "[ShaderLink] {0} program='{1}' hash={2} programId={3} backend={4} separable={5} hazard={6} shaderCount={7} shaderTypes={8} sourceBytes={9} sourceLines={10} binaryBytes={11} binaryFormat={12} fingerprint={13} frame={14} renderThread={15}{16}.",
                    eventName,
                    Data.Name ?? "<unnamed>",
                    Hash,
                    programId,
                    backend ?? "<unknown>",
                    Data.Separable,
                    IsKnownAsyncLinkHazard,
                    sourceSummary.ShaderCount,
                    sourceSummary.StageList,
                    sourceSummary.SourceBytes,
                    sourceSummary.SourceLines,
                    binaryBytes,
                    binaryFormat ?? "<none>",
                    fingerprint ?? _activeBuildFingerprint ?? "<none>",
                    Engine.Rendering.State.RenderFrameId,
                    Engine.IsRenderThread,
                    FormatRenderingDetail(detail));
            }

            private ShaderProgramSourceSummary CollectShaderProgramSourceSummary(GLProgramCompileLinkQueue.ShaderInput[]? inputs = null)
            {
                if (inputs is { Length: > 0 })
                {
                    long inputBytes = 0;
                    int inputLines = 0;
                    var inputStages = new StringBuilder(inputs.Length * 16);
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        if (i > 0)
                            inputStages.Append('|');

                        inputStages.Append(inputs[i].Type);
                        inputBytes += CountUtf8Bytes(inputs[i].ResolvedSource);
                        inputLines += CountLines(inputs[i].ResolvedSource);
                    }

                    return new ShaderProgramSourceSummary(inputs.Length, inputBytes, inputLines, inputStages.ToString());
                }

                int shaderCount = Data.Shaders.Count;
                if (shaderCount == 0)
                    return new ShaderProgramSourceSummary(0, 0, 0, "<none>");

                long bytes = 0;
                int lines = 0;
                var stages = new StringBuilder(shaderCount * 16);
                for (int index = 0; index < shaderCount; index++)
                {
                    if (index > 0)
                        stages.Append('|');

                    XRShader shaderData = Data.Shaders[index];
                    stages.Append(shaderData.Type);
                    string? source = null;
                    if (_shaderCache.TryGetValue(shaderData, out GLShader? shader) && shader is not null)
                    {
                        source = shader.ResolveFullSource();
                    }
                    else if (shaderData.TryGetResolvedSource(out string resolvedSource, logFailures: false))
                    {
                        source = GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(resolvedSource, shaderData.Type, Data.Separable);
                    }

                    bytes += CountUtf8Bytes(source);
                    lines += CountLines(source);
                }

                return new ShaderProgramSourceSummary(shaderCount, bytes, lines, stages.ToString());
            }

            private double MeasureRenderingProgramGlCall(string callName, uint programId, Action action, string? detail = null)
            {
                if (!ShouldLogRenderingShaderLinkVerbose())
                {
                    action();
                    return 0.0;
                }

                long startTimestamp = Stopwatch.GetTimestamp();
                action();
                double elapsedMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - startTimestamp);
                LogRenderingProgramGlCall(callName, programId, elapsedMilliseconds, detail);
                return elapsedMilliseconds;
            }

            private void LogRenderingProgramGlCall(string callName, uint programId, double elapsedMilliseconds, string? detail = null)
            {
                bool renderThread = Engine.IsRenderThread;
                Debug.Rendering(
                    EOutputVerbosity.Verbose,
                    false,
                    "[ShaderGLCall] call={0} program='{1}' hash={2} programId={3} separable={4} elapsedMs={5:F3} renderThread={6} renderThreadStallMs={7:F3}{8}.",
                    callName,
                    Data.Name ?? "<unnamed>",
                    Hash,
                    programId,
                    Data.Separable,
                    elapsedMilliseconds,
                    renderThread,
                    renderThread ? elapsedMilliseconds : 0.0,
                    FormatRenderingDetail(detail));
            }

            private static double MeasureRenderingProgramGlCallStatic(string callName, uint programId, Action action, string? detail = null)
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
                    "[ShaderGLCall] call={0} program='<unknown>' hash=0 programId={1} separable=<unknown> elapsedMs={2:F3} renderThread={3} renderThreadStallMs={4:F3}{5}.",
                    callName,
                    programId,
                    elapsedMilliseconds,
                    renderThread,
                    renderThread ? elapsedMilliseconds : 0.0,
                    FormatRenderingDetail(detail));
                return elapsedMilliseconds;
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

            private static double StopwatchTicksToSeconds(long ticks)
                => ticks <= 0L ? 0.0 : (double)ticks / Stopwatch.Frequency;

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
                if (_replacementProgramPending || string.IsNullOrWhiteSpace(cacheKey) || programId == 0)
                    return;

                if (_sharedLinkedProgram is not null && _sharedLinkedProgram.ProgramId == programId)
                    return;

                UniformMetadataEntry[] uniforms = SnapshotUniformMetadata();
                if (uniforms.Length == 0 && _cachedProgram?.Uniforms is { Length: > 0 } cachedUniforms)
                    uniforms = cachedUniforms;
                var sharedProgram = new SharedLinkedProgram(Renderer, cacheKey, programId, Hash, format, Data.Separable, uniforms);
                SharedLinkedProgramKey key = BuildSharedLinkedProgramKey(cacheKey);
                if (SharedLinkedPrograms.TryAdd(key, sharedProgram))
                {
                    _sharedLinkedProgram = sharedProgram;
                    ShaderProgramLifecycleDiagnostics.RecordSharedProgramCreate();
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
                CompleteBuildTelemetry(true, binaryLoadMilliseconds: 0.0, reflectionMilliseconds: reflectionMilliseconds);
                return true;
            }

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
                EnqueueDeferredProgramHandleDelete(sharedProgram.Renderer, sharedProgram.ProgramId);
                ShaderProgramLifecycleDiagnostics.RecordSharedProgramDelete();
            }

            internal bool MarkSharedMaterialUniformSource(XRMaterialBase source)
                => _sharedLinkedProgram is not null && _sharedLinkedProgram.MarkUniformSource(source);

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
                _explicitAttributeLocations.Clear();
                _explicitAttributeLocationsResolved = false;
            }

            private bool ReturnPendingBuildResult()
                => IsLinked && _replacementProgramPending;

            // Phase 3: capture compact diagnostic metadata the first time a hash fails
            // and bump the failed-hash dictionary. Cheap, non-allocating in the steady
            // state because subsequent calls just hit the AddOrUpdate update branch.
            private void MarkHashFailed(string? reason)
            {
                Failed.TryAdd(Hash, 0);
                long now = Stopwatch.GetTimestamp();
                string label = Data.Name ?? "<unnamed>";
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
                        snapshot = new FailedHashRecord(now, now, 1, null, Data.Name ?? "<unnamed>", BuildFailedHashStageListSnapshot(), Data.Separable);
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
                    $"[ShaderLink] SOURCE_FAILED_SKIPPED hash={Hash} label='{snapshot.Label ?? Data.Name ?? "<unnamed>"}' " +
                    $"separable={snapshot.Separable} stages={snapshot.StageList ?? "<none>"} skipCount={snapshot.SkipCount} " +
                    $"elapsedMs={elapsedMs:F0} reason='{snapshot.Reason ?? "<unknown>"}' " +
                    $"fingerprint={cacheKey ?? "<none>"} programId={bindingId}.");
            }

            private void MarkBuildFailed()
            {
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

            public bool Link(bool force = false, bool nonBlocking = false)
            {
                using var prof = Engine.Profiler.Start("GLRenderProgram.Link", ProfilerScopeKind.ConditionalLoop);

                if (IsLinked && !_replacementProgramPending)
                    return true;

                if (IsLinkPreparationPending)
                    return ReturnPendingBuildResult();

                Exception? linkPreparationException = _linkPreparationFailure;
                bool linkPreparationFailed = linkPreparationException is not null;
                if (linkPreparationException is not null)
                {
                    Debug.OpenGLWarning($"GLRenderProgram link preparation failed for '{Data.Name ?? "unnamed"}': {linkPreparationException.Message}");
                    _linkPreparationFailure = null;
                }

                // Check for completed async binary upload from the shared context thread.
                if (_asyncBinaryUploadPending)
                {
                    var queue = Renderer.ProgramBinaryUploadQueue;
                    if (queue is not null && TryGetBuildBindingId(out uint pendingId) && queue.TryGetResult(pendingId, out var asyncResult))
                    {
                        _asyncBinaryUploadPending = false;
                        if (asyncResult.Status == GLProgramBinaryUploadQueue.UploadStatus.Success)
                        {
                            AdoptLinkedBuildProgram(pendingId);
                            IsLinked = true;
                            double reflectionMilliseconds = RestoreRuntimeBindingStateAfterBinaryLoad();
                            RegisterCurrentLinkedProgramForSharing(asyncResult.CacheKey, asyncResult.Format, pendingId);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Ready,
                                "BinaryUploadAsync",
                                "cached binary loaded asynchronously",
                                compileMilliseconds: 0.0,
                                linkMilliseconds: 0.0,
                                fingerprint: asyncResult.CacheKey);
                            LogRenderingProgramBuildEvent(
                                "BINARY_UPLOAD_ASYNC_READY",
                                "BinaryUploadAsync",
                                "cached binary loaded asynchronously",
                                asyncResult.CacheKey,
                                pendingId,
                                binaryBytes: _cachedProgram?.Length ?? 0,
                                binaryFormat: asyncResult.Format.ToString());
                            CompleteBuildTelemetry(true, binaryLoadMilliseconds: asyncResult.LoadMilliseconds, reflectionMilliseconds: reflectionMilliseconds);
                            return true;
                        }
                        else
                        {
                            Debug.OpenGLWarning($"Async program binary upload failed for hash {Hash}: {asyncResult.ErrorLog ?? "unknown error"}. Falling back to source compilation.");
                            PublishBackendStatus(
                                EShaderProgramBackendStage.BinaryUploadFailed,
                                "BinaryUploadAsync",
                                "cached binary failed final link validation",
                                asyncResult.ErrorLog,
                                fingerprint: asyncResult.CacheKey);
                            LogRenderingProgramBuildEvent(
                                "BINARY_UPLOAD_ASYNC_FAILED",
                                "BinaryUploadAsync",
                                asyncResult.ErrorLog ?? "cached binary failed final link validation",
                                asyncResult.CacheKey,
                                pendingId,
                                binaryBytes: _cachedProgram?.Length ?? 0,
                                binaryFormat: asyncResult.Format.ToString());
                            CompleteBuildTelemetry(false, binaryLoadMilliseconds: asyncResult.LoadMilliseconds, failureReason: asyncResult.ErrorLog);
                            DeleteFromBinaryShaderCache(_cachedProgram?.CacheKey ?? asyncResult.CacheKey, asyncResult.Format);
                            // Fall through to compile from source below.
                        }
                    }
                    else
                    {
                        return ReturnPendingBuildResult(); // Upload still in progress.
                    }
                }

                // Check for completed async compile+link from the shared context thread.
                if (_asyncCompileLinkPending)
                {
                    var compileQueue = Renderer.ProgramCompileLinkQueue;
                    if (compileQueue is not null && TryGetBuildBindingId(out uint pendingId2) && compileQueue.TryGetResult(pendingId2, out var compileResult))
                    {
                        _asyncCompileLinkPending = false;
                        if (compileResult.Status == GLProgramCompileLinkQueue.CompileStatus.Success)
                        {
                            CompleteUberBackendTracking(true, compileMilliseconds: compileResult.CompileMilliseconds, linkMilliseconds: compileResult.LinkMilliseconds);
                            Debug.OpenGL($"[ShaderCache] READY hash={Hash}, shared-context compileMs={compileResult.CompileMilliseconds:F2}, linkMs={compileResult.LinkMilliseconds:F2}.");
                            AdoptLinkedBuildProgram(pendingId2);
                            IsLinked = true;
                            long reflectionStart = Stopwatch.GetTimestamp();
                            using var uniformsProf = Engine.Profiler.Start("GLRenderProgram.Link.CacheActiveUniforms", ProfilerScopeKind.OneOffInvoke);
                            CacheActiveUniforms();
                            double reflectionMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - reflectionStart);
                            CacheBinary(pendingId2);
                            if (_cachedProgram is { } cachedSourceProgram)
                                RegisterCurrentLinkedProgramForSharing(cachedSourceProgram.CacheKey, cachedSourceProgram.Format, pendingId2);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Ready,
                                "SharedContextSource",
                                "source compile/link completed on shared context",
                                compileMilliseconds: compileResult.CompileMilliseconds,
                                linkMilliseconds: compileResult.LinkMilliseconds);
                            LogRenderingProgramBuildEvent(
                                "SOURCE_QUEUE_ASYNC_READY",
                                "SharedContextSource",
                                "source compile/link completed on shared context",
                                _activeBuildFingerprint,
                                pendingId2,
                                _preparedCompileInputs);
                            CompleteBuildTelemetry(
                                true,
                                compileResult.CompileMilliseconds,
                                compileResult.LinkMilliseconds,
                                reflectionMilliseconds: reflectionMilliseconds);
                            InFlightCompilations.TryRemove(Hash, out _);
                            _asyncCompileDuplicateHashWaitPending = false;
                            return true;
                        }
                        else
                        {
                            CompleteUberBackendTracking(false, compileResult.ErrorLog, compileResult.CompileMilliseconds, compileResult.LinkMilliseconds);
                            string errorKind = compileResult.Status == GLProgramCompileLinkQueue.CompileStatus.CompileFailed
                                ? "compile" : "link";
                            PrintLinkDebug(pendingId2, compileResult.ErrorLog, $"Async {errorKind} failed");
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Failed,
                                "SharedContextSource",
                                $"async {errorKind} failed",
                                compileResult.ErrorLog,
                                compileResult.CompileMilliseconds,
                                compileResult.LinkMilliseconds);
                            LogRenderingProgramBuildEvent(
                                "SOURCE_QUEUE_ASYNC_FAILED",
                                "SharedContextSource",
                                compileResult.ErrorLog ?? $"async {errorKind} failed",
                                _activeBuildFingerprint,
                                pendingId2,
                                _preparedCompileInputs);
                            CompleteBuildTelemetry(
                                false,
                                compileResult.CompileMilliseconds,
                                compileResult.LinkMilliseconds,
                                failureReason: compileResult.ErrorLog);
                            MarkHashFailed(compileResult.ErrorLog ?? $"async {errorKind} failed");
                            InFlightCompilations.TryRemove(Hash, out _);
                            _asyncCompileDuplicateHashWaitPending = false;
                            MarkBuildFailed();
                            return IsLinked;
                        }
                    }
                    else
                    {
                        ReportSlowAsyncPending("shared-context compile/link");
                        return ReturnPendingBuildResult(); // Compile+link still in progress.
                    }
                }

                // Resume an in-progress async compile/link if the extension is active.
                if (_asyncLinkPhase != EAsyncLinkPhase.Idle)
                    return ContinueAsyncLink();

                if (!LinkReady && !force)
                    return ReturnPendingBuildResult();

                //if (!IsGenerated)
                //{
                //    Generate();
                //    return false;
                //}

                //if (IsLinked)
                //    return true;

                if (_shaderCache.IsEmpty/* || _shaderCache.Values.Any(x => !x.IsCompiled)*/)
                    return ReturnPendingBuildResult();

                if (!force && !linkPreparationFailed && ShouldDeferLinkPreparationOnRenderThread())
                {
                    BeginPrepareLinkData();
                    RegisterPendingAsyncProgram();
                    return ReturnPendingBuildResult();
                }

                bool isCached;
                uint bindingId = GetBuildBindingId();
                BinaryProgram binProg;
                string? cacheKey;

                // Use pre-computed link data if available (populated by PrepareLinkData on a job thread),
                // otherwise fall back to computing on the GL thread. Once computed,
                // _hashComputed prevents redundant CalcHash calls while deferring.
                if (_linkDataPrepared)
                {
                    Hash = _preparedHash;
                    cacheKey = _preparedCacheKey;
                    isCached = _preparedIsCached && !ShouldBypassBinaryCacheForLiveUberVariant();
                    binProg = _preparedBinProg;
                    _linkDataPrepared = false;
                    _hashComputed = true;
                }
                else
                {
                    isCached = false;
                    binProg = default;
                    cacheKey = null;
                    if (!_hashComputed)
                    {
                        using (Engine.Profiler.Start("GLRenderProgram.Link.CacheLookup", ProfilerScopeKind.OneOffInvoke))
                        {
                            using (Engine.Profiler.Start("GLRenderProgram.Link.CalcHash", ProfilerScopeKind.OneOffInvoke))
                                Hash = CalcShaderSourceHash();
                        }
                        _hashComputed = true;
                    }

                    bool bypassBinaryCache = ShouldBypassBinaryCacheForLiveUberVariant();
                    if (!bypassBinaryCache && Engine.Rendering.Settings.AllowBinaryProgramCaching)
                    {
                        cacheKey = BuildBinaryCacheKey(Hash);
                        using (Engine.Profiler.Start("GLRenderProgram.Link.BinaryCacheLookup", ProfilerScopeKind.OneOffInvoke))
                            isCached = BinaryCache?.TryGetValue(cacheKey, out binProg) ?? false;
                    }
                }

                if (isCached)
                {
                    _asyncCompileDuplicateHashWaitPending = false;
                    using var cacheLoadProf = Engine.Profiler.Start("GLRenderProgram.Link.LoadCachedBinary", ProfilerScopeKind.OneOffInvoke);
                    _cachedProgram = binProg;
                    GLEnum format = binProg.Format;
                    ShaderProgramLifecycleDiagnostics.RecordBinaryCacheHit();
                    PublishBackendStatus(
                        EShaderProgramBackendStage.CacheHit,
                        "BinaryCache",
                        "binary cache entry matched runtime fingerprint",
                        fingerprint: binProg.CacheKey);
                    LogRenderingProgramBuildEvent(
                        "BINARY_CACHE_HIT",
                        "BinaryCache",
                        "binary cache entry matched runtime fingerprint",
                        binProg.CacheKey,
                        bindingId,
                        binaryBytes: binProg.Length,
                        binaryFormat: format.ToString());
                    if (TryUseSharedLinkedProgram(binProg))
                        return true;

                    if (!Engine.Rendering.Stats.CanAllocateVram(binProg.Length, 0, out long projectedBytes, out long budgetBytes))
                    {
                        Debug.OpenGLWarning($"[VRAM Budget] Skipping cached program binary load for hash {Hash} ({binProg.Length} bytes). Projected={projectedBytes} bytes, Budget={budgetBytes} bytes. Deleting from cache.");
                        DeleteFromBinaryShaderCache(binProg.CacheKey, format);
                    }
                    else
                    {
                        var uploadQueue = Renderer.ProgramBinaryUploadQueue;
                        OpenGLShaderLinkBackendSelection selection = OpenGLShaderLinkBackendSelector.Select(new OpenGLShaderLinkBackendContext(
                            Engine.Rendering.Settings.OpenGLShaderLinkStrategy,
                            Engine.Rendering.Settings.AsyncProgramCompilation,
                            Engine.Rendering.Settings.AllowBinaryProgramCaching,
                            Engine.Rendering.Settings.AsyncProgramBinaryUpload,
                            HasBinaryCacheHit: true,
                            BinaryUploadAvailable: uploadQueue is { IsAvailable: true },
                            BinaryUploadCanEnqueue: uploadQueue is { CanEnqueue: true },
                            DriverParallelAvailable: Renderer.UseDriverParallelShaderCompile,
                            SharedContextCompileAvailable: Renderer.ProgramCompileLinkQueue is { IsAvailable: true },
                            SharedContextCompileCanEnqueue: Renderer.ProgramCompileLinkQueue is { CanEnqueue: true },
                            CompileInputsReady: _preparedCompileInputs is { Length: > 0 },
                            IsKnownAsyncLinkHazard,
                            HashPreviouslyFailed: false,
                            AllowSynchronousSourceLink: false));

                        if (selection.Lane == EOpenGLProgramBuildLane.BinaryQueueBackpressure)
                        {
                            uploadQueue?.RecordBackpressure();
                            PublishBackendStatus(
                                EShaderProgramBackendStage.QueueBackpressure,
                                "BinaryUploadAsync",
                                selection.Reason,
                                fingerprint: binProg.CacheKey);
                            LogRenderingProgramBuildEvent(
                                "BINARY_UPLOAD_BACKPRESSURE",
                                "BinaryUploadAsync",
                                selection.Reason,
                                binProg.CacheKey,
                                bindingId,
                                binaryBytes: binProg.Length,
                                binaryFormat: format.ToString());
                            RegisterPendingAsyncProgram();
                            return ReturnPendingBuildResult();
                        }

                        if (selection.Lane == EOpenGLProgramBuildLane.BinaryUploadAsync && uploadQueue is not null)
                        {
                            // Phase 4: coalesce duplicate uploads of the same cacheKey.
                            // If a sibling GLRenderProgram is already uploading this exact
                            // binary, defer this caller until that upload completes so we
                            // do not flood the queue with 50 simultaneous uploads of the
                            // same bytes (each program still needs its own programId
                            // populated; serialization just prevents queue saturation).
                            if (!uploadQueue.TryReserveCacheKey(binProg.CacheKey))
                            {
                                uploadQueue.RecordBackpressure();
                                PublishBackendStatus(
                                    EShaderProgramBackendStage.QueueBackpressure,
                                    "BinaryUploadAsync",
                                    "duplicate cacheKey upload already in flight",
                                    fingerprint: binProg.CacheKey);
                                LogRenderingProgramBuildEvent(
                                    "BINARY_UPLOAD_COALESCED",
                                    "BinaryUploadAsync",
                                    "duplicate cacheKey upload already in flight",
                                    binProg.CacheKey,
                                    bindingId,
                                    binaryBytes: binProg.Length,
                                    binaryFormat: format.ToString());
                                RegisterPendingAsyncProgram();
                                return ReturnPendingBuildResult();
                            }

                            BeginBuildTelemetry("BinaryUploadAsync", binProg.CacheKey);
                            uploadQueue.EnqueueUpload(bindingId, binProg.Binary, format, binProg.Length, Hash, binProg.CacheKey);
                            _asyncBinaryUploadPending = true;
                            PublishBackendStatus(
                                EShaderProgramBackendStage.BinaryUploadPending,
                                "BinaryUploadAsync",
                                selection.Reason,
                                fingerprint: binProg.CacheKey);
                            LogRenderingProgramBuildEvent(
                                "BINARY_UPLOAD_ASYNC_QUEUED",
                                "BinaryUploadAsync",
                                selection.Reason,
                                binProg.CacheKey,
                                bindingId,
                                binaryBytes: binProg.Length,
                                binaryFormat: format.ToString());
                            RegisterPendingAsyncProgram();
                            return ReturnPendingBuildResult();
                        }

                        BeginBuildTelemetry("BinaryUploadSynchronous", binProg.CacheKey);
                        LogRenderingProgramBuildEvent(
                            "BINARY_UPLOAD_SYNC_BEGIN",
                            "BinaryUploadSynchronous",
                            selection.Reason,
                            binProg.CacheKey,
                            bindingId,
                            binaryBytes: binProg.Length,
                            binaryFormat: format.ToString());
                        long binaryStart = Stopwatch.GetTimestamp();
                        using (Engine.Profiler.Start("GLRenderProgram.Link.ProgramBinary", ProfilerScopeKind.OneOffInvoke))
                        {
                            fixed (byte* ptr = binProg.Binary)
                            {
                                long programBinaryStart = Stopwatch.GetTimestamp();
                                Api.ProgramBinary(bindingId, format, ptr, binProg.Length);
                                if (ShouldLogRenderingShaderLinkVerbose())
                                {
                                    double programBinaryMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - programBinaryStart);
                                    LogRenderingProgramGlCall(
                                        "glProgramBinary",
                                        bindingId,
                                        programBinaryMilliseconds,
                                        $"binaryBytes={binProg.Length} binaryFormat={format} cacheKey={binProg.CacheKey}");
                                }
                            }
                        }
                        double binaryLoadMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - binaryStart);
                        GLEnum error = GLEnum.NoError;
                        MeasureRenderingProgramGlCall(
                            "glGetError",
                            bindingId,
                            () => error = Api.GetError(),
                            "phase=binary-load-error-check");
                        if (error != GLEnum.NoError)
                        {
                            Debug.OpenGLWarning($"Failed to load cached program binary with format {format} and hash {Hash}: {error}. Deleting from cache.");
                            PublishBackendStatus(
                                EShaderProgramBackendStage.BinaryUploadFailed,
                                "BinaryUploadSynchronous",
                                "glProgramBinary returned a GL error",
                                error.ToString(),
                                fingerprint: binProg.CacheKey);
                            LogRenderingProgramBuildEvent(
                                "BINARY_UPLOAD_SYNC_FAILED",
                                "BinaryUploadSynchronous",
                                $"glProgramBinary returned {error}",
                                binProg.CacheKey,
                                bindingId,
                                binaryBytes: binProg.Length,
                                binaryFormat: format.ToString());
                            CompleteBuildTelemetry(false, binaryLoadMilliseconds: binaryLoadMilliseconds, failureReason: error.ToString());
                            DeleteFromBinaryShaderCache(binProg.CacheKey, format);
                        }
                        else if (!TryValidateProgramBinaryLoad(bindingId, binProg.CacheKey, format, out string? binaryFailureReason))
                        {
                            Debug.OpenGLWarning($"Cached program binary failed link-status validation for hash {Hash}: {binaryFailureReason}. Deleting from cache.");
                            PublishBackendStatus(
                                EShaderProgramBackendStage.BinaryUploadFailed,
                                "BinaryUploadSynchronous",
                                "cached binary failed final link validation",
                                binaryFailureReason,
                                fingerprint: binProg.CacheKey);
                            LogRenderingProgramBuildEvent(
                                "BINARY_UPLOAD_SYNC_FAILED",
                                "BinaryUploadSynchronous",
                                binaryFailureReason ?? "cached binary failed final link validation",
                                binProg.CacheKey,
                                bindingId,
                                binaryBytes: binProg.Length,
                                binaryFormat: format.ToString());
                            CompleteBuildTelemetry(false, binaryLoadMilliseconds: binaryLoadMilliseconds, failureReason: binaryFailureReason);
                            DeleteFromBinaryShaderCache(binProg.CacheKey, format);
                        }
                        else
                        {
                            AdoptLinkedBuildProgram(bindingId);
                            IsLinked = true;
                            double reflectionMilliseconds = RestoreRuntimeBindingStateAfterBinaryLoad();
                            RegisterCurrentLinkedProgramForSharing(binProg.CacheKey, format, bindingId);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Ready,
                                "BinaryUploadSynchronous",
                                "cached binary loaded synchronously",
                                fingerprint: binProg.CacheKey);
                            LogRenderingProgramBuildEvent(
                                "BINARY_UPLOAD_SYNC_READY",
                                "BinaryUploadSynchronous",
                                "cached binary loaded synchronously",
                                binProg.CacheKey,
                                bindingId,
                                binaryBytes: binProg.Length,
                                binaryFormat: format.ToString());
                            CompleteBuildTelemetry(true, binaryLoadMilliseconds: binaryLoadMilliseconds, reflectionMilliseconds: reflectionMilliseconds);
                            return true;
                        }
                    }
                }
                else
                {
                    // Phase 3: short-circuit known-failed hashes BEFORE the
                    // BINARY_CACHE_MISS log + ShaderProgramSourceSummary so repeated
                    // failed hashes do not regenerate full miss records every frame.
                    if (Failed.ContainsKey(Hash))
                    {
                        EmitFailedHashSkipLog(cacheKey, bindingId);
                        PublishBackendStatus(
                            EShaderProgramBackendStage.Failed,
                            "Source",
                            "hash previously failed",
                            "Hash is marked failed.",
                            fingerprint: cacheKey);
                        MarkBuildFailed();
                        return IsLinked;
                    }

                    bool duplicateHashWaitInProgress = _asyncCompileDuplicateHashWaitPending &&
                        InFlightCompilations.ContainsKey(Hash);
                    if (!duplicateHashWaitInProgress)
                    {
                        PublishBackendStatus(
                            EShaderProgramBackendStage.CacheMiss,
                            "BinaryCache",
                            Engine.Rendering.Settings.AllowBinaryProgramCaching ? "binary cache miss" : "binary cache disabled",
                            fingerprint: cacheKey);
                        LogRenderingProgramBuildEvent(
                            "BINARY_CACHE_MISS",
                            "BinaryCache",
                            Engine.Rendering.Settings.AllowBinaryProgramCaching ? "binary cache miss" : "binary cache disabled",
                            cacheKey,
                            bindingId,
                            _preparedCompileInputs);
                    }
                }

                // If another GLRenderProgram with the same hash is already compiling,
                // defer until its binary lands in the cache.
                bool waitingForCompileQueue = _asyncCompileLinkQueueWaitPending;
                if (!waitingForCompileQueue && !InFlightCompilations.TryAdd(Hash, 0))
                {
                    bool alreadyWaitingForDuplicateHash = _asyncCompileDuplicateHashWaitPending;
                    _asyncCompileDuplicateHashWaitPending = true;
                    if (!alreadyWaitingForDuplicateHash)
                    {
                        PublishBackendStatus(
                            EShaderProgramBackendStage.QueueBackpressure,
                            "SharedContextSource",
                            "duplicate shader hash already compiling; waiting for binary cache",
                            fingerprint: cacheKey);
                        LogRenderingProgramBuildEvent(
                            "SOURCE_DUPLICATE_HASH_DEFERRED",
                            "SharedContextSource",
                            "duplicate shader hash already compiling; waiting for binary cache",
                            cacheKey,
                            bindingId,
                            _preparedCompileInputs);
                    }
                    RegisterPendingAsyncProgram();
                    return ReturnPendingBuildResult();
                }

                _asyncCompileDuplicateHashWaitPending = false;

                {
                    _cachedProgram = null;
                    if (!waitingForCompileQueue)
                    {
                        CaptureLinkRequestStackTrace();
                        ShaderProgramLifecycleDiagnostics.RecordBinaryCacheMiss();
                        ShaderProgramLifecycleDiagnostics.RecordSourceBuild();
                        Debug.OpenGL($"[ShaderCache] MISS hash={Hash}, compiling {_shaderCache.Count} shader(s) from source.");
                    }

                    var compileQueue = Renderer.ProgramCompileLinkQueue;
                    bool isKnownAsyncLinkHazard = IsKnownAsyncLinkHazard;
                    GLProgramCompileLinkQueue.ShaderInput[]? inputs = _preparedCompileInputs;
                    // Hazardous graphics programs (single-stage separable) are
                    // routed to the shared-context lane by the selector when the
                    // queue is available, even under Auto strategy, to keep the
                    // (potentially huge) cold link off the render thread. Prepare
                    // their inputs unconditionally so the selector can choose the
                    // shared-context lane. The queue still rejects compute hazards,
                    // which fall through to the synchronous path below.
                    bool wantsSharedSourceInputs = compileQueue is { IsAvailable: true } &&
                        (Renderer.UseSharedContextProgramCompileLinkQueue ||
                         Engine.Rendering.Settings.OpenGLShaderLinkStrategy == EOpenGLShaderLinkStrategy.SharedContext ||
                         isKnownAsyncLinkHazard);
                    if (wantsSharedSourceInputs && inputs is null)
                    {
                        if (nonBlocking)
                        {
                            // Phase B: PrepareCompileInputs() resolves and concatenates
                            // shader source on the calling thread. Skip on the
                            // render-prep hot path; kick async preparation instead
                            // so PollPendingAsyncPrograms can advance the program
                            // next frame.
                            InFlightCompilations.TryRemove(Hash, out _);
                            BeginPrepareLinkData();
                            RegisterPendingAsyncProgram();
                            return ReturnPendingBuildResult();
                        }
                        inputs = PrepareCompileInputs();
                    }

                    OpenGLShaderLinkBackendSelection sourceSelection = OpenGLShaderLinkBackendSelector.Select(new OpenGLShaderLinkBackendContext(
                        Engine.Rendering.Settings.OpenGLShaderLinkStrategy,
                        Engine.Rendering.Settings.AsyncProgramCompilation,
                        Engine.Rendering.Settings.AllowBinaryProgramCaching,
                        Engine.Rendering.Settings.AsyncProgramBinaryUpload,
                        HasBinaryCacheHit: false,
                        BinaryUploadAvailable: Renderer.ProgramBinaryUploadQueue is { IsAvailable: true },
                        BinaryUploadCanEnqueue: Renderer.ProgramBinaryUploadQueue is { CanEnqueue: true },
                        DriverParallelAvailable: Renderer.UseDriverParallelShaderCompile,
                        SharedContextCompileAvailable: compileQueue is { IsAvailable: true },
                        SharedContextCompileCanEnqueue: compileQueue is { CanEnqueue: true },
                        CompileInputsReady: inputs is { Length: > 0 },
                        isKnownAsyncLinkHazard,
                        HashPreviouslyFailed: false,
                        AllowSynchronousSourceLink: false));
                    LogRenderingProgramBuildEvent(
                        "SOURCE_BACKEND_SELECTED",
                        sourceSelection.Lane.ToString(),
                        sourceSelection.Reason,
                        cacheKey,
                        bindingId,
                        inputs);

                    if (sourceSelection.Lane == EOpenGLProgramBuildLane.SharedContextQueueBackpressure)
                    {
                        _asyncCompileLinkQueueWaitPending = true;
                        PublishBackendStatus(
                            EShaderProgramBackendStage.QueueBackpressure,
                            "SharedContextSource",
                            sourceSelection.Reason,
                            fingerprint: cacheKey);
                        LogRenderingProgramBuildEvent(
                            "SOURCE_QUEUE_BACKPRESSURE",
                            "SharedContextSource",
                            sourceSelection.Reason,
                            cacheKey,
                            bindingId,
                            inputs);
                        RegisterPendingAsyncProgram();
                        return ReturnPendingBuildResult();
                    }

                    if (sourceSelection.Lane == EOpenGLProgramBuildLane.SharedContextSource &&
                        compileQueue is not null &&
                        inputs is { Length: > 0 })
                    {
                        if (TryResolveUberVariantHash(inputs, out ulong queuedVariantHash))
                            BeginUberBackendCompileTracking(queuedVariantHash);

                        EnsureProgramBinaryRetrievableHintForSourceBuild(bindingId, "SharedContextSource");
                        if (!compileQueue.TryEnqueueCompileAndLink(bindingId, inputs, out string? rejectReason))
                        {
                            _asyncCompileLinkQueueWaitPending = true;
                            PublishBackendStatus(
                                EShaderProgramBackendStage.QueueBackpressure,
                                "SharedContextSource",
                                rejectReason ?? "shared-context compile/link queue rejected source job",
                                fingerprint: cacheKey);
                            LogRenderingProgramBuildEvent(
                                "SOURCE_QUEUE_REJECTED",
                                "SharedContextSource",
                                rejectReason ?? "shared-context compile/link queue rejected source job",
                                cacheKey,
                                bindingId,
                                inputs);
                            Debug.OpenGLWarning($"[ShaderCache] Shared compile queue rejected hash {Hash}: {rejectReason}. Keeping program pending; synchronous source linking is disabled.");
                            RegisterPendingAsyncProgram();
                            return ReturnPendingBuildResult();
                        }
                        else
                        {
                            BeginBuildTelemetry("SharedContextSource", cacheKey);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.SourceQueued,
                                "SharedContextSource",
                                sourceSelection.Reason,
                                fingerprint: cacheKey);
                            ShaderProgramLifecycleDiagnostics.RecordSharedContextSourceQueued();
                            Debug.OpenGL($"[ShaderCache] QUEUE hash={Hash}, compiling {_shaderCache.Count} shader(s) on shared context.");
                            LogRenderingProgramBuildEvent(
                                "SOURCE_QUEUE_ASYNC_QUEUED",
                                "SharedContextSource",
                                sourceSelection.Reason,
                                cacheKey,
                                bindingId,
                                inputs);
                            _asyncCompileLinkQueueWaitPending = false;
                            _asyncCompileLinkPending = true;
                            RegisterPendingAsyncProgram();
                            return ReturnPendingBuildResult();
                        }
                    }
                    _asyncCompileLinkQueueWaitPending = false;
                    if (sourceSelection.Lane == EOpenGLProgramBuildLane.SourceUnavailable)
                    {
                        InFlightCompilations.TryRemove(Hash, out _);
                        _asyncCompileDuplicateHashWaitPending = false;
                        _asyncCompileLinkQueueWaitPending = true;
                        PublishBackendStatus(
                            EShaderProgramBackendStage.QueueBackpressure,
                            "SourceUnavailable",
                            sourceSelection.Reason,
                            fingerprint: cacheKey);
                        LogRenderingProgramBuildEvent(
                            "SOURCE_UNAVAILABLE_DEFERRED",
                            "SourceUnavailable",
                            sourceSelection.Reason,
                            cacheKey,
                            bindingId,
                            inputs);
                        RegisterPendingAsyncProgram();
                        return ReturnPendingBuildResult();
                    }
                    if (sourceSelection.Lane == EOpenGLProgramBuildLane.SynchronousSource)
                    {
                        if (nonBlocking)
                        {
                            // Phase B: defer the synchronous source-compile fallback
                            // off the render-prep hot path. PollPendingAsyncPrograms
                            // will pick this program up and run the same work
                            // bounded by PollPendingAsyncProgramsSyncBudgetMilliseconds
                            // inside the upload pump rather than inside
                            // GLMeshRenderer.TryPrepareForRendering.
                            InFlightCompilations.TryRemove(Hash, out _);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.SynchronousFallback,
                                "SynchronousSource",
                                sourceSelection.Reason + " (deferred: non-blocking caller)",
                                fingerprint: cacheKey);
                            RegisterPendingAsyncProgram();
                            return ReturnPendingBuildResult();
                        }
                        PublishBackendStatus(
                            EShaderProgramBackendStage.SynchronousFallback,
                            "SynchronousSource",
                            sourceSelection.Reason,
                            fingerprint: cacheKey);
                    }

                    // If a hazardous program reaches the synchronous fallback, disable
                    // driver parallel compile just around this link. This avoids asking
                    // the NVIDIA parallel-link worker to process a shape that can wedge.
                    // In this fallback, suppressing driver parallel compile lets
                    // glCompileShader and glLinkProgram run inline on the GL thread —
                    // status queries can complete without waiting on the parallel-link
                    // worker.
                    //
                    // CAVEAT: doing this on the render thread for a large uber fragment
                    // forces the driver to run a single compile thread on a 300+ KB
                    // program, which can take 60-90 seconds and produces multi-second
                    // main-thread stalls. The historical render-thread path was slow but
                    // *did not* suppress parallel compile, and did not wedge. So by
                    // default we now keep parallel compile enabled on this fallback.
                    // Set XRE_SYNCSRC_HAZARD_DISABLE_PARALLEL=1 to opt back in to the
                    // suppression if the wedge ever surfaces on the render thread.
                    bool hazardSyncDisableParallel = string.Equals(
                        Environment.GetEnvironmentVariable("XRE_SYNCSRC_HAZARD_DISABLE_PARALLEL"),
                        "1",
                        StringComparison.Ordinal);
                    bool hazardSyncLink = hazardSyncDisableParallel
                        && isKnownAsyncLinkHazard
                        && Renderer.UseDriverParallelShaderCompile
                        && Renderer.TryDisableParallelShaderCompileForHazardousLink();
                    if (hazardSyncLink)
                    {
                        LogRenderingProgramBuildEvent(
                            "SOURCE_HAZARD_PARALLEL_COMPILE_DISABLED",
                            "SynchronousSource",
                            "temporarily set shader compiler thread count to zero around hazardous link",
                            cacheKey,
                            bindingId,
                            inputs);
                    }

                    try
                    {

                    if (_activeBuildBackend is null)
                    {
                        string backendName = sourceSelection.Lane == EOpenGLProgramBuildLane.DriverParallelSource
                            ? "DriverParallelSource"
                            : "SynchronousSource";
                        BeginBuildTelemetry(backendName, cacheKey);
                    }
                    LogRenderingProgramBuildEvent(
                        "SOURCE_BUILD_BEGIN",
                        _activeBuildBackend,
                        sourceSelection.Reason,
                        cacheKey,
                        bindingId,
                        inputs);

                    long sourceCompileStart = Stopwatch.GetTimestamp();
                    using (Engine.Profiler.Start("GLRenderProgram.Link.GenerateShaders", ProfilerScopeKind.OneOffInvoke))
                    {
                        if (TryResolveUberVariantHash(out ulong variantHash))
                            BeginUberBackendCompileTracking(variantHash);

                        foreach (GLShader shader in _shaderCache.Values)
                        {
                            shader.PrepareCompileVariant(Data.Separable);
                            if (shader.Data.GenerateAsync)
                                Engine.EnqueueMainThreadTask(shader.Generate);
                            else
                                shader.Generate();
                        }
                    }

                    // Driver-parallel linking is only safe for shapes outside the
                    // known hazard set.
                    bool useDriverParallelLink = Renderer.UseDriverParallelShaderCompile
                        && !isKnownAsyncLinkHazard;

                    if (!useDriverParallelLink)
                    {
                        // Drain any in-flight async compiles started under driver-parallel
                        // so we can attach + link synchronously below.
                        foreach (GLShader s in _shaderCache.Values)
                        {
                            if (s.IsCompilePending)
                                s.CompleteCompileBlocking();
                        }
                    }
                    else if (_shaderCache.Values.Any(s => s.IsCompilePending))
                    {
                        // When driver parallel shader compile is active, CompileShader() is non-blocking.
                        // Shaders may still be compiling — enter the async state machine and return.
                        _asyncLinkPhase = EAsyncLinkPhase.Compiling;
                        PublishBackendStatus(
                            EShaderProgramBackendStage.Compiling,
                            "DriverParallelSource",
                            "driver-parallel shader compile dispatched",
                            fingerprint: cacheKey);
                        RegisterPendingAsyncProgram();
                        return ReturnPendingBuildResult();
                    }

                    double compileMilliseconds = CompleteUberBackendCompileTracking();
                    if (compileMilliseconds <= 0.0)
                        compileMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - sourceCompileStart);

                    if (_shaderCache.Values.Any(x => !x.IsCompiled))
                    {
                        ShaderProgramLifecycleDiagnostics.RecordSourceFailure();
                        Debug.OpenGLWarning($"Failed to compile program with hash {Hash}.");
                        CompleteUberBackendTracking(false, "Backend shader compile failed.", compileMilliseconds, 0.0);
                        PublishBackendStatus(
                            EShaderProgramBackendStage.Failed,
                            _activeBuildBackend ?? "SynchronousSource",
                            "shader compile failed",
                            "Backend shader compile failed.",
                            compileMilliseconds,
                            0.0);
                        CompleteBuildTelemetry(false, compileMilliseconds, failureReason: "Backend shader compile failed.");
                        MarkHashFailed("Backend shader compile failed.");
                        InFlightCompilations.TryRemove(Hash, out _);
                        //TODO: return invalid material until shaders are compiled
                        MarkBuildFailed();
                        return IsLinked;
                    }
                    
                    //Debug.Out($"Compiled program with hash {Hash}.");
                    var shaderCache = _shaderCache.Values;
                    List<uint> attachedShaderIds = [];
                    bool noErrors = true;
                    bool sourceBuildFailed = false;
                    using (Engine.Profiler.Start("GLRenderProgram.Link.AttachShaders", ProfilerScopeKind.OneOffInvoke))
                    {
                        foreach (GLShader shader in shaderCache)
                        {
                            if (shader.IsCompiled)
                            {
                                uint shaderId = shader.BindingId;
                                MeasureRenderingProgramGlCall(
                                    "glAttachShader",
                                    bindingId,
                                    () => Api.AttachShader(bindingId, shaderId),
                                    $"shaderId={shaderId} shaderType={shader.Data.Type} phase=source-sync-attach");
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
                    }
                    if (noErrors)
                    {
                        BeginUberBackendLinkTracking(compileMilliseconds);
                        long linkStartTimestamp = Stopwatch.GetTimestamp();
                        using var linkProf = Engine.Profiler.Start("GLRenderProgram.Link.DriverLinkProgram", ProfilerScopeKind.OneOffInvoke);
                        EnsureProgramBinaryRetrievableHintForSourceBuild(bindingId, _activeBuildBackend ?? "SynchronousSource");
                        MeasureRenderingProgramGlCall(
                            "glLinkProgram",
                            bindingId,
                            () => Api.LinkProgram(bindingId),
                            $"backend={_activeBuildBackend ?? "SynchronousSource"}");

                        // When driver parallel shader compile is active, LinkProgram is also non-blocking.
                        if (useDriverParallelLink)
                        {
                            _asyncLinkedProgramId = bindingId;
                            _asyncAttachedShaderIds = [.. attachedShaderIds];
                            _asyncLinkPhase = EAsyncLinkPhase.Linking;
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Linking,
                                "DriverParallelSource",
                                "driver-parallel link dispatched",
                                compileMilliseconds: compileMilliseconds,
                                fingerprint: cacheKey);
                            RestartAsyncPendingDiagnostics();
                            RegisterPendingAsyncProgram();
                            return ReturnPendingBuildResult();
                        }

                        int status = 0;
                        MeasureRenderingProgramGlCall(
                            "glGetProgramiv(GL_LINK_STATUS)",
                            bindingId,
                            () => Api.GetProgram(bindingId, GLEnum.LinkStatus, out status),
                            "phase=source-sync-link-status");
                        bool linked = status != 0;
                        string? linkError = null;
                        if (!linked)
                        {
                            MeasureRenderingProgramGlCall(
                                "glGetProgramInfoLog",
                                bindingId,
                                () => Api.GetProgramInfoLog(bindingId, out linkError),
                                "phase=source-sync-link-log");
                        }
                        double linkMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - linkStartTimestamp);

                        CompleteUberBackendTracking(linked, linkError, compileMilliseconds, linkMilliseconds);

                        if (!linked)
                        {
                            PrintLinkDebug(bindingId);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Failed,
                                _activeBuildBackend ?? "SynchronousSource",
                                "source link failed",
                                linkError,
                                compileMilliseconds,
                                linkMilliseconds);
                            CompleteBuildTelemetry(false, compileMilliseconds, linkMilliseconds, failureReason: linkError);
                            MarkHashFailed(linkError);
                            sourceBuildFailed = true;
                        }
                        else
                        {
                            AdoptLinkedBuildProgram(bindingId);
                            IsLinked = true;
                            long reflectionStart = Stopwatch.GetTimestamp();
                            using var uniformsProf = Engine.Profiler.Start("GLRenderProgram.Link.CacheActiveUniforms", ProfilerScopeKind.OneOffInvoke);
                            CacheActiveUniforms();
                            double reflectionMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - reflectionStart);
                            CacheBinary(bindingId);
                            if (_cachedProgram is { } cachedSyncSourceProgram)
                                RegisterCurrentLinkedProgramForSharing(cachedSyncSourceProgram.CacheKey, cachedSyncSourceProgram.Format, bindingId);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Ready,
                                _activeBuildBackend ?? "SynchronousSource",
                                "source link completed",
                                compileMilliseconds: compileMilliseconds,
                                linkMilliseconds: linkMilliseconds);
                            CompleteBuildTelemetry(true, compileMilliseconds, linkMilliseconds, reflectionMilliseconds: reflectionMilliseconds);
                        }
                        InFlightCompilations.TryRemove(Hash, out _);
                    }
                    else
                    {
                        PublishBackendStatus(
                            EShaderProgramBackendStage.Failed,
                            _activeBuildBackend ?? "SynchronousSource",
                            "one or more shaders were not compiled",
                            "One or more shaders failed to compile.",
                            compileMilliseconds);
                        CompleteBuildTelemetry(false, compileMilliseconds, failureReason: "One or more shaders failed to compile.");
                        MarkHashFailed("One or more shaders failed to compile.");
                        sourceBuildFailed = true;
                    }

                    using (Engine.Profiler.Start("GLRenderProgram.Link.DetachShaders", ProfilerScopeKind.OneOffInvoke))
                    {
                        DetachShaders(bindingId, [.. attachedShaderIds]);
                    }
                    using (Engine.Profiler.Start("GLRenderProgram.Link.DestroyShaderObjects", ProfilerScopeKind.OneOffInvoke))
                    {
                        _shaderCache.ForEach(x =>
                        {
                            x.Value.Destroy();
                        });
                    }
                    if (sourceBuildFailed)
                    {
                        InFlightCompilations.TryRemove(Hash, out _);
                        MarkBuildFailed();
                    }
                    return IsLinked;

                    }
                    finally
                    {
                        if (hazardSyncLink)
                        {
                            Renderer.RestoreParallelShaderCompile();
                            LogRenderingProgramBuildEvent(
                                "SOURCE_HAZARD_PARALLEL_COMPILE_RESTORED",
                                "SynchronousSource",
                                "restored configured shader compiler thread count after hazardous link",
                                cacheKey,
                                bindingId,
                                inputs);
                        }
                    }
                }
            }

            private void Value_SourceChanged()
            {
                InvalidatePreparedLinkData();

                //If the source of a shader changes, we need to relink the program.
                if (IsLinked)
                    Relink();
            }

            private void Relink()
            {
                if (Engine.InvokeOnMainThread(Relink, "GLRenderProgram.Relink"))
                    return;

                if (IsLinked && TryGetBindingId(out _))
                {
                    BeginReplacementProgramBuild();
                    Link();
                    return;
                }

                Destroy();
                Generate();
                BeginPrepareLinkData();
                Link();
            }

            private void PrintLinkDebug(uint bindingId)
            {
                string info = string.Empty;
                MeasureRenderingProgramGlCall(
                    "glGetProgramInfoLog",
                    bindingId,
                    () => Api.GetProgramInfoLog(bindingId, out info),
                    "phase=link-debug");
                PrintLinkDebug(bindingId, info, "Link failed");
            }

            private void PrintLinkDebug(uint bindingId, string? info, string? failureKind)
            {
                string programName = string.IsNullOrWhiteSpace(Data.Name) ? "<unnamed>" : Data.Name;
                var builder = new StringBuilder();
                builder
                    .Append("GLRenderProgram ")
                    .Append(string.IsNullOrWhiteSpace(failureKind) ? "link failed" : failureKind)
                    .AppendLine(".")
                    .Append("Program='")
                    .Append(programName)
                    .Append("', BindingId=")
                    .Append(bindingId)
                    .Append(", Hash=")
                    .Append(Hash)
                    .Append(", Separable=")
                    .Append(Data.Separable)
                    .Append(", ShaderCount=")
                    .AppendLine(_shaderCache.Count.ToString());

                builder.AppendLine("[Driver Log]");
                builder.AppendLine(string.IsNullOrWhiteSpace(info)
                    ? "Unable to link program, but no error was returned."
                    : info.TrimEnd());

                builder.AppendLine("[Shaders]");
                foreach (GLShader shader in _shaderCache.Values)
                {
                    string? filePath = shader.Data.Source?.FilePath;
                    builder
                        .Append("  - ")
                        .Append(shader.Data.Type)
                        .Append(": ")
                        .AppendLine(string.IsNullOrWhiteSpace(filePath) ? "<inline>" : filePath);
                }

                if (!string.IsNullOrWhiteSpace(_linkRequestStackTrace))
                {
                    builder.AppendLine("[Link Request StackTrace]");
                    builder.AppendLine(_linkRequestStackTrace.TrimEnd());
                }

                Debug.OpenGLError(builder.ToString());
            }

            private void CaptureLinkRequestStackTrace()
            {
#if DEBUG || EDITOR
                _linkRequestStackTrace ??= Debug.GetStackTrace(3, 24, ignoreBeforeWndProc: false);
#endif
            }

        }
    }
}
