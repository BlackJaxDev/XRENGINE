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
            private readonly ConcurrentDictionary<XRShader, GLShader> _shaderCache = [];

            private void ShaderRemoved(XRShader item)
            {
                if (!_shaderCache.TryRemove(item, out var shader) || shader is null)
                    return;

                // Decouple this program from the shared GLShader first. ShaderUncached removes
                // `this` from shader.ActivePrograms, which is the per-GLShader refcount.
                ShaderUncached(shader);

                // The same GLShader instance is shared across every GLRenderProgram that uses the
                // underlying XRShader (resolved via Renderer.GenericToAPI<GLShader>). Only destroy
                // the shared GL shader object once the last program drops it; otherwise siblings
                // would lose their compiled shader and incur a forced recompile on next link.
                if (shader.ActivePrograms.Count == 0)
                    shader.Destroy();
            }

            private void ShaderAdded(XRShader item)
            {
                _shaderCache.TryAdd(item, GetAndGenerate(item));
            }
            private GLShader GetAndGenerate(XRShader data)
            {
                GLShader shader = Renderer.GenericToAPI<GLShader>(data)!;
                //RuntimeEngine.EnqueueMainThreadTask(shader.Generate);
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
                ShaderProgramLifecycleDiagnostics.RecordLogicalProgramCreate();

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
                Data.TransformFeedbackLayoutChanged += TransformFeedbackLayoutChanged;

                foreach (XRShader shader in Data.Shaders)
                    ShaderAdded(shader);
                Data.Shaders.PostAnythingAdded += ShaderAdded;
                Data.Shaders.PostAnythingRemoved += ShaderRemoved;
            }

            private void UseRequested(XRRenderProgram program)
            {
                if (RuntimeEngine.InvokeOnMainThread(() => UseRequested(program), "GLRenderProgram.UseRequested"))
                    return;

                Use();
            }

            private void LinkRequested(XRRenderProgram program)
            {
                if (RuntimeEngine.InvokeOnMainThread(() => LinkRequested(program), "GLRenderProgram.LinkRequested"))
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

                if (!glBuf.IsReadyForRendering)
                    glBuf.EnsureStorageAllocatedForGpuCopy();

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
                if (!Use())
                {
                    if (IsAsyncBuildPending || !Data.LinkReady)
                        return;

                    if (Debug.ShouldLogEvery(ComputeDispatchNotLinkedLogKey, ComputeDispatchNotLinkedLogInterval))
                    {
                        uint bindingId = TryGetBindingId(out uint id) ? id : 0u;
                        Debug.OpenGL(
                            "[WARN] Cannot dispatch compute shader because program '{0}' is not linked yet. hash={1} linkReady={2} binding={3}",
                            GetProgramDebugName(),
                            Hash,
                            Data.LinkReady,
                            bindingId);
                    }
                    return;
                }
                if (textures is not null)
                    foreach (var (unit, texture, level, layer, access, format) in textures)
                        BindImageTexture(unit, texture, level, layer.HasValue, layer ?? 0, access, format);

                // Diagnostic: env-gated per-dispatch trace + optional glFinish + glGetError
                // to localize TDR-inducing compute dispatches. AutoFlush stream so entries
                // survive a GPU TDR / driver fail-fast.
                //   XRE_DISPATCH_TRACE=1   -> log every dispatch (pre+post)
                //   XRE_DISPATCH_FINISH=1  -> glFinish() after each dispatch (slow; pinpoints TDR)
                if (_dispatchTraceEnabled)
                {
                    string label = ResolveDispatchLabel();
                    TraceDispatch("pre", label, x, y, z, 0);
                    Api.DispatchCompute(x, y, z);
                    if (_dispatchFinishEnabled)
                    {
                        Api.Finish();
                        var err = Api.GetError();
                        TraceDispatch("post-finish", label, x, y, z, (uint)err);
                    }
                    else
                    {
                        var err = Api.GetError();
                        TraceDispatch("post", label, x, y, z, (uint)err);
                    }
                }
                else
                {
                    Api.DispatchCompute(x, y, z);
                }
            }

            private string ResolveDispatchLabel()
            {
                try
                {
                    string n = Data?.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(n))
                        return n;
                    var sh = Data?.Shaders?.Count > 0 ? Data.Shaders[0] : null;
                    if (sh is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(sh.Name))
                            return sh.Name!;
                        var fp = sh.Source?.FilePath;
                        if (!string.IsNullOrWhiteSpace(fp))
                            return System.IO.Path.GetFileName(fp);
                    }
                }
                catch { }
                return TryGetBindingId(out uint id) ? $"<prog#{id}>" : "<prog#unlinked>";
            }

            private static bool _dispatchTraceEnabled => RenderDiagnosticsFlags.DispatchTrace;
            private static bool _dispatchFinishEnabled => RenderDiagnosticsFlags.DispatchFinish;
            private static readonly object _dispatchTraceLock = new();
            private static System.IO.StreamWriter? _dispatchTraceWriter;

            private static void TraceDispatch(string stage, string? programName, uint x, uint y, uint z, uint glError)
            {
                try
                {
                    var w = _dispatchTraceWriter;
                    if (w is null)
                    {
                        lock (_dispatchTraceLock)
                        {
                            if (_dispatchTraceWriter is null)
                            {
                                string root = AppContext.BaseDirectory;
                                string logsDir = System.IO.Path.Combine(root, "Build", "Logs");
                                try { System.IO.Directory.CreateDirectory(logsDir); } catch { }
                                string path = System.IO.Path.Combine(logsDir, $"dispatch-trace_pid{Environment.ProcessId}.log");
                                var fs = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite, 4096, System.IO.FileOptions.WriteThrough);
                                _dispatchTraceWriter = new System.IO.StreamWriter(fs) { AutoFlush = true };
                                _dispatchTraceWriter.WriteLine($"# Dispatch trace started at {DateTime.UtcNow:O} (finish={_dispatchFinishEnabled})");
                            }
                            w = _dispatchTraceWriter;
                        }
                    }
                    lock (_dispatchTraceLock)
                    {
                        if (glError != 0)
                            w.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} tid={Environment.CurrentManagedThreadId} stage={stage} program={programName ?? "<null>"} groups=({x},{y},{z}) glError=0x{glError:X}");
                        else
                            w.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} tid={Environment.CurrentManagedThreadId} stage={stage} program={programName ?? "<null>"} groups=({x},{y},{z})");
                    }
                }
                catch { }
            }

            protected override void UnlinkData()
            {
                ShaderProgramLifecycleDiagnostics.RecordLogicalProgramDestroy();

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
                Data.TransformFeedbackLayoutChanged -= TransformFeedbackLayoutChanged;

                Data.Shaders.PostAnythingAdded -= ShaderAdded;
                Data.Shaders.PostAnythingRemoved -= ShaderRemoved;
                foreach (XRShader shader in Data.Shaders)
                    ShaderRemoved(shader);
            }

            public ulong Hash { get; private set; }
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
                Environment.GetEnvironmentVariable("XRE_DUMP_SLOW_SHADER_SOURCE"),
                "1",
                StringComparison.Ordinal);
            private static readonly bool TraceShaderCompletionPollGlCalls = string.Equals(
                Environment.GetEnvironmentVariable("XRE_TRACE_SHADER_COMPLETION_POLL_GLCALLS"),
                "1",
                StringComparison.Ordinal);
            private static readonly bool AllowRenderThreadDriverParallelSourceLinks = string.Equals(
                Environment.GetEnvironmentVariable("XRE_ALLOW_RENDER_THREAD_DRIVER_PARALLEL_SOURCE"),
                "1",
                StringComparison.Ordinal);
            private static readonly bool SharedLinkedProgramReuseEnabled = string.Equals(
                Environment.GetEnvironmentVariable("XRE_ENABLE_SHARED_LINKED_PROGRAM_REUSE"),
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
            private const int LargeSourceSharedContextPreferenceThresholdBytes = 128 * 1024;

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
                Environment.GetEnvironmentVariable("XRE_ALLOW_SYNC_SOURCE_RETRY_AFTER_ASYNC_TIMEOUT"),
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
            /// leaked into the driver — recovery from a driver-side hang is more
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

            private string GetProgramDebugName()
            {
                if (!string.IsNullOrWhiteSpace(Data.Name))
                    return Data.Name!;

                string stageTopology = GetShaderStageTopology();
                ShaderProgramVariantMetadata variant = Data.ShaderMetadata.Variant;
                string variantSegment = variant.HasVariant
                    ? string.Concat(
                        string.IsNullOrWhiteSpace(variant.Kind) ? "variant" : variant.Kind,
                        ":",
                        variant.VariantHash.ToString("x16", CultureInfo.InvariantCulture))
                    : "no-variant";

                return string.IsNullOrWhiteSpace(stageTopology)
                    ? string.Concat("<unnamed ", variantSegment, " hash=", Hash.ToString(CultureInfo.InvariantCulture), ">")
                    : string.Concat("<unnamed ", stageTopology, " ", variantSegment, " hash=", Hash.ToString(CultureInfo.InvariantCulture), ">");
            }

            private string GetProgramDescriptorLogKey()
                => string.IsNullOrWhiteSpace(Data.ProgramDescriptor.StableKey)
                    ? "<none>"
                    : Data.ProgramDescriptor.StableKey;

            private string GetCurrentHandleSourceLabel()
            {
                if (_sharedLinkedProgram is not null)
                    return "shared";
                if (_cachedProgram is not null || _preparedIsCached)
                    return "binary";
                return IsLinked ? "source" : "none";
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
                    GetProgramDebugName(),
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
                string programDebugName = GetProgramDebugName();
                string descriptorKey = GetProgramDescriptorLogKey();
                Debug.OpenGL(
                    $"[ShaderBackend] {result} program='{programDebugName}' hash={Hash} " +
                    $"backend={_activeBuildBackend ?? "<unknown>"} fingerprint={_activeBuildFingerprint ?? "<none>"} " +
                    $"descriptor={descriptorKey} handleSource={GetCurrentHandleSourceLabel()} " +
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
                        "[ShaderBackend] {0} program='{1}' hash={2} backend={3} fingerprint={4} descriptor={5} separable={6} hazard={7} shaderCount={8} shaderTypes={9} sourceBytes={10} sourceLines={11} shaderSources={12} queueMs={13:F2} compileMs={14:F2} linkMs={15:F2} binaryMs={16:F2} reflectionMs={17:F2}{18}.",
                        result,
                        programDebugName,
                        Hash,
                        _activeBuildBackend ?? "<unknown>",
                        _activeBuildFingerprint ?? "<none>",
                        descriptorKey,
                        Data.Separable,
                        IsKnownAsyncLinkHazard,
                        sourceSummary.ShaderCount,
                        sourceSummary.StageList,
                        sourceSummary.SourceBytes,
                        sourceSummary.SourceLines,
                        sourceSummary.SourceLabels,
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
                ShaderProgramSourceSummary sourceSummary = CollectShaderProgramSourceSummary(inputs);
                Debug.OpenGL(
                    $"[ShaderSlowLink] thresholdMs={SlowShaderLinkSourceDumpMilliseconds:F2} linkMs={linkMilliseconds:F2} " +
                    $"result={result} program='{programName}' hash={Hash} backend={backend} fingerprint={fingerprint} " +
                    $"separable={Data.Separable} hazard={IsKnownAsyncLinkHazard} shaderCount={shaderCount} " +
                    $"shaderTypes={sourceSummary.StageList} sourceBytes={sourceSummary.SourceBytes} sourceLines={sourceSummary.SourceLines} " +
                    $"sourceLabels={sourceSummary.SourceLabels} dumpSources={DumpSlowShaderSources}" +
                    (string.IsNullOrWhiteSpace(failureReason) ? "." : $" failure='{failureReason}'."));

                if (!DumpSlowShaderSources)
                    return;

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

                return shaderData.TryGetOptimizedSource(out string optimizedSource, logFailures: false)
                    ? GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(optimizedSource, shaderData.Type, Data.Separable)
                    : null;
            }

            private readonly record struct ShaderProgramSourceSummary(
                int ShaderCount,
                long SourceBytes,
                int SourceLines,
                string StageList,
                string SourceLabels);

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
                    "[ShaderLink] {0} program='{1}' hash={2} descriptor={3} programId={4} backend={5} separable={6} hazard={7} shaderCount={8} shaderTypes={9} sourceBytes={10} sourceLines={11} shaderSources={12} binaryBytes={13} binaryFormat={14} fingerprint={15} frame={16} renderThread={17}{18}.",
                    eventName,
                    GetProgramDebugName(),
                    Hash,
                    GetProgramDescriptorLogKey(),
                    programId,
                    backend ?? "<unknown>",
                    Data.Separable,
                    IsKnownAsyncLinkHazard,
                    sourceSummary.ShaderCount,
                    sourceSummary.StageList,
                    sourceSummary.SourceBytes,
                    sourceSummary.SourceLines,
                    sourceSummary.SourceLabels,
                    binaryBytes,
                    binaryFormat ?? "<none>",
                    fingerprint ?? _activeBuildFingerprint ?? "<none>",
                    RuntimeEngine.Rendering.State.RenderFrameId,
                    RuntimeEngine.IsRenderThread,
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

                    return new ShaderProgramSourceSummary(inputs.Length, inputBytes, inputLines, inputStages.ToString(), "<prepared-inputs>");
                }

                int shaderCount = Data.Shaders.Count;
                if (shaderCount == 0)
                    return new ShaderProgramSourceSummary(0, 0, 0, "<none>", "<none>");

                long bytes = 0;
                int lines = 0;
                var stages = new StringBuilder(shaderCount * 16);
                var sourceLabels = new StringBuilder(shaderCount * 48);
                for (int index = 0; index < shaderCount; index++)
                {
                    if (index > 0)
                    {
                        stages.Append('|');
                        sourceLabels.Append('|');
                    }

                    XRShader shaderData = Data.Shaders[index];
                    stages.Append(shaderData.Type);
                    sourceLabels.Append(shaderData.Type)
                        .Append(':')
                        .Append(string.IsNullOrWhiteSpace(shaderData.Source?.FilePath)
                            ? "<inline>"
                            : shaderData.Source.FilePath);
                    string? source = null;
                    if (_shaderCache.TryGetValue(shaderData, out GLShader? shader) && shader is not null)
                    {
                        source = shader.ResolveFullSource();
                    }
                    else if (shaderData.TryGetOptimizedSource(out string optimizedSource, logFailures: false))
                    {
                        source = GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(optimizedSource, shaderData.Type, Data.Separable);
                    }

                    bytes += CountUtf8Bytes(source);
                    lines += CountLines(source);
                }

                return new ShaderProgramSourceSummary(shaderCount, bytes, lines, stages.ToString(), sourceLabels.ToString());
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
                if (ShouldLogRenderingShaderGlCall(callName, detail, elapsedMilliseconds))
                    LogRenderingProgramGlCall(callName, programId, elapsedMilliseconds, detail);
                return elapsedMilliseconds;
            }

            private void LogRenderingProgramGlCall(string callName, uint programId, double elapsedMilliseconds, string? detail = null)
            {
                bool renderThread = RuntimeEngine.IsRenderThread;
                Debug.Rendering(
                    EOutputVerbosity.Verbose,
                    false,
                    "[ShaderGLCall] call={0} program='{1}' hash={2} programId={3} separable={4} elapsedMs={5:F3} renderThread={6} renderThreadStallMs={7:F3}{8}.",
                    callName,
                    GetProgramDebugName(),
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
                if (!ShouldLogRenderingShaderGlCall(callName, detail, elapsedMilliseconds))
                    return elapsedMilliseconds;

                bool renderThread = RuntimeEngine.IsRenderThread;
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

            public bool Link(bool force = false, bool nonBlocking = false)
            {
                using var prof = RuntimeEngine.Profiler.Start("GLRenderProgram.Link", ProfilerScopeKind.ConditionalLoop);

                if (IsLinked && !_replacementProgramPending)
                {
                    if (HasCompletedBuildPendingState)
                        ClearCompletedBuildPendingState();
                    return true;
                }

                RuntimeEngine.Rendering.Stats.RecordShaderVariant(requested: true);

                if (IsLinkPreparationPending)
                {
                    RuntimeEngine.Rendering.Stats.RecordShaderVariant(warming: true);
                    return ReturnPendingBuildResult();
                }

                if (_asyncBinaryUploadQueueWaitPending && _cachedProgram is { } sharedCandidate)
                {
                    if (TryUseSharedLinkedProgram(sharedCandidate))
                    {
                        _asyncBinaryUploadQueueWaitPending = false;
                        return true;
                    }

                    GLProgramBinaryUploadQueue? uploadQueue = Renderer.ProgramBinaryUploadQueue;
                    bool binaryUploadQueueUnavailable = uploadQueue is not { IsAvailable: true } ||
                        uploadQueue.IsDisabledAfterFailures;
                    if (!binaryUploadQueueUnavailable && uploadQueue is not null)
                    {
                        if (!uploadQueue.CanEnqueue || uploadQueue.IsCacheKeyReserved(sharedCandidate.CacheKey))
                        {
                            ReportSlowAsyncPending("binary-upload queue wait");
                            return ReturnPendingBuildResult();
                        }
                    }

                    if (binaryUploadQueueUnavailable &&
                        !string.IsNullOrWhiteSpace(sharedCandidate.CacheKey) &&
                        AsyncBinaryUploadTimeoutCacheKeys.TryAdd(sharedCandidate.CacheKey, 0))
                    {
                        DeleteFromBinaryShaderCache(sharedCandidate.CacheKey, sharedCandidate.Format);
                    }

                    _asyncBinaryUploadQueueWaitPending = false;
                }

                Exception? linkPreparationException = _linkPreparationFailure;
                bool linkPreparationFailed = linkPreparationException is not null;
                if (linkPreparationException is not null)
                {
                    Debug.OpenGLWarning($"GLRenderProgram link preparation failed for '{GetProgramDebugName()}': {linkPreparationException.Message}");
                    _linkPreparationFailure = null;
                }

                // Check for completed async binary upload from the shared context thread.
                if (_asyncBinaryUploadPending)
                {
                    var queue = Renderer.ProgramBinaryUploadQueue;
                    if (TryGetBuildBindingId(out uint pendingId) && (queue is null || queue.IsWorkerUnhealthy || !queue.IsAvailable))
                    {
                        string reason = queue is null
                            ? "binary upload queue unavailable"
                            : "binary upload worker unhealthy";
                        AbandonAsyncBinaryUpload(queue, pendingId, reason);
                    }
                    else if (queue is not null && TryGetBuildBindingId(out pendingId) && queue.TryGetResult(pendingId, out var asyncResult))
                    {
                        _asyncBinaryUploadPending = false;
                        _asyncBinaryUploadQueueWaitPending = false;
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
                            RuntimeEngine.Rendering.Stats.RecordShaderVariant(linked: true, loadedFromDiskCache: true);
                            CompleteBuildTelemetry(true, binaryLoadMilliseconds: asyncResult.LoadMilliseconds, reflectionMilliseconds: reflectionMilliseconds);
                            ClearCompletedBuildPendingState();
                            return true;
                        }
                        else
                        {
                            string failedCacheKey = _cachedProgram?.CacheKey ?? asyncResult.CacheKey;
                            uint failedBinaryBytes = _cachedProgram?.Length ?? 0u;
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
                                binaryBytes: failedBinaryBytes,
                                binaryFormat: asyncResult.Format.ToString());
                            RuntimeEngine.Rendering.Stats.RecordShaderVariant(failed: true);
                            CompleteBuildTelemetry(false, binaryLoadMilliseconds: asyncResult.LoadMilliseconds, failureReason: asyncResult.ErrorLog);
                            DeleteFromBinaryShaderCache(failedCacheKey, asyncResult.Format);
                            _cachedProgram = null;
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
                            if (ShouldLogRenderingShaderLinkVerbose())
                                Debug.OpenGL($"[ShaderCache] READY hash={Hash}, shared-context compileMs={compileResult.CompileMilliseconds:F2}, linkMs={compileResult.LinkMilliseconds:F2}.");
                            AdoptLinkedBuildProgram(pendingId2);
                            IsLinked = true;
                            long reflectionStart = Stopwatch.GetTimestamp();
                            using var uniformsProf = RuntimeEngine.Profiler.Start("GLRenderProgram.Link.CacheActiveUniforms", ProfilerScopeKind.OneOffInvoke);
                            CacheActiveUniforms();
                            double reflectionMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - reflectionStart);
                            CacheBinary(pendingId2, compileResult.ProgramBinary);
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
                            RuntimeEngine.Rendering.Stats.RecordShaderVariant(linked: true, generatedThisRun: true);
                            CompleteBuildTelemetry(
                                true,
                                compileResult.CompileMilliseconds,
                                compileResult.LinkMilliseconds,
                                reflectionMilliseconds: reflectionMilliseconds);
                            SynchronousSourceRetryHashes.TryRemove(Hash, out _);
                            DriverParallelSourceTimeouts.TryRemove(Hash, out _);
                            InFlightCompilations.TryRemove(Hash, out _);
                            ClearCompletedBuildPendingState();
                            return true;
                        }
                        else
                        {
                            CompleteUberBackendTracking(false, compileResult.ErrorLog, compileResult.CompileMilliseconds, compileResult.LinkMilliseconds);
                            string errorKind = compileResult.Status == GLProgramCompileLinkQueue.CompileStatus.CompileFailed
                                ? "compile" : "link";
                            bool sharedContextAbandonedLink = compileResult.Status == GLProgramCompileLinkQueue.CompileStatus.LinkFailed &&
                                IsSharedContextAbandonedLink(compileResult.ErrorLog);
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
                            RuntimeEngine.Rendering.Stats.RecordShaderVariant(failed: true);
                            CompleteBuildTelemetry(
                                false,
                                compileResult.CompileMilliseconds,
                                compileResult.LinkMilliseconds,
                                failureReason: compileResult.ErrorLog);
                            if (sharedContextAbandonedLink)
                            {
                                DeferRetryAfterSharedContextSourceTimeout(pendingId2);
                            }
                            else
                            {
                                MarkHashFailed(compileResult.ErrorLog ?? $"async {errorKind} failed");
                                InFlightCompilations.TryRemove(Hash, out _);
                                _asyncCompileDuplicateHashWaitPending = false;
                                MarkBuildFailed();
                            }
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
                    isCached = _preparedIsCached &&
                        !ShouldBypassBinaryCacheForLiveUberVariant() &&
                        !IsAsyncBinaryUploadTimedOutCacheKey(cacheKey);
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
                        using (RuntimeEngine.Profiler.Start("GLRenderProgram.Link.CacheLookup", ProfilerScopeKind.OneOffInvoke))
                        {
                            using (RuntimeEngine.Profiler.Start("GLRenderProgram.Link.CalcHash", ProfilerScopeKind.OneOffInvoke))
                                Hash = CalcShaderSourceHash();
                        }
                        _hashComputed = true;
                    }

                    bool bypassBinaryCache = ShouldBypassBinaryCacheForLiveUberVariant();
                    if (!bypassBinaryCache && RuntimeEngine.Rendering.Settings.AllowBinaryProgramCaching)
                    {
                        cacheKey = BuildBinaryCacheKey(Hash);
                        if (!IsAsyncBinaryUploadTimedOutCacheKey(cacheKey))
                        {
                            using (RuntimeEngine.Profiler.Start("GLRenderProgram.Link.BinaryCacheLookup", ProfilerScopeKind.OneOffInvoke))
                                isCached = BinaryCache?.TryGetValue(cacheKey, out binProg) ?? false;
                        }
                    }
                }

                if (isCached && Renderer.ProgramBinaryUploadQueue?.IsDisabledAfterFailures == true)
                {
                    GLEnum format = binProg.Format;
                    PublishBackendStatus(
                        EShaderProgramBackendStage.CacheMiss,
                        "BinaryCache",
                        "cached binary upload disabled for this session",
                        fingerprint: binProg.CacheKey);
                    LogRenderingProgramBuildEvent(
                        "BINARY_CACHE_SKIPPED_ASYNC_UPLOAD_DISABLED",
                        "BinaryCache",
                        "cached binary upload disabled for this session",
                        binProg.CacheKey,
                        bindingId,
                        binaryBytes: binProg.Length,
                        binaryFormat: format.ToString());
                    isCached = false;
                }

                if (isCached)
                {
                    _asyncCompileDuplicateHashWaitPending = false;
                    using var cacheLoadProf = RuntimeEngine.Profiler.Start("GLRenderProgram.Link.LoadCachedBinary", ProfilerScopeKind.OneOffInvoke);
                    _cachedProgram = binProg;
                    GLEnum format = binProg.Format;
                    ShaderProgramLifecycleDiagnostics.RecordBinaryCacheHit();
                    RuntimeEngine.Rendering.Stats.RecordShaderVariant(warming: true, loadedFromDiskCache: true);
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
                    {
                        _asyncBinaryUploadQueueWaitPending = false;
                        return true;
                    }

                    if (!RuntimeEngine.Rendering.Stats.Vram.CanAllocateVram(binProg.Length, 0, out long projectedBytes, out long budgetBytes))
                    {
                        _asyncBinaryUploadQueueWaitPending = false;
                        Debug.OpenGLWarning($"[VRAM Budget] Skipping cached program binary load for hash {Hash} ({binProg.Length} bytes). Projected={projectedBytes} bytes, Budget={budgetBytes} bytes. Deleting from cache.");
                        DeleteFromBinaryShaderCache(binProg.CacheKey, format);
                    }
                    else
                    {
                        var uploadQueue = Renderer.ProgramBinaryUploadQueue;
                        OpenGLShaderLinkBackendSelection selection = OpenGLShaderLinkBackendSelector.Select(new OpenGLShaderLinkBackendContext(
                            RuntimeEngine.Rendering.Settings.OpenGLShaderLinkStrategy,
                            RuntimeEngine.Rendering.Settings.AsyncProgramCompilation,
                            RuntimeEngine.Rendering.Settings.AllowBinaryProgramCaching,
                            RuntimeEngine.Rendering.Settings.AsyncProgramBinaryUpload,
                            HasBinaryCacheHit: true,
                            BinaryUploadAvailable: uploadQueue is { IsAvailable: true, IsDisabledAfterFailures: false },
                            BinaryUploadCanEnqueue: uploadQueue is { CanEnqueue: true },
                            DriverParallelAvailable: Renderer.UseDriverParallelShaderCompile,
                            SharedContextCompileAvailable: Renderer.ProgramCompileLinkQueue is { IsAvailable: true },
                            SharedContextCompileCanEnqueue: Renderer.ProgramCompileLinkQueue is { CanEnqueue: true },
                            CompileInputsReady: _preparedCompileInputs is { Length: > 0 },
                            IsKnownAsyncLinkHazard: IsKnownAsyncLinkHazard,
                            PreferSharedContextForLargeSource: false,
                            HashPreviouslyFailed: false,
                            AllowSynchronousSourceLink: false));

                        if (selection.Lane == EOpenGLProgramBuildLane.BinaryQueueBackpressure)
                        {
                            _asyncBinaryUploadQueueWaitPending = true;
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
                                _asyncBinaryUploadQueueWaitPending = true;
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

                            _asyncBinaryUploadQueueWaitPending = false;
                            BeginBuildTelemetry("BinaryUploadAsync", binProg.CacheKey);
                            EProgramPriority priority = Data?.Priority ?? EProgramPriority.Main;
                            uploadQueue.EnqueueUpload(bindingId, binProg.Binary, format, binProg.Length, Hash, binProg.CacheKey, priority);
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

                        _asyncBinaryUploadQueueWaitPending = false;
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
                        using (RuntimeEngine.Profiler.Start("GLRenderProgram.Link.ProgramBinary", ProfilerScopeKind.OneOffInvoke))
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
                            RuntimeEngine.Rendering.Stats.RecordShaderVariant(failed: true);
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
                            RuntimeEngine.Rendering.Stats.RecordShaderVariant(failed: true);
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
                            RuntimeEngine.Rendering.Stats.RecordShaderVariant(linked: true, loadedFromDiskCache: true);
                            CompleteBuildTelemetry(true, binaryLoadMilliseconds: binaryLoadMilliseconds, reflectionMilliseconds: reflectionMilliseconds);
                            ClearCompletedBuildPendingState();
                            return true;
                        }
                    }
                }
                else
                {
                    _asyncBinaryUploadQueueWaitPending = false;
                    // Phase 3: short-circuit known-failed hashes BEFORE the
                    // BINARY_CACHE_MISS log + ShaderProgramSourceSummary so repeated
                    // failed hashes do not regenerate full miss records every frame.
                    if (Failed.ContainsKey(Hash))
                    {
                        EmitFailedHashSkipLog(cacheKey, bindingId);
                        string failedHashReason = GetFailedHashFailureReason(Hash);
                        PublishBackendStatus(
                            EShaderProgramBackendStage.Failed,
                            "Source",
                            "hash previously failed",
                            failedHashReason,
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
                            RuntimeEngine.Rendering.Settings.AllowBinaryProgramCaching ? "binary cache miss" : "binary cache disabled",
                            fingerprint: cacheKey);
                        LogRenderingProgramBuildEvent(
                            "BINARY_CACHE_MISS",
                            "BinaryCache",
                            RuntimeEngine.Rendering.Settings.AllowBinaryProgramCaching ? "binary cache miss" : "binary cache disabled",
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
                        RuntimeEngine.Rendering.Stats.RecordShaderVariant(warming: true);
                        if (ShouldLogRenderingShaderLinkVerbose())
                            Debug.OpenGL($"[ShaderCache] MISS hash={Hash}, compiling {_shaderCache.Count} shader(s) from source.");
                    }

                    var compileQueue = Renderer.ProgramCompileLinkQueue;
                    bool isKnownAsyncLinkHazard = IsKnownAsyncLinkHazard;
                    bool forceSynchronousSourceRetry = Hash != 0 && SynchronousSourceRetryHashes.ContainsKey(Hash);
                    bool driverParallelSourceTimedOut = Hash != 0 && DriverParallelSourceTimeouts.ContainsKey(Hash);
                    bool sharedContextSourceTimedOut = Hash != 0 && SharedContextLargeSourceTimeouts.ContainsKey(Hash);
                    bool forceDriverParallelSourceRetry =
                        sharedContextSourceTimedOut &&
                        !driverParallelSourceTimedOut &&
                        !forceSynchronousSourceRetry &&
                        !isKnownAsyncLinkHazard &&
                        Renderer.UseDriverParallelShaderCompile;
                    bool driverParallelSourceAvailable =
                        Renderer.UseDriverParallelShaderCompile &&
                        (AllowRenderThreadDriverParallelSourceLinks || forceDriverParallelSourceRetry);
                    bool sharedContextCompileAvailable = compileQueue is { IsAvailable: true };
                    GLProgramCompileLinkQueue.ShaderInput[]? inputs = _preparedCompileInputs;
                    // Hazardous graphics programs (single-stage separable), and
                    // oversized generated graphics programs, are routed to the
                    // shared-context lane by the selector when the queue is available.
                    // Prepare their inputs unconditionally so the selector can make
                    // that decision from source size. The queue still rejects compute
                    // hazards, which fall through to the synchronous path below.
                    bool wantsSharedSourceInputs = sharedContextCompileAvailable &&
                        (Renderer.UseSharedContextProgramCompileLinkQueue ||
                         RuntimeEngine.Rendering.Settings.OpenGLShaderLinkStrategy == EOpenGLShaderLinkStrategy.SharedContext ||
                         Renderer.UseDriverParallelShaderCompile ||
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
                    bool preferSharedContextForLargeSource = Renderer.UseDriverParallelShaderCompile &&
                        sharedContextCompileAvailable &&
                        ShouldPreferSharedContextForLargeSource(Hash, inputs);
                    EProgramPriority priority = Data?.Priority ?? EProgramPriority.Main;
                    GLProgramCompileLinkQueue.TransformFeedbackLinkInfo transformFeedback;
                    try
                    {
                        transformFeedback = BuildTransformFeedbackLinkInfo();
                    }
                    catch (Exception ex)
                    {
                        InFlightCompilations.TryRemove(Hash, out _);
                        string message = "Invalid OpenGL transform feedback layout: " + ex.Message;
                        Debug.OpenGLWarning(message);
                        PublishBackendStatus(
                            EShaderProgramBackendStage.Failed,
                            "TransformFeedback",
                            "transform feedback layout validation failed",
                            message,
                            fingerprint: cacheKey);
                        LogRenderingProgramBuildEvent(
                            "TRANSFORM_FEEDBACK_LAYOUT_FAILED",
                            "TransformFeedback",
                            message,
                            cacheKey,
                            bindingId,
                            inputs);
                        CompleteBuildTelemetry(false, failureReason: message);
                        MarkBuildFailed();
                        return IsLinked;
                    }

                    OpenGLShaderLinkBackendSelection sourceSelection = OpenGLShaderLinkBackendSelector.Select(new OpenGLShaderLinkBackendContext(
                        RuntimeEngine.Rendering.Settings.OpenGLShaderLinkStrategy,
                        RuntimeEngine.Rendering.Settings.AsyncProgramCompilation,
                        RuntimeEngine.Rendering.Settings.AllowBinaryProgramCaching,
                        RuntimeEngine.Rendering.Settings.AsyncProgramBinaryUpload,
                        HasBinaryCacheHit: false,
                        BinaryUploadAvailable: Renderer.ProgramBinaryUploadQueue is { IsAvailable: true, IsDisabledAfterFailures: false },
                        BinaryUploadCanEnqueue: Renderer.ProgramBinaryUploadQueue is { CanEnqueue: true },
                        DriverParallelAvailable: driverParallelSourceAvailable,
                        SharedContextCompileAvailable: sharedContextCompileAvailable,
                        SharedContextCompileCanEnqueue: sharedContextCompileAvailable && compileQueue?.CanEnqueuePriority(priority) == true,
                        CompileInputsReady: inputs is { Length: > 0 },
                        IsKnownAsyncLinkHazard: isKnownAsyncLinkHazard,
                        PreferSharedContextForLargeSource: preferSharedContextForLargeSource,
                        HashPreviouslyFailed: false,
                        AllowSynchronousSourceLink: false,
                        ForceSynchronousSourceRetry: forceSynchronousSourceRetry,
                        ForceDriverParallelSourceRetry: forceDriverParallelSourceRetry));
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

                        bool setBinaryRetrievableHint = ShouldSetProgramBinaryRetrievableHintForSourceBuild();
                        if (!compileQueue.TryEnqueueCompileAndLink(
                            bindingId,
                            inputs,
                            priority,
                            setBinaryRetrievableHint,
                            transformFeedback,
                            out string? rejectReason))
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
                            if (ShouldLogRenderingShaderLinkVerbose())
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
                        if (nonBlocking && !forceSynchronousSourceRetry)
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
                    // Timeout recovery must not call glMaxShaderCompilerThreads* here:
                    // some drivers can block inside that call while a previous
                    // driver-parallel link is already wedged. The forced synchronous
                    // retry below instead avoids the async lane in engine code and
                    // lets normal compile/link status queries perform the blocking wait.
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
                    using (RuntimeEngine.Profiler.Start("GLRenderProgram.Link.GenerateShaders", ProfilerScopeKind.OneOffInvoke))
                    {
                        if (TryResolveUberVariantHash(out ulong variantHash))
                            BeginUberBackendCompileTracking(variantHash);

                        bool separableProgram = Data?.Separable ?? false;
                        foreach (GLShader shader in _shaderCache.Values)
                        {
                            shader.PrepareCompileVariant(separableProgram);
                            if (shader.Data.GenerateAsync)
                                RuntimeEngine.EnqueueMainThreadTask(shader.Generate);
                            else
                                shader.Generate();
                        }
                    }

                    // Driver-parallel linking is only safe for shapes outside the
                    // known hazard set.
                    bool useDriverParallelLink =
                        sourceSelection.Lane == EOpenGLProgramBuildLane.DriverParallelSource &&
                        Renderer.UseDriverParallelShaderCompile &&
                        !isKnownAsyncLinkHazard &&
                        !forceSynchronousSourceRetry;

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
                        RuntimeEngine.Rendering.Stats.RecordShaderVariant(failed: true);
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
                    using (RuntimeEngine.Profiler.Start("GLRenderProgram.Link.AttachShaders", ProfilerScopeKind.OneOffInvoke))
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
                        using var linkProf = RuntimeEngine.Profiler.Start("GLRenderProgram.Link.DriverLinkProgram", ProfilerScopeKind.OneOffInvoke);
                        EnsureProgramBinaryRetrievableHintForSourceBuild(bindingId, _activeBuildBackend ?? "SynchronousSource");
                        ApplyTransformFeedbackVaryings(
                            bindingId,
                            transformFeedback,
                            $"backend={_activeBuildBackend ?? "SynchronousSource"} phase=source-sync-transform-feedback-varyings");
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
                            RuntimeEngine.Rendering.Stats.RecordShaderVariant(failed: true);
                            CompleteBuildTelemetry(false, compileMilliseconds, linkMilliseconds, failureReason: linkError);
                            MarkHashFailed(linkError);
                            sourceBuildFailed = true;
                        }
                        else
                        {
                            AdoptLinkedBuildProgram(bindingId);
                            IsLinked = true;
                            SynchronousSourceRetryHashes.TryRemove(Hash, out _);
                            DriverParallelSourceTimeouts.TryRemove(Hash, out _);
                            long reflectionStart = Stopwatch.GetTimestamp();
                            using var uniformsProf = RuntimeEngine.Profiler.Start("GLRenderProgram.Link.CacheActiveUniforms", ProfilerScopeKind.OneOffInvoke);
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
                            RuntimeEngine.Rendering.Stats.RecordShaderVariant(linked: true, generatedThisRun: true);
                            CompleteBuildTelemetry(true, compileMilliseconds, linkMilliseconds, reflectionMilliseconds: reflectionMilliseconds);
                            ClearCompletedBuildPendingState();
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
                        RuntimeEngine.Rendering.Stats.RecordShaderVariant(failed: true);
                        CompleteBuildTelemetry(false, compileMilliseconds, failureReason: "One or more shaders failed to compile.");
                        MarkHashFailed("One or more shaders failed to compile.");
                        sourceBuildFailed = true;
                    }

                    using (RuntimeEngine.Profiler.Start("GLRenderProgram.Link.DetachShaders", ProfilerScopeKind.OneOffInvoke))
                    {
                        DetachShaders(bindingId, [.. attachedShaderIds]);
                    }
                    using (RuntimeEngine.Profiler.Start("GLRenderProgram.Link.DestroyShaderObjects", ProfilerScopeKind.OneOffInvoke))
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

            private void TransformFeedbackLayoutChanged(XRRenderProgram program)
            {
                if (RuntimeEngine.InvokeOnMainThread(() => TransformFeedbackLayoutChanged(program), "GLRenderProgram.TransformFeedbackLayoutChanged"))
                    return;

                ReleaseAsyncLinkState();
                InvalidatePreparedLinkData();
                _hashComputed = false;
                IsLinked = false;

                if (Data.LinkReady)
                    Relink();
            }

            private void Relink()
            {
                if (RuntimeEngine.InvokeOnMainThread(Relink, "GLRenderProgram.Relink"))
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
                string programName = GetProgramDebugName();
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
