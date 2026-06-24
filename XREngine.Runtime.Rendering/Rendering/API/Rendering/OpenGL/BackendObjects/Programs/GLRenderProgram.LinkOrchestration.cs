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
                    // glCompileShader and glLinkProgram run inline on the GL thread â€”
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
                        // Shaders may still be compiling â€” enter the async state machine and return.
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

        }
    }
}
