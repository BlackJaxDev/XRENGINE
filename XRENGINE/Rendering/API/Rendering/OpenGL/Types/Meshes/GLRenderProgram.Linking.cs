using XREngine.Extensions;
using Silk.NET.OpenGL;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
            private bool _preparedIsCached;
            private BinaryProgram _preparedBinProg;
            private GLProgramCompileLinkQueue.ShaderInput[]? _preparedCompileInputs;
            private volatile int _linkPreparationPendingGeneration = -1;
            private int _linkPreparationGeneration;
            private Exception? _linkPreparationFailure;

            // Async binary upload state: set when a glProgramBinary call has been
            // dispatched to the shared context thread and we are waiting for completion.
            private volatile bool _asyncBinaryUploadPending;

            // Async compile+link state: set when shader compilation and program linking
            // have been dispatched to the shared context thread.
            private volatile bool _asyncCompileLinkPending;

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

            /// <summary>
            /// Pre-computes the shader source hash and binary cache lookup.
            /// Safe to call from any thread. The result is consumed once by the next <see cref="Link"/> call.
            /// Saves ~2-5ms of CPU work that would otherwise block the GL thread.
            /// </summary>
            public void PrepareLinkData()
            {
                if (_linkDataPrepared || IsLinked || _shaderCache.IsEmpty)
                    return;

                ulong hash;
                bool isCached = false;
                BinaryProgram binProg = default;
                GLProgramCompileLinkQueue.ShaderInput[]? compileInputs = null;
                using (Engine.Profiler.Start("GLRenderProgram.Link.CacheLookup"))
                {
                    using (Engine.Profiler.Start("GLRenderProgram.Link.CalcHash"))
                        hash = CalcShaderSourceHash();

                    if (Engine.Rendering.Settings.AllowBinaryProgramCaching)
                    {
                        using (Engine.Profiler.Start("GLRenderProgram.Link.BinaryCacheLookup"))
                            isCached = BinaryCache?.TryGetValue(hash, out binProg) ?? false;
                    }
                }

                compileInputs = PrepareCompileInputs();

                _preparedHash = hash;
                _preparedIsCached = isCached;
                _preparedBinProg = binProg;
                _preparedCompileInputs = compileInputs;
                _linkPreparationFailure = null;
                _linkDataPrepared = true;
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

            private void InvalidatePreparedLinkData()
            {
                Interlocked.Increment(ref _linkPreparationGeneration);
                Volatile.Write(ref _linkPreparationPendingGeneration, -1);
                _linkPreparationFailure = null;
                _preparedCompileInputs = null;
                _preparedHash = 0;
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

            private bool HasPendingAsyncWork
                => _asyncBinaryUploadPending || _asyncCompileLinkPending || _asyncLinkPhase != EAsyncLinkPhase.Idle;

            private void RegisterPendingAsyncProgram()
                => PendingAsyncPrograms[this] = 0;

            private void UnregisterPendingAsyncProgram()
                => PendingAsyncPrograms.TryRemove(this, out _);

            internal static void PollPendingAsyncPrograms(int maxPrograms)
            {
                int remaining = Math.Max(1, maxPrograms);
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

                    if (!program.HasPendingAsyncWork)
                        program.UnregisterPendingAsyncProgram();
                }
            }

            /// <summary>
            /// True once <see cref="Hash"/> has been computed for this instance.
            /// Avoids redundant CalcHash calls while the instance is deferring.
            /// Reset in <see cref="Reset"/>.
            /// </summary>
            private bool _hashComputed;

            /// <summary>
            /// Continues an in-progress async compile/link operation (GL_ARB_parallel_shader_compile).
            /// Called from <see cref="Link"/> when <see cref="_asyncLinkPhase"/> is not Idle.
            /// </summary>
            private bool ContinueAsyncLink()
            {
                uint bindingId = BindingId;

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
                            return false; // Still compiling — retry next frame

                        double compileMilliseconds = CompleteUberBackendCompileTracking();

                        if (anyFailed)
                        {
                            Debug.OpenGLWarning($"Failed to compile program with hash {Hash}.");
                            CompleteUberBackendTracking(false, "Backend shader compile failed.", compileMilliseconds, 0.0);
                            Failed.TryAdd(Hash, 0);
                            InFlightCompilations.TryRemove(Hash, out _);
                            CleanupAsyncLink();
                            return false;
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
                            InFlightCompilations.TryRemove(Hash, out _);
                            CleanupAsyncLink();
                            return false;
                        }

                        BeginUberBackendLinkTracking(compileMilliseconds);
                        Api.LinkProgram(bindingId);
                        _asyncLinkedProgramId = bindingId;
                        _asyncAttachedShaderIds = [.. attachedShaderIds];
                        _asyncLinkPhase = EAsyncLinkPhase.Linking;
                        RegisterPendingAsyncProgram();
                        return false; // Will poll link completion next frame
                    }
                    case EAsyncLinkPhase.Linking:
                    {
                        using var prof = Engine.Profiler.Start("GLRenderProgram.ContinueAsyncLink.PollLink");

                        uint linkedProgramId = _asyncLinkedProgramId != 0 ? _asyncLinkedProgramId : bindingId;

                        Api.GetProgram(linkedProgramId, (GLEnum)GLShader.GL_COMPLETION_STATUS_ARB, out int complete);
                        if (complete == 0)
                            return false; // Still linking — retry next frame

                        Api.GetProgram(linkedProgramId, GLEnum.LinkStatus, out int status);
                        bool linked = status != 0;
                        string? linkError = null;
                        if (linked)
                        {
                            CacheActiveUniforms();
                            CacheBinary(linkedProgramId);
                        }
                        else
                        {
                            Api.GetProgramInfoLog(linkedProgramId, out linkError);
                            PrintLinkDebug(linkedProgramId);
                        }

                        CompleteUberBackendTracking(linked, linkError);

                        IsLinked = linked;
                        InFlightCompilations.TryRemove(Hash, out _);

                        // Detach and destroy shader objects
                        if (_asyncAttachedShaderIds is not null)
                        {
                            DetachShaders(linkedProgramId, _asyncAttachedShaderIds);
                            _asyncAttachedShaderIds = null;
                        }
                        _asyncLinkedProgramId = 0;
                        _shaderCache.ForEach(x => x.Value.Destroy());
                        _asyncLinkPhase = EAsyncLinkPhase.Idle;
                        return IsLinked;
                    }
                    default:
                        return false;
                }
            }

            /// <summary>
            /// Resets async link state and destroys any attached shader objects.
            /// </summary>
            private void CleanupAsyncLink()
            {
                if (_asyncAttachedShaderIds is not null)
                {
                    uint linkedProgramId = _asyncLinkedProgramId != 0 ? _asyncLinkedProgramId : BindingId;
                    DetachShaders(linkedProgramId, _asyncAttachedShaderIds);
                    _asyncAttachedShaderIds = null;
                }
                _asyncLinkedProgramId = 0;
                _shaderCache.ForEach(x => x.Value.Destroy());
                _asyncLinkPhase = EAsyncLinkPhase.Idle;
                ResetUberBackendTracking();
                UnregisterPendingAsyncProgram();
            }

            private void ReleaseAsyncLinkState()
            {
                if (Hash != 0)
                    InFlightCompilations.TryRemove(Hash, out _);

                CleanupAsyncLink();
                _asyncBinaryUploadPending = false;
                _asyncCompileLinkPending = false;
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

            private static double StopwatchTicksToMilliseconds(long ticks)
                => ticks <= 0L ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;

            private void DetachShaders(uint programId, ReadOnlySpan<uint> attachedShaderIds)
            {
                if (programId == 0 || attachedShaderIds.Length == 0)
                    return;

                HashSet<uint> detachedShaderIds = [];
                foreach (uint shaderId in attachedShaderIds)
                {
                    if (shaderId == 0 || !detachedShaderIds.Add(shaderId))
                        continue;

                    Api.DetachShader(programId, shaderId);
                }
            }

            public bool Link(bool force = false)
            {
                using var prof = Engine.Profiler.Start("GLRenderProgram.Link");

                if (IsLinked)
                    return true;

                if (IsLinkPreparationPending)
                    return false;

                if (_linkPreparationFailure is not null)
                {
                    Debug.OpenGLWarning($"GLRenderProgram link preparation failed for '{Data.Name ?? "unnamed"}': {_linkPreparationFailure.Message}");
                    _linkPreparationFailure = null;
                }

                // Check for completed async binary upload from the shared context thread.
                if (_asyncBinaryUploadPending)
                {
                    var queue = Renderer.ProgramBinaryUploadQueue;
                    if (queue is not null && TryGetBindingId(out uint pendingId) && queue.TryGetResult(pendingId, out var asyncResult))
                    {
                        _asyncBinaryUploadPending = false;
                        if (asyncResult.Status == GLProgramBinaryUploadQueue.UploadStatus.Success)
                        {
                            IsLinked = true;
                            bool restoredMetadata;
                            using (Engine.Profiler.Start("GLRenderProgram.Link.RestoreCachedUniformMetadata"))
                                restoredMetadata = TryRestoreCachedUniformMetadata(_cachedProgram?.Uniforms);
                            if (!restoredMetadata)
                            {
                                using var uniformsProf = Engine.Profiler.Start("GLRenderProgram.Link.CacheActiveUniforms");
                                CacheActiveUniforms();
                                PromoteCurrentUniformMetadataToCachedProgram();
                            }
                            return true;
                        }
                        else
                        {
                            Debug.OpenGLWarning($"Async program binary upload failed for hash {Hash}. Falling back to source compilation.");
                            DeleteFromBinaryShaderCache(Hash, asyncResult.Format);
                            // Fall through to compile from source below.
                        }
                    }
                    else
                    {
                        return false; // Upload still in progress.
                    }
                }

                // Check for completed async compile+link from the shared context thread.
                if (_asyncCompileLinkPending)
                {
                    var compileQueue = Renderer.ProgramCompileLinkQueue;
                    if (compileQueue is not null && TryGetBindingId(out uint pendingId2) && compileQueue.TryGetResult(pendingId2, out var compileResult))
                    {
                        _asyncCompileLinkPending = false;
                        if (compileResult.Status == GLProgramCompileLinkQueue.CompileStatus.Success)
                        {
                            CompleteUberBackendTracking(true, compileMilliseconds: compileResult.CompileMilliseconds, linkMilliseconds: compileResult.LinkMilliseconds);
                            IsLinked = true;
                            using var uniformsProf = Engine.Profiler.Start("GLRenderProgram.Link.CacheActiveUniforms");
                            CacheActiveUniforms();
                            CacheBinary(pendingId2);
                            InFlightCompilations.TryRemove(Hash, out _);
                            return true;
                        }
                        else
                        {
                            CompleteUberBackendTracking(false, compileResult.ErrorLog, compileResult.CompileMilliseconds, compileResult.LinkMilliseconds);
                            string errorKind = compileResult.Status == GLProgramCompileLinkQueue.CompileStatus.CompileFailed
                                ? "compile" : "link";
                            Debug.OpenGLWarning($"Async {errorKind} failed for hash {Hash}: {compileResult.ErrorLog}");
                            Failed.TryAdd(Hash, 0);
                            InFlightCompilations.TryRemove(Hash, out _);
                            return false;
                        }
                    }
                    else
                    {
                        return false; // Compile+link still in progress.
                    }
                }

                // Resume an in-progress async compile/link if the extension is active.
                if (_asyncLinkPhase != EAsyncLinkPhase.Idle)
                    return ContinueAsyncLink();

                if (!LinkReady && !force)
                    return false;

                //if (!IsGenerated)
                //{
                //    Generate();
                //    return false;
                //}

                //if (IsLinked)
                //    return true;

                if (_shaderCache.IsEmpty/* || _shaderCache.Values.Any(x => !x.IsCompiled)*/)
                    return false;

                bool isCached;
                uint bindingId = BindingId;
                BinaryProgram binProg;

                // Use pre-computed link data if available (populated by PrepareLinkData on a job thread),
                // otherwise fall back to computing on the GL thread. Once computed,
                // _hashComputed prevents redundant CalcHash calls while deferring.
                if (_linkDataPrepared)
                {
                    Hash = _preparedHash;
                    isCached = _preparedIsCached;
                    binProg = _preparedBinProg;
                    _linkDataPrepared = false;
                    _hashComputed = true;
                }
                else
                {
                    isCached = false;
                    binProg = default;
                    if (!_hashComputed)
                    {
                        using (Engine.Profiler.Start("GLRenderProgram.Link.CacheLookup"))
                        {
                            using (Engine.Profiler.Start("GLRenderProgram.Link.CalcHash"))
                                Hash = CalcShaderSourceHash();
                        }
                        _hashComputed = true;
                    }
                    if (Engine.Rendering.Settings.AllowBinaryProgramCaching)
                    {
                        using (Engine.Profiler.Start("GLRenderProgram.Link.BinaryCacheLookup"))
                            isCached = BinaryCache?.TryGetValue(Hash, out binProg) ?? false;
                    }
                }
                    
                    if (isCached)
                    {
                        using var cacheLoadProf = Engine.Profiler.Start("GLRenderProgram.Link.LoadCachedBinary");
                        //Debug.OpenGL($"[ShaderCache] HIT hash={Hash}");
                        _cachedProgram = binProg;
                        GLEnum format = binProg.Format;
                        if (!Engine.Rendering.Stats.CanAllocateVram(binProg.Length, 0, out long projectedBytes, out long budgetBytes))
                        {
                            Debug.OpenGLWarning($"[VRAM Budget] Skipping cached program binary load for hash {Hash} ({binProg.Length} bytes). Projected={projectedBytes} bytes, Budget={budgetBytes} bytes. Deleting from cache.");
                            DeleteFromBinaryShaderCache(Hash, format);
                        }
                        else
                        {
                            // Async path: offload glProgramBinary to the shared context thread.
                            var uploadQueue = Renderer.ProgramBinaryUploadQueue;
                            if (Engine.Rendering.Settings.AsyncProgramBinaryUpload && uploadQueue is not null && uploadQueue.IsAvailable)
                            {
                                if (!uploadQueue.CanEnqueue)
                                {
                                    RegisterPendingAsyncProgram();
                                    return false;
                                }

                                uploadQueue.EnqueueUpload(bindingId, binProg.Binary, format, binProg.Length, Hash);
                                _asyncBinaryUploadPending = true;
                                RegisterPendingAsyncProgram();
                                return false;
                            }

                            // Synchronous fallback.
                            using (Engine.Profiler.Start("GLRenderProgram.Link.ProgramBinary"))
                            {
                                fixed (byte* ptr = binProg.Binary)
                                    Api.ProgramBinary(bindingId, format, ptr, binProg.Length);
                            }
                            var error = Api.GetError();
                            if (error != GLEnum.NoError)
                            {
                                Debug.OpenGLWarning($"Failed to load cached program binary with format {format} and hash {Hash}: {error}. Deleting from cache.");
                                DeleteFromBinaryShaderCache(Hash, format);
                            }
                            else
                            {
                                IsLinked = true;
                                bool restoredMetadata;
                                using (Engine.Profiler.Start("GLRenderProgram.Link.RestoreCachedUniformMetadata"))
                                    restoredMetadata = TryRestoreCachedUniformMetadata(_cachedProgram?.Uniforms);
                                if (!restoredMetadata)
                                {
                                    using var uniformsProf = Engine.Profiler.Start("GLRenderProgram.Link.CacheActiveUniforms");
                                    CacheActiveUniforms();
                                    PromoteCurrentUniformMetadataToCachedProgram();
                                }
                                return true;
                            }
                        }
                    }

                    if (Failed.ContainsKey(Hash))
                        return false;

                    // If another GLRenderProgram with the same hash is already compiling,
                    // defer until its binary lands in the cache.
                    if (!InFlightCompilations.TryAdd(Hash, 0))
                        return false;

                    {
                        _cachedProgram = null;
                        Debug.OpenGL($"[ShaderCache] MISS hash={Hash}, compiling {_shaderCache.Count} shader(s) from source.");

                        // When the shared context compile+link queue is available and the driver
                        // does NOT have GL_ARB_parallel_shader_compile, offload the entire
                        // compile → attach → link pipeline to the background thread.
                        // This keeps the main render thread free while the driver's shader compiler runs.
                        var compileQueue = Renderer.ProgramCompileLinkQueue;
                        if (Engine.Rendering.Settings.AsyncProgramCompilation
                            && !Engine.Rendering.State.HasParallelShaderCompile
                            && compileQueue is not null
                            && compileQueue.IsAvailable
                            && compileQueue.CanEnqueue)
                        {
                            GLProgramCompileLinkQueue.ShaderInput[]? inputs = _preparedCompileInputs ?? PrepareCompileInputs();
                            bool allResolved = inputs is { Length: > 0 };

                            if (allResolved)
                            {
                                if (TryResolveUberVariantHash(inputs!, out ulong queuedVariantHash))
                                    BeginUberBackendCompileTracking(queuedVariantHash);

                                compileQueue.EnqueueCompileAndLink(bindingId, inputs!);
                                _asyncCompileLinkPending = true;
                                RegisterPendingAsyncProgram();
                                return false;
                            }
                            // If source resolution failed, fall through to the synchronous path.
                        }

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

                        // When GL_ARB_parallel_shader_compile is active, CompileShader() is non-blocking.
                        // Shaders may still be compiling — enter the async state machine and return.
                        if (Engine.Rendering.State.HasParallelShaderCompile &&
                            _shaderCache.Values.Any(s => s.IsCompilePending))
                        {
                            _asyncLinkPhase = EAsyncLinkPhase.Compiling;
                            RegisterPendingAsyncProgram();
                            return false;
                        }

                        double compileMilliseconds = CompleteUberBackendCompileTracking();

                        if (_shaderCache.Values.Any(x => !x.IsCompiled))
                        {
                            Debug.OpenGLWarning($"Failed to compile program with hash {Hash}.");
                            CompleteUberBackendTracking(false, "Backend shader compile failed.", compileMilliseconds, 0.0);
                            Failed.TryAdd(Hash, 0);
                            InFlightCompilations.TryRemove(Hash, out _);
                            //TODO: return invalid material until shaders are compiled
                            return false;
                        }
                        
                        //Debug.Out($"Compiled program with hash {Hash}.");
                        var shaderCache = _shaderCache.Values;
                        List<uint> attachedShaderIds = [];
                        bool noErrors = true;
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

                            // When the extension is active, LinkProgram is also non-blocking.
                            if (Engine.Rendering.State.HasParallelShaderCompile)
                            {
                                _asyncLinkedProgramId = bindingId;
                                _asyncAttachedShaderIds = [.. attachedShaderIds];
                                _asyncLinkPhase = EAsyncLinkPhase.Linking;
                                RegisterPendingAsyncProgram();
                                return false;
                            }

                            Api.GetProgram(bindingId, GLEnum.LinkStatus, out int status);
                            bool linked = status != 0;
                            string? linkError = null;
                            if (!linked)
                                Api.GetProgramInfoLog(bindingId, out linkError);

                            CompleteUberBackendTracking(linked, linkError, compileMilliseconds);

                            if (!linked)
                                PrintLinkDebug(bindingId);
                            IsLinked = linked;
                            if (IsLinked)
                            {
                                using var uniformsProf = Engine.Profiler.Start("GLRenderProgram.Link.CacheActiveUniforms");
                                CacheActiveUniforms();
                                CacheBinary(bindingId);
                            }
                            InFlightCompilations.TryRemove(Hash, out _);
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
                        if (!IsLinked)
                            InFlightCompilations.TryRemove(Hash, out _);
                        return IsLinked;
                    }
            }

            private void Value_SourceChanged()
            {
                InvalidatePreparedLinkData();

                //If the source of a shader changes, we need to relink the program.
                //This will cause the program to be destroyed and recreated.
                if (IsLinked)
                    Relink();
            }

            private void Relink()
            {
                if (Engine.InvokeOnMainThread(Relink, "GLRenderProgram.Relink"))
                    return;

                //Programs can't be relinked; destroy and recreate.
                Destroy();
                Generate();
                BeginPrepareLinkData();
                Link();
            }

            private void PrintLinkDebug(uint bindingId)
            {
                Api.GetProgramInfoLog(bindingId, out string info);
                Debug.OpenGL(string.IsNullOrWhiteSpace(info)
                    ? "Unable to link program, but no error was returned."
                    : info);

                //if (info.Contains("Vertex info"))
                //{
                //    RenderShader s = _shaders.FirstOrDefault(x => x.File.Type == EShaderMode.Vertex);
                //    string source = s.GetSource(true);
                //    Engine.PrintLine(source);
                //}
                //else if (info.Contains("Geometry info"))
                //{
                //    RenderShader s = _shaders.FirstOrDefault(x => x.File.Type == EShaderMode.Geometry);
                //    string source = s.GetSource(true);
                //    Engine.PrintLine(source);
                //}
                //else if (info.Contains("Fragment info"))
                //{
                //    RenderShader s = _shaders.FirstOrDefault(x => x.File.Type == EShaderMode.Fragment);
                //    string source = s.GetSource(true);
                //    Engine.PrintLine(source);
                //}
            }
        }
    }
}
