using XREngine.Extensions;
using Silk.NET.OpenGL;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using XREngine;
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
                using (Engine.Profiler.Start("GLRenderProgram.Link.CacheLookup"))
                {
                    using (Engine.Profiler.Start("GLRenderProgram.Link.CalcHash"))
                        hash = CalcShaderSourceHash();

                    bool bypassBinaryCache = ShouldBypassBinaryCacheForLiveUberVariant();
                    if (!bypassBinaryCache && Engine.Rendering.Settings.AllowBinaryProgramCaching)
                    {
                        cacheKey = BuildBinaryCacheKey(hash);
                        using (Engine.Profiler.Start("GLRenderProgram.Link.BinaryCacheLookup"))
                            isCached = BinaryCache?.TryGetValue(cacheKey, out binProg) ?? false;
                    }
                }

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

            /// <summary>
            /// Tracks hashes currently being compiled from source so that duplicate
            /// GLRenderProgram instances with the same shader source can defer instead
            /// of redundantly compiling. Cleared when compilation succeeds (binary cached)
            /// or fails (added to <see cref="Failed"/>).
            /// </summary>
            private static readonly ConcurrentDictionary<ulong, byte> InFlightCompilations = new();
            private static readonly ConcurrentDictionary<GLRenderProgram, byte> PendingAsyncPrograms = new();
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
                        api.GetProgram(programId, (GLEnum)GLShader.GL_COMPLETION_STATUS_ARB, out int complete);
                        if (complete == 0)
                            return false;

                        DetachShaders(api, programId, shaderIds);
                        api.DeleteProgram(programId);
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
                   _asyncLinkPhase != EAsyncLinkPhase.Idle;

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

                    program.Link();
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
                            delete.Renderer.Api.DeleteProgram(delete.ProgramId);
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
                        using var prof = Engine.Profiler.Start("GLRenderProgram.ContinueAsyncLink.PollCompile");

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
                            Failed.TryAdd(Hash, 0);
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
                                Api.AttachShader(bindingId, shaderId);
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
                            Failed.TryAdd(Hash, 0);
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
                        Api.LinkProgram(bindingId);
                        _asyncLinkedProgramId = bindingId;
                        _asyncAttachedShaderIds = [.. attachedShaderIds];
                        _asyncLinkPhase = EAsyncLinkPhase.Linking;
                        RestartAsyncPendingDiagnostics();
                        RegisterPendingAsyncProgram();
                        return ReturnPendingBuildResult(); // Will poll link completion next frame
                    }
                    case EAsyncLinkPhase.Linking:
                    {
                        using var prof = Engine.Profiler.Start("GLRenderProgram.ContinueAsyncLink.PollLink");

                        uint linkedProgramId = _asyncLinkedProgramId != 0 ? _asyncLinkedProgramId : bindingId;

                        Api.GetProgram(linkedProgramId, (GLEnum)GLShader.GL_COMPLETION_STATUS_ARB, out int complete);
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

                        Api.GetProgram(linkedProgramId, GLEnum.LinkStatus, out int status);
                        bool linked = status != 0;
                        string? linkError = null;
                        if (linked)
                        {
                            AdoptLinkedBuildProgram(linkedProgramId);
                            IsLinked = true;
                            long reflectionStart = Stopwatch.GetTimestamp();
                            CacheActiveUniforms();
                            double reflectionMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - reflectionStart);
                            CacheBinary(linkedProgramId);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Ready,
                                "DriverParallelSource",
                                "driver-parallel link completed");
                            CompleteBuildTelemetry(true, reflectionMilliseconds: reflectionMilliseconds);
                        }
                        else
                        {
                            Api.GetProgramInfoLog(linkedProgramId, out linkError);
                        }

                        return CompleteAsyncLink(linkedProgramId, linked, linkError, "Async link failed");
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
                    Api.Flush();
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

                Failed.TryAdd(Hash, 0);
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

            private bool CompleteAsyncLink(uint linkedProgramId, bool linked, string? linkError, string? failureKind)
            {
                if (!linked)
                {
                    if (string.IsNullOrWhiteSpace(linkError))
                        Api.GetProgramInfoLog(linkedProgramId, out linkError);

                    PrintLinkDebug(linkedProgramId, linkError, failureKind);
                    PublishBackendStatus(
                        EShaderProgramBackendStage.Failed,
                        _activeBuildBackend ?? "DriverParallelSource",
                        failureKind,
                        linkError);
                    CompleteBuildTelemetry(false, failureReason: linkError);
                    Failed.TryAdd(Hash, 0);
                }

                CompleteUberBackendTracking(linked, linkError);
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
                Api.GetProgram(programId, GLEnum.LinkStatus, out int linkStatus);
                if (linkStatus != 0)
                {
                    failureReason = null;
                    return true;
                }

                Api.GetProgramInfoLog(programId, out string? infoLog);
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
            /// Programs known to hang NVIDIA's <c>GL_ARB_parallel_shader_compile</c>
            /// link worker. Covers:
            ///  * Single-stage separable programs (imported model materials whose
            ///    vertex/fragment stages are split into individual programs).
            ///  * Compute programs (always single-stage; NVIDIA's parallel-link
            ///    worker can leave the program waiting forever, and the first
            ///    <c>glUseProgram</c>/<c>glDispatchCompute</c> implicitly waits for
            ///    completion which deadlocks the render thread — observed during
            ///    BVH/physics-chain dispatch in <c>GlobalPreRender</c>).
            ///  * Any program with a single attached shader, which exhibits the
            ///    same hazard regardless of the <c>Separable</c> flag.
            /// For these we bypass both driver-parallel and shared-source lanes.
            /// The guarded synchronous path temporarily disables driver compiler
            /// threads, links on the render thread under the per-frame shader-work
            /// budget, and leaves any previously linked hot-reload program visible.
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

                _activeBuildBackend = null;
                _activeBuildFingerprint = null;
                _activeBuildQueueTimestamp = 0;
            }

            private static double StopwatchTicksToMilliseconds(long ticks)
                => ticks <= 0L ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;

            private static double StopwatchTicksToSeconds(long ticks)
                => ticks <= 0L ? 0.0 : (double)ticks / Stopwatch.Frequency;

            private void DetachShaders(uint programId, ReadOnlySpan<uint> attachedShaderIds)
                => DetachShaders(Api, programId, attachedShaderIds);

            private static void DetachShaders(GL api, uint programId, ReadOnlySpan<uint> attachedShaderIds)
            {
                if (programId == 0 || attachedShaderIds.Length == 0)
                    return;

                HashSet<uint> detachedShaderIds = [];
                foreach (uint shaderId in attachedShaderIds)
                {
                    if (shaderId == 0 || !detachedShaderIds.Add(shaderId))
                        continue;

                    api.DetachShader(programId, shaderId);
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

                    api.DeleteShader(shaderId);
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
                if (TryGetBindingId(out uint oldProgramId))
                {
                    previousProgramId = oldProgramId;
                    RemoveCacheEntry(oldProgramId);
                }

                _bindingId = linkedProgramId;
                Cache[linkedProgramId] = this;
                _replacementProgramId = 0;
                _replacementProgramPending = false;

                _attribCache.Clear();
                _uniformCache.Clear();
                _failedAttributes.Clear();
                _failedUniforms.Clear();
                _locationNameCache.Clear();
                _uniformMetadata.Clear();
                _activeSamplerUniforms = [];
                _explicitAttributeLocations.Clear();
                _explicitAttributeLocationsResolved = false;

                if (previousProgramId != 0)
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

            private bool ReturnPendingBuildResult()
                => IsLinked && _replacementProgramPending;

            private void MarkBuildFailed()
            {
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

            public bool Link(bool force = false)
            {
                using var prof = Engine.Profiler.Start("GLRenderProgram.Link");

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
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Ready,
                                "BinaryUploadAsync",
                                "cached binary loaded asynchronously",
                                compileMilliseconds: 0.0,
                                linkMilliseconds: 0.0,
                                fingerprint: asyncResult.CacheKey);
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
                            using var uniformsProf = Engine.Profiler.Start("GLRenderProgram.Link.CacheActiveUniforms");
                            CacheActiveUniforms();
                            double reflectionMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - reflectionStart);
                            CacheBinary(pendingId2);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Ready,
                                "SharedContextSource",
                                "source compile/link completed on shared context",
                                compileMilliseconds: compileResult.CompileMilliseconds,
                                linkMilliseconds: compileResult.LinkMilliseconds);
                            CompleteBuildTelemetry(
                                true,
                                compileResult.CompileMilliseconds,
                                compileResult.LinkMilliseconds,
                                reflectionMilliseconds: reflectionMilliseconds);
                            InFlightCompilations.TryRemove(Hash, out _);
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
                            CompleteBuildTelemetry(
                                false,
                                compileResult.CompileMilliseconds,
                                compileResult.LinkMilliseconds,
                                failureReason: compileResult.ErrorLog);
                            Failed.TryAdd(Hash, 0);
                            InFlightCompilations.TryRemove(Hash, out _);
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
                        using (Engine.Profiler.Start("GLRenderProgram.Link.CacheLookup"))
                        {
                            using (Engine.Profiler.Start("GLRenderProgram.Link.CalcHash"))
                                Hash = CalcShaderSourceHash();
                        }
                        _hashComputed = true;
                    }
                    bool bypassBinaryCache = ShouldBypassBinaryCacheForLiveUberVariant();
                    if (!bypassBinaryCache && Engine.Rendering.Settings.AllowBinaryProgramCaching)
                    {
                        cacheKey = BuildBinaryCacheKey(Hash);
                        using (Engine.Profiler.Start("GLRenderProgram.Link.BinaryCacheLookup"))
                            isCached = BinaryCache?.TryGetValue(cacheKey, out binProg) ?? false;
                    }
                }
                
                if (isCached)
                {
                    using var cacheLoadProf = Engine.Profiler.Start("GLRenderProgram.Link.LoadCachedBinary");
                    _cachedProgram = binProg;
                    GLEnum format = binProg.Format;
                    PublishBackendStatus(
                        EShaderProgramBackendStage.CacheHit,
                        "BinaryCache",
                        "binary cache entry matched runtime fingerprint",
                        fingerprint: binProg.CacheKey);
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
                            HashPreviouslyFailed: false));

                        if (selection.Lane == EOpenGLProgramBuildLane.BinaryQueueBackpressure)
                        {
                            uploadQueue?.RecordBackpressure();
                            PublishBackendStatus(
                                EShaderProgramBackendStage.QueueBackpressure,
                                "BinaryUploadAsync",
                                selection.Reason,
                                fingerprint: binProg.CacheKey);
                            RegisterPendingAsyncProgram();
                            return ReturnPendingBuildResult();
                        }

                        if (selection.Lane == EOpenGLProgramBuildLane.BinaryUploadAsync && uploadQueue is not null)
                        {
                            BeginBuildTelemetry("BinaryUploadAsync", binProg.CacheKey);
                            uploadQueue.EnqueueUpload(bindingId, binProg.Binary, format, binProg.Length, Hash, binProg.CacheKey);
                            _asyncBinaryUploadPending = true;
                            PublishBackendStatus(
                                EShaderProgramBackendStage.BinaryUploadPending,
                                "BinaryUploadAsync",
                                selection.Reason,
                                fingerprint: binProg.CacheKey);
                            RegisterPendingAsyncProgram();
                            return ReturnPendingBuildResult();
                        }

                        BeginBuildTelemetry("BinaryUploadSynchronous", binProg.CacheKey);
                        long binaryStart = Stopwatch.GetTimestamp();
                        using (Engine.Profiler.Start("GLRenderProgram.Link.ProgramBinary"))
                        {
                            fixed (byte* ptr = binProg.Binary)
                                Api.ProgramBinary(bindingId, format, ptr, binProg.Length);
                        }
                        double binaryLoadMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - binaryStart);
                        var error = Api.GetError();
                        if (error != GLEnum.NoError)
                        {
                            Debug.OpenGLWarning($"Failed to load cached program binary with format {format} and hash {Hash}: {error}. Deleting from cache.");
                            PublishBackendStatus(
                                EShaderProgramBackendStage.BinaryUploadFailed,
                                "BinaryUploadSynchronous",
                                "glProgramBinary returned a GL error",
                                error.ToString(),
                                fingerprint: binProg.CacheKey);
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
                            CompleteBuildTelemetry(false, binaryLoadMilliseconds: binaryLoadMilliseconds, failureReason: binaryFailureReason);
                            DeleteFromBinaryShaderCache(binProg.CacheKey, format);
                        }
                        else
                        {
                            AdoptLinkedBuildProgram(bindingId);
                            IsLinked = true;
                            double reflectionMilliseconds = RestoreRuntimeBindingStateAfterBinaryLoad();
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Ready,
                                "BinaryUploadSynchronous",
                                "cached binary loaded synchronously",
                                fingerprint: binProg.CacheKey);
                            CompleteBuildTelemetry(true, binaryLoadMilliseconds: binaryLoadMilliseconds, reflectionMilliseconds: reflectionMilliseconds);
                            return true;
                        }
                    }
                }
                else
                {
                    PublishBackendStatus(
                        EShaderProgramBackendStage.CacheMiss,
                        "BinaryCache",
                        Engine.Rendering.Settings.AllowBinaryProgramCaching ? "binary cache miss" : "binary cache disabled",
                        fingerprint: cacheKey);
                }

                if (Failed.ContainsKey(Hash))
                {
                    PublishBackendStatus(
                        EShaderProgramBackendStage.Failed,
                        "Source",
                        "hash previously failed",
                        "Hash is marked failed.",
                        fingerprint: cacheKey);
                    MarkBuildFailed();
                    return IsLinked;
                }

                // If another GLRenderProgram with the same hash is already compiling,
                // defer until its binary lands in the cache.
                bool waitingForCompileQueue = _asyncCompileLinkQueueWaitPending;
                if (!waitingForCompileQueue && !InFlightCompilations.TryAdd(Hash, 0))
                    return ReturnPendingBuildResult();

                {
                    _cachedProgram = null;
                    if (!waitingForCompileQueue)
                    {
                        CaptureLinkRequestStackTrace();
                        Debug.OpenGL($"[ShaderCache] MISS hash={Hash}, compiling {_shaderCache.Count} shader(s) from source.");
                    }

                    var compileQueue = Renderer.ProgramCompileLinkQueue;
                    bool isKnownAsyncLinkHazard = IsKnownAsyncLinkHazard;
                    GLProgramCompileLinkQueue.ShaderInput[]? inputs = _preparedCompileInputs;
                    bool wantsSharedSourceInputs = compileQueue is { IsAvailable: true } &&
                        (Renderer.UseSharedContextProgramCompileLinkQueue ||
                         Engine.Rendering.Settings.OpenGLShaderLinkStrategy == EOpenGLShaderLinkStrategy.SharedContext);
                    if (wantsSharedSourceInputs && inputs is null && !isKnownAsyncLinkHazard)
                        inputs = PrepareCompileInputs();

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
                        HashPreviouslyFailed: false));

                    if (sourceSelection.Lane == EOpenGLProgramBuildLane.SharedContextQueueBackpressure)
                    {
                        _asyncCompileLinkQueueWaitPending = true;
                        PublishBackendStatus(
                            EShaderProgramBackendStage.QueueBackpressure,
                            "SharedContextSource",
                            sourceSelection.Reason,
                            fingerprint: cacheKey);
                        RegisterPendingAsyncProgram();
                        return ReturnPendingBuildResult();
                    }

                    if (sourceSelection.Lane == EOpenGLProgramBuildLane.SharedContextSource &&
                        compileQueue is not null &&
                        inputs is { Length: > 0 })
                    {
                        if (TryResolveUberVariantHash(inputs, out ulong queuedVariantHash))
                            BeginUberBackendCompileTracking(queuedVariantHash);

                        if (!compileQueue.TryEnqueueCompileAndLink(bindingId, inputs, out string? rejectReason))
                        {
                            Debug.OpenGLWarning($"[ShaderCache] Shared compile queue rejected hash {Hash}: {rejectReason}. Falling back to synchronous source path.");
                        }
                        else
                        {
                            BeginBuildTelemetry("SharedContextSource", cacheKey);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.SourceQueued,
                                "SharedContextSource",
                                sourceSelection.Reason,
                                fingerprint: cacheKey);
                            Debug.OpenGL($"[ShaderCache] QUEUE hash={Hash}, compiling {_shaderCache.Count} shader(s) on shared context.");
                            _asyncCompileLinkQueueWaitPending = false;
                            _asyncCompileLinkPending = true;
                            RegisterPendingAsyncProgram();
                            return ReturnPendingBuildResult();
                        }
                    }
                    _asyncCompileLinkQueueWaitPending = false;
                    if (sourceSelection.Lane == EOpenGLProgramBuildLane.SynchronousSource)
                    {
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
                    bool hazardSyncLink = isKnownAsyncLinkHazard
                        && Renderer.UseDriverParallelShaderCompile
                        && Renderer.TryDisableParallelShaderCompileForHazardousLink();

                    try
                    {

                    if (_activeBuildBackend is null)
                    {
                        string backendName = sourceSelection.Lane == EOpenGLProgramBuildLane.DriverParallelSource
                            ? "DriverParallelSource"
                            : "SynchronousSource";
                        BeginBuildTelemetry(backendName, cacheKey);
                    }

                    long sourceCompileStart = Stopwatch.GetTimestamp();
                    using (Engine.Profiler.Start("GLRenderProgram.Link.GenerateShaders"))
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
                        Failed.TryAdd(Hash, 0);
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
                    using (Engine.Profiler.Start("GLRenderProgram.Link.AttachShaders"))
                    {
                        foreach (GLShader shader in shaderCache)
                        {
                            if (shader.IsCompiled)
                            {
                                uint shaderId = shader.BindingId;
                                Api.AttachShader(bindingId, shaderId);
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
                        using var linkProf = Engine.Profiler.Start("GLRenderProgram.Link.DriverLinkProgram");
                        Api.LinkProgram(bindingId);

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

                        Api.GetProgram(bindingId, GLEnum.LinkStatus, out int status);
                        bool linked = status != 0;
                        string? linkError = null;
                        if (!linked)
                            Api.GetProgramInfoLog(bindingId, out linkError);

                        CompleteUberBackendTracking(linked, linkError, compileMilliseconds);

                        if (!linked)
                        {
                            PrintLinkDebug(bindingId);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Failed,
                                _activeBuildBackend ?? "SynchronousSource",
                                "source link failed",
                                linkError,
                                compileMilliseconds);
                            CompleteBuildTelemetry(false, compileMilliseconds, failureReason: linkError);
                            Failed.TryAdd(Hash, 0);
                            sourceBuildFailed = true;
                        }
                        else
                        {
                            AdoptLinkedBuildProgram(bindingId);
                            IsLinked = true;
                            long reflectionStart = Stopwatch.GetTimestamp();
                            using var uniformsProf = Engine.Profiler.Start("GLRenderProgram.Link.CacheActiveUniforms");
                            CacheActiveUniforms();
                            double reflectionMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - reflectionStart);
                            CacheBinary(bindingId);
                            PublishBackendStatus(
                                EShaderProgramBackendStage.Ready,
                                _activeBuildBackend ?? "SynchronousSource",
                                "source link completed",
                                compileMilliseconds: compileMilliseconds);
                            CompleteBuildTelemetry(true, compileMilliseconds, reflectionMilliseconds: reflectionMilliseconds);
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
                        Failed.TryAdd(Hash, 0);
                        sourceBuildFailed = true;
                    }

                    using (Engine.Profiler.Start("GLRenderProgram.Link.DetachShaders"))
                    {
                        DetachShaders(bindingId, [.. attachedShaderIds]);
                    }
                    using (Engine.Profiler.Start("GLRenderProgram.Link.DestroyShaderObjects"))
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
                            Renderer.RestoreParallelShaderCompile();
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
                Api.GetProgramInfoLog(bindingId, out string info);
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
